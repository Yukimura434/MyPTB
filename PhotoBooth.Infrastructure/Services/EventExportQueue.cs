using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class EventExportQueue : IEventExportQueue,IDisposable
    {
        readonly IEventExportJobRepository jobs;
        readonly IDeliverableService deliverables;
        readonly ApplicationOptions options;
        readonly ILogger<EventExportQueue> log;
        readonly SemaphoreSlim wake=new SemaphoreSlim(0,1);
        readonly object sync=new object();
        CancellationTokenSource cancellation;
        Task runner;

        public EventExportQueue(IEventExportJobRepository jobRepository,IDeliverableService deliverableService,ApplicationOptions applicationOptions,ILogger<EventExportQueue> logger)
        {jobs=jobRepository;deliverables=deliverableService;options=applicationOptions;log=logger;}

        public async Task EnqueueAsync(PhotoEvent photoEvent,BoothSession boothSession,Deliverable deliverable,CancellationToken token)
        {
            if(photoEvent==null||photoEvent.Id==Guid.Empty||string.IsNullOrWhiteSpace(photoEvent.OutputDirectory))throw new ArgumentException("Sự kiện chưa có thư mục bàn giao hợp lệ.",nameof(photoEvent));
            if(boothSession==null||boothSession.Id==Guid.Empty||boothSession.EventId!=photoEvent.Id)throw new ArgumentException("Lượt chụp không thuộc sự kiện cần xuất.",nameof(boothSession));
            if(deliverable==null||string.IsNullOrWhiteSpace(deliverable.Id)||deliverable.BoothSessionId!=boothSession.Id)throw new ArgumentException("Bộ thành phẩm không thuộc lượt chụp cần xuất.",nameof(deliverable));
            var destination=Path.GetFullPath(photoEvent.OutputDirectory);
            var folder=SanitizeFolderName(string.IsNullOrWhiteSpace(boothSession.DisplayCode)?boothSession.Id.ToString("N"):boothSession.DisplayCode);
            var stored=await jobs.CreateIntentAsync(new EventExportJobRecord
            {
                Id=Guid.NewGuid().ToString("N"),EventId=photoEvent.Id,SessionId=boothSession.Id,DeliverableId=deliverable.Id,
                DestinationRoot=destination,TurnFolderName=folder,
                IdempotencyKey="event-export:"+photoEvent.Id.ToString("N")+":"+boothSession.Id.ToString("N")+":"+deliverable.Id,
                CreatedAtUtc=DateTime.UtcNow
            },token).ConfigureAwait(false);
            if(!string.Equals(stored.State,DurableOutputJobStates.Completed,StringComparison.Ordinal))Signal();
        }

        public Task StartAsync(CancellationToken token)
        {
            lock(sync){if(runner!=null)return Task.CompletedTask;cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);runner=Task.Run(()=>RunAsync(cancellation.Token));}
            Signal();return Task.CompletedTask;
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
                EventExportJobRecord job=null;
                try
                {
                    job=await jobs.LeaseNextDueAsync(leaseId,DateTime.UtcNow.AddMinutes(10),token).ConfigureAwait(false);
                    if(job==null){await wake.WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false);continue;}
                    var exportedPath=await ExportAsync(job,token).ConfigureAwait(false);
                    await jobs.CompleteAsync(job.Id,exportedPath,token).ConfigureAwait(false);
                    log?.LogInformation("Completed durable Event export {ExportJobId} to {ExportedPath}.",job.Id,exportedPath);
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                catch(Exception exception)
                {
                    var safe="Không thể xuất ảnh vào thư mục sự kiện. Ứng dụng sẽ tự thử lại.";
                    log?.LogWarning(exception,"Event export {ExportJobId} failed on attempt {AttemptCount}.",job?.Id,job?.AttemptCount??0);
                    if(job!=null)try
                    {
                        var limit=Math.Max(1,options.UploadMaxRetries);
                        if(job.AttemptCount>=limit)await jobs.FailAsync(job.Id,"Không thể xuất ảnh sau nhiều lần thử. Ảnh gốc vẫn được giữ an toàn trong ứng dụng.",CancellationToken.None).ConfigureAwait(false);
                        else await jobs.ScheduleRetryAsync(job.Id,DateTime.UtcNow.AddSeconds(Math.Min(300,Math.Pow(2,Math.Max(1,job.AttemptCount)))),safe,CancellationToken.None).ConfigureAwait(false);
                    }
                    catch(Exception updateError){log?.LogError(updateError,"Could not persist Event export failure state for {ExportJobId}.",job?.Id);}
                }
            }
        }

        async Task<string> ExportAsync(EventExportJobRecord job,CancellationToken token)
        {
            var deliverable=await deliverables.GetAsync(job.SessionId,job.DeliverableId,token).ConfigureAwait(false);
            if(deliverable==null)throw new InvalidOperationException("Không tìm thấy bộ thành phẩm cần xuất.");
            var assets=RemoteDeliveryAssetPolicy.Select(deliverable.Assets).Where(value=>value!=null).ToList();
            if(assets.Count==0)throw new InvalidOperationException("Bộ thành phẩm chưa có ảnh hợp lệ để xuất.");
            var root=Path.GetFullPath(job.DestinationRoot);
            var target=Path.GetFullPath(Path.Combine(root,job.TurnFolderName));
            var prefix=root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Thư mục xuất ảnh nằm ngoài phạm vi sự kiện.");
            return await Task.Run(()=>
            {
                token.ThrowIfCancellationRequested();Directory.CreateDirectory(target);
                var counters=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                foreach(var asset in assets)
                {
                    token.ThrowIfCancellationRequested();
                    if(string.IsNullOrWhiteSpace(asset.LocalPath)||!File.Exists(asset.LocalPath))throw new FileNotFoundException("Không tìm thấy tệp nguồn cần xuất.",asset.LocalPath);
                    var source=Path.GetFullPath(asset.LocalPath);var info=new FileInfo(source);
                    if(asset.FileLength>0&&info.Length!=asset.FileLength)throw new InvalidDataException("Kích thước tệp nguồn không còn khớp dữ liệu đã ghi nhận.");
                    var sourceHash=Sha256(source);if(!string.IsNullOrWhiteSpace(asset.ContentHashSha256)&&!string.Equals(sourceHash,asset.ContentHashSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Checksum tệp nguồn không còn khớp dữ liệu đã ghi nhận.");
                    var label=RoleLabel(asset.Role);int ordinal;counters.TryGetValue(label,out ordinal);ordinal++;counters[label]=ordinal;
                    var extension=Path.GetExtension(source);var destination=Path.Combine(target,ordinal.ToString("00")+"-"+label+extension.ToLowerInvariant());
                    CopyAtomically(source,destination,sourceHash,token);
                }
                return target;
            },token).ConfigureAwait(false);
        }

        static void CopyAtomically(string source,string destination,string expectedHash,CancellationToken token)
        {
            if(File.Exists(destination)&&string.Equals(Sha256(destination),expectedHash,StringComparison.OrdinalIgnoreCase))return;
            var temporary=destination+".partial-"+Guid.NewGuid().ToString("N");
            try
            {
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.SequentialScan))
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.WriteThrough))
                {input.CopyTo(output);output.Flush(true);token.ThrowIfCancellationRequested();}
                if(!string.Equals(Sha256(temporary),expectedHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Checksum tệp xuất không khớp tệp nguồn.");
                if(File.Exists(destination))File.Delete(destination);File.Move(temporary,destination);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        static string Sha256(string path){using(var hash=SHA256.Create())using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.SequentialScan))return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();}
        static string RoleLabel(string role)
        {
            if(string.Equals(role,DeliverableAssetRoles.OriginalPicture,StringComparison.OrdinalIgnoreCase))return "anh-goc";
            if(string.Equals(role,DeliverableAssetRoles.FinalComposite,StringComparison.OrdinalIgnoreCase))return "anh-thanh-pham";
            if(string.Equals(role,DeliverableAssetRoles.Gif,StringComparison.OrdinalIgnoreCase))return "anh-dong";
            if(string.Equals(role,DeliverableAssetRoles.CompositeVideo,StringComparison.OrdinalIgnoreCase))return "video-thanh-pham";
            return "tep-thanh-pham";
        }
        static string SanitizeFolderName(string value){foreach(var character in Path.GetInvalidFileNameChars())value=value.Replace(character,'-');value=value.Trim().TrimEnd('.');return string.IsNullOrWhiteSpace(value)?Guid.NewGuid().ToString("N"):value;}
        void Signal(){if(wake.CurrentCount==0)try{wake.Release();}catch(SemaphoreFullException){}}
        public void Dispose()
        {
            Task pending; CancellationTokenSource stopped;
            lock(sync) { pending=runner; stopped=cancellation; }
            try { stopped?.Cancel(); } catch(ObjectDisposedException) { }
            if(pending == null || pending.IsCompleted) { stopped?.Dispose(); wake.Dispose(); }
            else pending.ContinueWith(completed => { var observed=completed.Exception; stopped?.Dispose(); wake.Dispose(); }, TaskScheduler.Default);
        }
    }
}
