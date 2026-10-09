using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class RemoteDeliveryQueue : IRemoteDeliveryQueue,IDisposable
    {
        const int UploadWorkerCount=3;
        readonly IDurableOutputJobRepository jobs;readonly IMediaAssetRepository assets;readonly IMediaAssetReplicaRepository replicas;readonly IGalleryLinkCacheRepository galleries;readonly IDeliverableService deliverables;readonly IRemoteMediaDeliveryClient client;readonly IPcSessionContext sessions;readonly ApplicationOptions options;readonly ILogger<RemoteDeliveryQueue> log;
        readonly SemaphoreSlim wake=new SemaphoreSlim(0,UploadWorkerCount);readonly object sync=new object();CancellationTokenSource cancellation;Task runner;
        public RemoteDeliveryQueue(IDurableOutputJobRepository jobRepository,IMediaAssetRepository assetRepository,IMediaAssetReplicaRepository replicaRepository,IGalleryLinkCacheRepository galleryRepository,IDeliverableService deliverableService,IRemoteMediaDeliveryClient deliveryClient,IPcSessionContext sessionContext,ApplicationOptions applicationOptions,ILogger<RemoteDeliveryQueue> logger){jobs=jobRepository;assets=assetRepository;replicas=replicaRepository;galleries=galleryRepository;deliverables=deliverableService;client=deliveryClient;sessions=sessionContext;options=applicationOptions;log=logger;}
        public event EventHandler<RemoteGalleryReadyEventArgs> GalleryReady;
        public event EventHandler<RemoteDeliveryFailedEventArgs> DeliveryFailed;
        readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> preparedGrants = new System.Collections.Concurrent.ConcurrentDictionary<string,string>();
        public async Task<RemoteGalleryResult> PrepareGalleryAsync(Guid boothSessionId,string deliverableId,CancellationToken token)
        {
            var result=await client.PrepareGalleryAsync(boothSessionId,deliverableId,token).ConfigureAwait(false);
            if(result.Succeeded && result.DeliveryToken?.StartsWith("du3.",StringComparison.Ordinal)==true)
                preparedGrants[deliverableId]=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(result.DeliveryToken),Encoding.UTF8.GetBytes("Miu.CaptureDelivery.v1"),DataProtectionScope.CurrentUser));
            return result;
        }

        public async Task EnqueueAsync(Deliverable deliverable,CancellationToken token)
        {
            if(deliverable==null||string.IsNullOrWhiteSpace(deliverable.Id)||deliverable.BoothSessionId==Guid.Empty)throw new ArgumentException("Deliverable identity is required.",nameof(deliverable));
            var selected=RemoteDeliveryAssetPolicy.Select(deliverable.Assets);
            if(selected.Count==0)throw new InvalidOperationException("Deliverable has no asset eligible for remote delivery.");
            // Durable media is retained, but bearer sessions must not resurrect a
            // booth-code login after process death. Re-authenticate the actor first.
            var hasPendingWork=false;preparedGrants.TryGetValue(deliverable.Id,out var protectedAuthorization);
            var authorityScope=protectedAuthorization==null?AuthorityScope(sessions.Current):"delivery:"+deliverable.BoothSessionId.ToString("N");
            foreach(var asset in selected)
            {
                var stored=await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=deliverable.BoothSessionId,DeliverableId=deliverable.Id,AssetId=asset.Id,JobType=DurableOutputJobTypes.UploadAsset,IdempotencyKey="delivery:"+deliverable.Id+":asset:"+asset.Id,ProtectedAuthorization=protectedAuthorization,AuthorizationScope=authorityScope,CreatedAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
                var pending=!string.Equals(stored.State,DurableOutputJobStates.Completed,StringComparison.Ordinal);hasPendingWork|=pending;
                if(pending)
                {
                    var current=await replicas.GetByAssetAsync(asset.Id,token).ConfigureAwait(false);
                    if(!current.Any(value=>(value.Provider==MediaReplicaProviders.SystemLibrary||value.Provider==MediaReplicaProviders.GoogleDrive)&&value.State==MediaReplicaStates.Verified))
                        await replicas.UpsertAsync(new MediaAssetReplicaRecord{Id="rep-"+Guid.NewGuid().ToString("N"),AssetId=asset.Id,Provider=MediaReplicaProviders.Online,IsRequired=true,PolicyReason="SessionDelivery",State=MediaReplicaStates.Queued,ContentHashSha256=asset.ContentHashSha256,FileLength=asset.FileLength,CreatedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
                }
            }
            var commit=await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=deliverable.BoothSessionId,DeliverableId=deliverable.Id,JobType=DurableOutputJobTypes.CommitDeliverable,IdempotencyKey="delivery:"+deliverable.Id+":commit",ProtectedAuthorization=protectedAuthorization,AuthorizationScope=authorityScope,CreatedAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
            var gallery=await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=deliverable.BoothSessionId,DeliverableId=deliverable.Id,JobType=DurableOutputJobTypes.CreateGallery,IdempotencyKey="delivery:"+deliverable.Id+":gallery",ProtectedAuthorization=protectedAuthorization,AuthorizationScope=authorityScope,CreatedAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
            hasPendingWork|=!string.Equals(commit.State,DurableOutputJobStates.Completed,StringComparison.Ordinal)||!string.Equals(gallery.State,DurableOutputJobStates.Completed,StringComparison.Ordinal);
            preparedGrants.TryRemove(deliverable.Id,out _);
            if(hasPendingWork)await deliverables.UpdateRemoteStateAsync(deliverable.Id,"QueuedForUpload",null,null,token).ConfigureAwait(false);Signal();
        }

        public Task StartAsync(CancellationToken token)
        {
            lock(sync){if(runner!=null)return Task.CompletedTask;cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);runner=Task.WhenAll(Enumerable.Range(0,UploadWorkerCount).Select(_=>Task.Run(()=>RunAsync(cancellation.Token))));}Signal();return Task.CompletedTask;
        }
        public async Task<int> RetryPendingAsync(string deliverableId,CancellationToken token)
        {
            if(!string.IsNullOrWhiteSpace(deliverableId))
            {
                var deliverable=await deliverables.GetAsync(deliverableId,token).ConfigureAwait(false);
                if(deliverable!=null)await EnqueueAsync(deliverable,token).ConfigureAwait(false);
            }
            var count=await jobs.RetryPendingAsync(deliverableId,token).ConfigureAwait(false);Signal();return count;
        }
        public async Task StopAsync(CancellationToken token)
        {
            Task pending;lock(sync){if(runner==null)return;cancellation.Cancel();pending=runner;}Signal();
            var completed=await Task.WhenAny(pending,Task.Delay(Timeout.Infinite,token)).ConfigureAwait(false);if(completed!=pending)token.ThrowIfCancellationRequested();
            lock(sync){runner=null;cancellation.Dispose();cancellation=null;}
        }
        async Task RunAsync(CancellationToken token)
        {
            var leaseId=Guid.NewGuid().ToString("N");
            while(!token.IsCancellationRequested)
            {
                DurableOutputJobRecord job=null;
                try
                {
                    var scope=AuthorityScope(sessions.Current);
                    // Durable delivery grants are independent of the current customer login.
                    job=await jobs.LeaseNextDueAsync(leaseId,DateTime.UtcNow.AddMinutes(10),token,scope??"delivery-only").ConfigureAwait(false);
                    if(job==null){await wake.WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false);continue;}
                    await ProcessAsync(job,token).ConfigureAwait(false);
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                catch(Exception exception)
                {
                    log?.LogWarning(exception,"Remote delivery worker recovered from an unexpected local error.");
                    if(job!=null)try{await jobs.ScheduleRetryAsync(job.Id,DateTime.UtcNow.AddSeconds(30),"Không thể tiếp tục tác vụ tải ảnh. Ứng dụng sẽ tự thử lại.",token).ConfigureAwait(false);}catch{}
                    try{await Task.Delay(TimeSpan.FromSeconds(5),token).ConfigureAwait(false);}catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                }
            }
        }
        async Task ProcessAsync(DurableOutputJobRecord job,CancellationToken token)
        {
            await jobs.SetStateAsync(job.Id,DurableOutputJobStates.Submitting,null,token).ConfigureAwait(false);job.AttemptCount++;
            RemoteDeliveryOperationResult result;var current=sessions.Current;var authorization=current!=null&&!current.IsExpired?current.RemoteSessionToken:null;MediaAssetRecord uploadAsset=null;string trackedReplicaProvider=null;
            var hasGrant=job.AuthorizationScope=="delivery:"+job.SessionId.ToString("N") && !string.IsNullOrWhiteSpace(job.ProtectedAuthorization);
            if(hasGrant)
            {
                try { authorization=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(job.ProtectedAuthorization),Encoding.UTF8.GetBytes("Miu.CaptureDelivery.v1"),DataProtectionScope.CurrentUser)); }
                catch { await jobs.FailAsync(job.Id,"Không thể đọc quyền tải ảnh. Vui lòng đăng nhập lại để tiếp tục.",token).ConfigureAwait(false); return; }
                if(!authorization.StartsWith("du3.",StringComparison.Ordinal)){await jobs.FailAsync(job.Id,"Quyền tải ảnh không hợp lệ.",token).ConfigureAwait(false);return;}
            }
            if(string.IsNullOrWhiteSpace(authorization)||(!hasGrant && job.AuthorizationScope!=AuthorityScope(current)))
            {
                await jobs.ScheduleRetryAsync(job.Id,DateTime.UtcNow.AddSeconds(30),"Ảnh đang chờ đăng nhập để tiếp tục tải.",token).ConfigureAwait(false);
                return;
            }
            try
            {
                if(job.JobType==DurableOutputJobTypes.UploadAsset)
                {
                    uploadAsset=await assets.GetAsync(job.AssetId,token).ConfigureAwait(false);trackedReplicaProvider=await TrackedReplicaProviderAsync(job.AssetId,token).ConfigureAwait(false);
                    if(trackedReplicaProvider!=null)await replicas.SetStateAsync(job.AssetId,trackedReplicaProvider,MediaReplicaStates.Uploading,null,null,null,null,token).ConfigureAwait(false);
                    result=await client.UploadAssetAsync(job.SessionId,job.DeliverableId,uploadAsset,job.IdempotencyKey,authorization,token).ConfigureAwait(false);
                }
                else if(job.JobType==DurableOutputJobTypes.CommitDeliverable)
                {
                    var cached=await galleries.GetAsync(job.DeliverableId,token).ConfigureAwait(false);
                    result=cached?.PublicUrl!=null&&cached.ExpiresAtUtc>DateTime.UtcNow
                        ?new RemoteGalleryResult{Succeeded=true,GalleryUrl=cached.PublicUrl,ExpiresAtUtc=cached.ExpiresAtUtc}
                        :await client.CommitDeliverableAsync(job.SessionId,job.DeliverableId,job.IdempotencyKey,authorization,token).ConfigureAwait(false);
                }
                else if(job.JobType==DurableOutputJobTypes.CreateGallery)
                {
                    var cached=await galleries.GetAsync(job.DeliverableId,token).ConfigureAwait(false);
                    result=cached?.PublicUrl!=null&&cached.ExpiresAtUtc>DateTime.UtcNow
                        ?new RemoteGalleryResult{Succeeded=true,GalleryUrl=cached.PublicUrl,ExpiresAtUtc=cached.ExpiresAtUtc}
                        :await client.CreateGalleryAsync(job.SessionId,job.DeliverableId,job.IdempotencyKey,authorization,token).ConfigureAwait(false);
                }
                else{await jobs.FailAsync(job.Id,"Tác vụ tải ảnh không được hỗ trợ.",token).ConfigureAwait(false);return;}
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception){result=new RemoteDeliveryOperationResult{Succeeded=false,Retryable=true,ErrorCode="local_delivery_error",ErrorMessage="Không thể tải ảnh lên thư viện. Ứng dụng sẽ tự thử lại."};}

            if(!result.Succeeded){await HandleFailureAsync(job,result,token).ConfigureAwait(false);return;}
            if(job.JobType==DurableOutputJobTypes.CreateGallery)
            {
                var gallery=(RemoteGalleryResult)result;var expires=gallery.ExpiresAtUtc??DateTime.UtcNow.AddDays(30);
                await galleries.SaveAsync(new GalleryLinkCacheRecord{DeliverableId=job.DeliverableId,PublicUrl=gallery.GalleryUrl,ExpiresAtUtc=expires,ReceivedAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
                await deliverables.UpdateSharePathAsync(job.DeliverableId,gallery.GalleryUrl.AbsoluteUri,token).ConfigureAwait(false);
                await deliverables.UpdateRemoteStateAsync(job.DeliverableId,"GalleryReady",null,DateTime.UtcNow,token).ConfigureAwait(false);
                try{GalleryReady?.Invoke(this,new RemoteGalleryReadyEventArgs{DeliverableId=job.DeliverableId,GalleryUrl=gallery.GalleryUrl,ExpiresAtUtc=expires});}catch(Exception exception){log?.LogWarning(exception,"A gallery-ready observer failed; remote delivery remains completed.");}
            }
            else if(job.JobType==DurableOutputJobTypes.CommitDeliverable)
            {
                if(result is RemoteGalleryResult completed && completed.GalleryUrl!=null && completed.ExpiresAtUtc.HasValue)
                    await galleries.SaveAsync(new GalleryLinkCacheRecord{DeliverableId=job.DeliverableId,PublicUrl=completed.GalleryUrl,ExpiresAtUtc=completed.ExpiresAtUtc.Value,ReceivedAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
                await deliverables.UpdateRemoteStateAsync(job.DeliverableId,"Uploaded",null,DateTime.UtcNow,token).ConfigureAwait(false);
            }
            else
            {
                var concrete=NormalizeReplicaProvider(result.ReplicaProvider);
                if(concrete==MediaReplicaProviders.SystemLibrary||concrete==MediaReplicaProviders.GoogleDrive)
                    await replicas.ReplaceOnlineProviderAsync(job.AssetId,concrete,MediaReplicaStates.Verified,result.ExternalReference,uploadAsset?.ContentHashSha256,uploadAsset?.FileLength??0,DateTime.UtcNow,token).ConfigureAwait(false);
                else if(trackedReplicaProvider!=null)
                    await replicas.SetStateAsync(job.AssetId,trackedReplicaProvider,MediaReplicaStates.Verified,result.ExternalReference,null,null,null,token).ConfigureAwait(false);
                await deliverables.UpdateRemoteStateAsync(job.DeliverableId,"Uploading",null,null,token).ConfigureAwait(false);
            }
            await jobs.CompleteAsync(job.Id,result.ExternalReference,token).ConfigureAwait(false);
        }
        async Task HandleFailureAsync(DurableOutputJobRecord job,RemoteDeliveryOperationResult result,CancellationToken token)
        {
            var safe=result.ErrorMessage??"Không thể tải ảnh lên thư viện.";if(safe.Length>1000)safe=safe.Substring(0,1000);
            if(result.Retryable && (job.AuthorizationScope?.StartsWith("delivery:",StringComparison.Ordinal)==true || job.AttemptCount<=Math.Max(1,options.UploadMaxRetries)))
            {
                var blocked=string.Equals(result.ErrorCode,"storage_provider_not_configured",StringComparison.Ordinal)||string.Equals(result.ErrorCode,"gallery_not_configured",StringComparison.Ordinal)||string.Equals(result.ErrorCode,"device_not_enrolled",StringComparison.Ordinal);
                var delay=blocked?TimeSpan.FromMinutes(30):TimeSpan.FromSeconds(Math.Min(300,Math.Pow(2,Math.Min(Math.Max(1,options.UploadMaxRetries),Math.Max(1,job.AttemptCount))))+RandomDelay());
                if(job.JobType==DurableOutputJobTypes.UploadAsset)await RecordReplicaFailureAsync(job,result,DateTime.UtcNow.Add(delay),token).ConfigureAwait(false);
                await jobs.ScheduleRetryAsync(job.Id,DateTime.UtcNow.Add(delay),safe,token).ConfigureAwait(false);await deliverables.UpdateRemoteStateAsync(job.DeliverableId,"RetryWaiting",safe,null,token).ConfigureAwait(false);
                log?.LogWarning("Remote delivery job {JobId} will retry after {DelaySeconds} seconds ({ErrorCode}).",job.Id,(int)delay.TotalSeconds,result.ErrorCode);return;
            }
            if(job.JobType==DurableOutputJobTypes.UploadAsset)await RecordReplicaFailureAsync(job,result,null,token).ConfigureAwait(false);
            await jobs.FailAsync(job.Id,safe,token).ConfigureAwait(false);await jobs.FailPendingDependentsAsync(job.DeliverableId,safe,token).ConfigureAwait(false);await deliverables.UpdateRemoteStateAsync(job.DeliverableId,"Failed",safe,null,token).ConfigureAwait(false);
            try{DeliveryFailed?.Invoke(this,new RemoteDeliveryFailedEventArgs{DeliverableId=job.DeliverableId,ErrorMessage=safe});}catch(Exception exception){log?.LogWarning(exception,"A delivery-failed observer failed.");}
            log?.LogError("Remote delivery job {JobId} stopped with {ErrorCode}; local capture remains available.",job.Id,result.ErrorCode);
        }
        async Task RecordReplicaFailureAsync(DurableOutputJobRecord job,RemoteDeliveryOperationResult result,DateTime? next,CancellationToken token)
        {
            var provider=NormalizeReplicaProvider(result.ReplicaProvider);var current=await TrackedReplicaProviderAsync(job.AssetId,token).ConfigureAwait(false);var asset=await assets.GetAsync(job.AssetId,token).ConfigureAwait(false);
            if((provider==MediaReplicaProviders.SystemLibrary||provider==MediaReplicaProviders.GoogleDrive)&&current==MediaReplicaProviders.Online)
            {
                await replicas.ReplaceOnlineProviderAsync(job.AssetId,provider,MediaReplicaStates.Failed,result.ExternalReference,asset?.ContentHashSha256,asset?.FileLength??0,null,token).ConfigureAwait(false);current=provider;
            }
            if(current!=null)await replicas.SetStateAsync(job.AssetId,current,MediaReplicaStates.Failed,result.ExternalReference,next,result.ErrorCode,result.ErrorMessage,token).ConfigureAwait(false);
        }
        async Task<string> TrackedReplicaProviderAsync(string assetId,CancellationToken token)
        {
            var values=await replicas.GetByAssetAsync(assetId,token).ConfigureAwait(false);
            return values.Where(value=>value.Provider!=MediaReplicaProviders.Local&&value.State!=MediaReplicaStates.Deleted)
                .OrderBy(value=>value.Provider==MediaReplicaProviders.Online?0:1).Select(value=>value.Provider).FirstOrDefault();
        }
        static string NormalizeReplicaProvider(string value)
        {
            if(value==MediaReplicaProviders.SystemLibrary||value==MediaReplicaProviders.GoogleDrive||value==MediaReplicaProviders.Online)return value;
            return MediaReplicaProviders.FromApi(value);
        }
        static int RandomDelay()=>Math.Abs(Guid.NewGuid().GetHashCode()%5);
        internal static string AuthorityScope(PcSession session)
        {
            if(session==null||session.IsExpired||string.IsNullOrWhiteSpace(session.RemoteSessionToken))return null;
            var authorization=session.EventAuthorization;
            if(authorization==null)return "employee:"+session.OrganizationId+":"+session.AccountId;
            if(!authorization.IsEvent)return authorization.CodeType+":"+authorization.AccessCodeId;
            using(var sha=SHA256.Create())return "event:"+Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(session.RemoteSessionToken)));
        }
        void Signal()
        {
            for(var index=wake.CurrentCount;index<UploadWorkerCount;index++)try{wake.Release();}catch(SemaphoreFullException){break;}
        }
        public void Dispose(){try{cancellation?.Cancel();runner?.GetAwaiter().GetResult();}catch{}cancellation?.Dispose();wake.Dispose();}
    }
}
