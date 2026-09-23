using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class RemoteMediaDeliveryClient : IRemoteMediaDeliveryClient
    {
        static readonly HttpClient Http = new HttpClient();
        readonly IDeviceIdentityRepository identities;
        readonly IPcSessionContext sessions;
        readonly ISessionRepository localSessions;
        readonly IStorageManager storage;
        readonly ApplicationOptions options;
        long storageBlockedUntilUtcTicks;

        public RemoteMediaDeliveryClient(IDeviceIdentityRepository identityRepository,IPcSessionContext sessionContext,ISessionRepository sessionRepository,IStorageManager storageManager,ApplicationOptions applicationOptions)
        { identities=identityRepository;sessions=sessionContext;localSessions=sessionRepository;storage=storageManager;options=applicationOptions; }

        public async Task<RemoteDeliveryOperationResult> UploadAssetAsync(Guid boothSessionId,string deliverableId,MediaAssetRecord asset,string idempotencyKey,CancellationToken token)
        {
            if(IsStorageBlocked())return Failure("storage_provider_not_configured","Object storage is not configured.",true);
            if(asset==null)return Failure("asset_missing","Local media metadata was not found.",false);
            string fullPath;
            try{fullPath=storage.GetFullPath(asset.RelativePath);}catch(Exception){return Failure("asset_path_invalid","Local media path is invalid.",false);}
            if(!File.Exists(fullPath))return Failure("asset_missing","Local media file is no longer available.",false);
            var info=new FileInfo(fullPath);if(info.Length!=asset.FileLength)return Failure("asset_changed","Local media size changed after capture.",false);
            var hash=await HashAsync(fullPath,token).ConfigureAwait(false);
            if(!string.Equals(hash,asset.ContentHashSha256,StringComparison.OrdinalIgnoreCase))return Failure("asset_changed","Local media checksum changed after capture.",false);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return Failure("device_not_enrolled","Device enrollment is required before cloud delivery.",true);
            var employee=EmployeeCredential();if(employee==null)return Failure("employee_session_required","Employee must sign in before cloud delivery.",true);
            var remoteSession=await EnsureBoothSessionAsync(boothSessionId,credential,employee,token).ConfigureAwait(false);if(!remoteSession.Operation.Succeeded)return remoteSession.Operation;
            var reserve=new ReserveRequest{BoothSessionId=boothSessionId.ToString("N"),AssetType=AssetType(asset.Kind),ContentType=asset.MimeType,Bytes=asset.FileLength,Sha256=hash};
            var reserveResult=await SendJsonAsync<ReserveRequest,ReserveResponse>(HttpMethod.Post,Api("api/v3/device/media/reserve"),credential,employee,idempotencyKey,reserve,token).ConfigureAwait(false);
            if(!reserveResult.Operation.Succeeded)return reserveResult.Operation;
            if(string.Equals(reserveResult.Value?.Status,"committed",StringComparison.OrdinalIgnoreCase)||string.Equals(reserveResult.Value?.Status,"ready",StringComparison.OrdinalIgnoreCase))return Success(asset.Id);
            if(reserveResult.Value==null||string.IsNullOrWhiteSpace(reserveResult.Value.UploadId)||string.IsNullOrWhiteSpace(reserveResult.Value.UploadUrl))return Failure("invalid_reservation","Worker returned an incomplete upload reservation.",true);

            var uploadUri=AbsoluteApiUri(reserveResult.Value.UploadUrl);
            using(var file=new FileStream(fullPath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous|FileOptions.SequentialScan))
            using(var content=new StreamContent(file,81920))
            using(var request=new HttpRequestMessage(HttpMethod.Put,uploadUri))
            {
                content.Headers.ContentType=MediaTypeHeaderValue.Parse(asset.MimeType);content.Headers.ContentLength=asset.FileLength;request.Content=content;
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);request.Headers.TryAddWithoutValidation("X-Content-Sha256",hash);request.Headers.TryAddWithoutValidation("X-Request-Id",RequestId());
                request.Headers.TryAddWithoutValidation("X-Employee-Session",employee);
                var uploadResult=await SendAsync(request,token).ConfigureAwait(false);if(!uploadResult.Succeeded)return uploadResult;
            }

            var commit=new CommitUploadRequest{UploadId=reserveResult.Value.UploadId,Bytes=asset.FileLength,Sha256=hash};
            var commitResult=await SendJsonAsync<CommitUploadRequest,StatusResponse>(HttpMethod.Post,Api($"api/v3/device/media/{Uri.EscapeDataString(reserveResult.Value.UploadId)}/commit"),credential,employee,idempotencyKey+":commit",commit,token).ConfigureAwait(false);
            return commitResult.Operation.Succeeded?Success(asset.Id):commitResult.Operation;
        }

        public async Task<RemoteDeliveryOperationResult> CommitDeliverableAsync(Guid boothSessionId,string deliverableId,string idempotencyKey,CancellationToken token)
        {
            if(IsStorageBlocked())return Failure("storage_provider_not_configured","Object storage is not configured.",true);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return Failure("device_not_enrolled","Device enrollment is required before cloud delivery.",true);
            var employee=EmployeeCredential();if(employee==null)return Failure("employee_session_required","Employee must sign in before cloud delivery.",true);
            var result=await SendJsonAsync<EmptyRequest,StatusResponse>(HttpMethod.Post,Api($"api/v3/device/booth-sessions/{boothSessionId:N}/complete"),credential,employee,idempotencyKey,new EmptyRequest(),token).ConfigureAwait(false);
            return result.Operation;
        }

        public async Task<RemoteGalleryResult> CreateGalleryAsync(Guid boothSessionId,string deliverableId,string idempotencyKey,CancellationToken token)
        {
            if(IsStorageBlocked())return GalleryFailure("storage_provider_not_configured","Object storage is not configured.",true);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return GalleryFailure("device_not_enrolled","Device enrollment is required before cloud delivery.",true);
            var employee=EmployeeCredential();if(employee==null)return GalleryFailure("employee_session_required","Employee must sign in before cloud delivery.",true);
            var result=await SendJsonAsync<EmptyRequest,GalleryEnvelope>(HttpMethod.Post,Api($"api/v3/device/booth-sessions/{boothSessionId:N}/gallery"),credential,employee,idempotencyKey,new EmptyRequest(),token).ConfigureAwait(false);
            if(!result.Operation.Succeeded)return GalleryFailure(result.Operation.ErrorCode,result.Operation.ErrorMessage,result.Operation.Retryable);
            Uri url;var gallery=result.Value?.Gallery;if(gallery==null||!Uri.TryCreate(gallery.Url,UriKind.Absolute,out url))return GalleryFailure("invalid_gallery_response","Worker returned an invalid gallery URL.",true);
            return new RemoteGalleryResult{Succeeded=true,ExternalReference=gallery.Id,GalleryUrl=url,ExpiresAtUtc=Unix(gallery.ExpiresAt)};
        }

        public async Task<RemoteGalleryResult> PrepareGalleryAsync(Guid boothSessionId,string deliverableId,CancellationToken token)
        {
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return GalleryFailure("device_not_enrolled","Device enrollment is required before cloud delivery.",true);
            var employee=EmployeeCredential();if(employee==null)return GalleryFailure("employee_session_required","Employee must sign in before cloud delivery.",true);
            var session=await EnsureBoothSessionAsync(boothSessionId,credential,employee,token).ConfigureAwait(false);if(!session.Operation.Succeeded)return GalleryFailure(session.Operation.ErrorCode,session.Operation.ErrorMessage,session.Operation.Retryable);
            return await CreateGalleryAsync(boothSessionId,deliverableId,"delivery:"+deliverableId+":gallery-prepare",token).ConfigureAwait(false);
        }

        public async Task<RemoteDeliveryOperationResult> ReportRevenueAsync(RevenueReport report,string idempotencyKey,CancellationToken token)
        {
            if(report==null||report.BoothSessionId==Guid.Empty)return Failure("invalid_revenue_report","Revenue report is incomplete.",false);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return Failure("device_not_enrolled","Device enrollment is required before revenue sync.",true);
            var employee=EmployeeCredential();if(employee==null)return Failure("employee_session_required","Employee must sign in before revenue sync.",true);
            var body=new RevenueRequest{BoothSessionId=report.BoothSessionId.ToString("N"),EventId=report.RemoteEventId,DisplayCode=report.DisplayCode,OpenedAt=Unix(report.OpenedAtUtc),CompletedAt=Unix(report.CompletedAtUtc),BasePriceMinor=report.BasePriceMinor,IncludedPrints=report.IncludedPrints,PrintedCopies=report.PrintedCopies,AdditionalPrintUnitPriceMinor=report.AdditionalPrintUnitPriceMinor,Currency=report.Currency};
            var result=await SendJsonAsync<RevenueRequest,StatusResponse>(HttpMethod.Post,Api("api/v3/device/revenue"),credential,employee,idempotencyKey,body,token).ConfigureAwait(false);
            return result.Operation;
        }

        async Task<string> CredentialAsync(CancellationToken token)
        {
            var identity=await identities.GetAsync(token).ConfigureAwait(false);if(identity==null||identity.EnrollmentState!=DeviceEnrollmentState.Enrolled)return null;
            var protectedValue=await identities.GetProtectedCredentialAsync(token).ConfigureAwait(false);if(protectedValue==null||protectedValue.Length==0)return null;
            try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedValue,null,DataProtectionScope.CurrentUser));}catch(CryptographicException){return null;}
        }

        string EmployeeCredential()
        {
            var session=sessions.Current;
            return session!=null&&!session.IsExpired&&!string.IsNullOrWhiteSpace(session.RemoteSessionToken)?session.RemoteSessionToken:null;
        }

        async Task<JsonResult<BoothSessionResponse>> EnsureBoothSessionAsync(Guid boothSessionId,string credential,string employee,CancellationToken token)
        {
            string remoteEventId=null;
            var booth=await localSessions.GetAsync(boothSessionId,token).ConfigureAwait(false);
            if(booth?.EventId!=null)
            {
                var photoEvent=await localSessions.GetAsync(booth.EventId.Value,token).ConfigureAwait(false);
                remoteEventId=photoEvent?.RemoteEventId;
            }
            return await SendJsonAsync<OpenBoothSessionRequest,BoothSessionResponse>(HttpMethod.Post,Api("api/v3/device/booth-sessions"),credential,employee,"booth-session:"+boothSessionId.ToString("N"),new OpenBoothSessionRequest{SessionId=boothSessionId.ToString("N"),EventId=remoteEventId},token).ConfigureAwait(false);
        }

        async Task<JsonResult<TResponse>> SendJsonAsync<TRequest,TResponse>(HttpMethod method,Uri uri,string credential,string idempotencyKey,TRequest body,CancellationToken token)
        { return await SendJsonAsync<TRequest,TResponse>(method,uri,credential,null,idempotencyKey,body,token).ConfigureAwait(false); }

        async Task<JsonResult<TResponse>> SendJsonAsync<TRequest,TResponse>(HttpMethod method,Uri uri,string credential,string employeeCredential,string idempotencyKey,TRequest body,CancellationToken token)
        {
            using(var request=new HttpRequestMessage(method,uri))
            {
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);if(!string.IsNullOrWhiteSpace(employeeCredential))request.Headers.TryAddWithoutValidation("X-Employee-Session",employeeCredential);request.Headers.TryAddWithoutValidation("Idempotency-Key",idempotencyKey);request.Headers.TryAddWithoutValidation("X-Request-Id",RequestId());request.Content=new ByteArrayContent(Json(body));request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(15,options.UploadTimeoutSeconds)));
                    try
                    {
                        using(var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                        {
                            var text=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if(!response.IsSuccessStatusCode){var code=ErrorCode(text);if(code=="storage_provider_not_configured"||code=="gallery_not_configured")BlockStorage();return new JsonResult<TResponse>{Operation=Failure(code,SafeMessage(response.StatusCode),Retryable(response.StatusCode,code))};}
                            TResponse value=default(TResponse);if(!string.IsNullOrWhiteSpace(text))try{value=Read<TResponse>(text);}catch(SerializationException){return new JsonResult<TResponse>{Operation=Failure("invalid_response","Worker returned an invalid response.",true)};}
                            return new JsonResult<TResponse>{Operation=Success(null),Value=value};
                        }
                    }
                    catch(OperationCanceledException) when(!token.IsCancellationRequested){return new JsonResult<TResponse>{Operation=Failure("request_timeout","Cloud delivery request timed out.",true)};}
                    catch(HttpRequestException){return new JsonResult<TResponse>{Operation=Failure("network_unavailable","Cloud delivery service is unavailable.",true)};}
                }
            }
        }

        async Task<RemoteDeliveryOperationResult> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(15,options.UploadTimeoutSeconds)));
                try{using(var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false)){if(response.IsSuccessStatusCode)return Success(null);var body=await response.Content.ReadAsStringAsync().ConfigureAwait(false);var code=ErrorCode(body);return Failure(code,SafeMessage(response.StatusCode),Retryable(response.StatusCode,code));}}
                catch(OperationCanceledException) when(!token.IsCancellationRequested){return Failure("request_timeout","Cloud delivery request timed out.",true);}
                catch(HttpRequestException){return Failure("network_unavailable","Cloud delivery service is unavailable.",true);}
            }
        }

        Uri Api(string relative){var value=(options.PhotoApiBaseUrl??string.Empty).TrimEnd('/');if(value.Length==0)throw new InvalidOperationException("Photo API base URL is not configured.");return new Uri(new Uri(value+"/"),relative);}
        Uri AbsoluteApiUri(string value){Uri result;if(Uri.TryCreate(value,UriKind.Absolute,out result))return result;return new Uri(new Uri((options.PhotoApiBaseUrl??string.Empty).TrimEnd('/')+"/"),value.TrimStart('/'));}
        static string AssetType(string kind){if(kind==MediaAssetKinds.FinalComposite)return "final_composite";if(kind==MediaAssetKinds.FinalVideo)return "final_video";if(kind==MediaAssetKinds.Gif)return "gif";if(kind==MediaAssetKinds.OriginalPicture)return "original_picture";throw new InvalidOperationException("Asset kind is not eligible for remote delivery.");}
        static async Task<string> HashAsync(string path,CancellationToken token){using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous|FileOptions.SequentialScan))using(var sha=SHA256.Create()){var buffer=new byte[81920];int read;while((read=await stream.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0)sha.TransformBlock(buffer,0,read,null,0);sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-",string.Empty).ToLowerInvariant();}}
        static byte[] Json<T>(T value){using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);return stream.ToArray();}}
        static T Read<T>(string value){using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(value)))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);}
        static string ErrorCode(string json){try{return Read<ErrorEnvelope>(json)?.Error?.Code??"remote_error";}catch{return "remote_error";}}
        static bool Retryable(HttpStatusCode status,string code){var number=(int)status;return number==408||number==425||number==429||number>=500||code=="storage_provider_not_configured"||code=="upload_reservation_expired"||code=="upload_not_writable"||code=="deliverable_not_ready";}
        static string SafeMessage(HttpStatusCode status){return "Cloud delivery request failed (HTTP "+(int)status+").";}
        static RemoteDeliveryOperationResult Success(string reference)=>new RemoteDeliveryOperationResult{Succeeded=true,ExternalReference=reference};
        static RemoteDeliveryOperationResult Failure(string code,string message,bool retryable)=>new RemoteDeliveryOperationResult{Succeeded=false,Retryable=retryable,ErrorCode=code,ErrorMessage=message};
        static RemoteGalleryResult GalleryFailure(string code,string message,bool retryable)=>new RemoteGalleryResult{Succeeded=false,Retryable=retryable,ErrorCode=code,ErrorMessage=message};
        static DateTime? Unix(long seconds)=>seconds>0?(DateTime?)DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime:null;
        static long Unix(DateTime value)=>new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds();
        static string RequestId()=>"pc-"+Guid.NewGuid().ToString("N");
        bool IsStorageBlocked()=>DateTime.UtcNow.Ticks<Interlocked.Read(ref storageBlockedUntilUtcTicks);
        void BlockStorage()=>Interlocked.Exchange(ref storageBlockedUntilUtcTicks,DateTime.UtcNow.AddMinutes(30).Ticks);

        sealed class JsonResult<T>{public RemoteDeliveryOperationResult Operation;public T Value;}
        [DataContract] sealed class ReserveRequest{[DataMember(Name="boothSessionId")]public string BoothSessionId;[DataMember(Name="assetType")]public string AssetType;[DataMember(Name="contentType")]public string ContentType;[DataMember(Name="bytes")]public long Bytes;[DataMember(Name="sha256")]public string Sha256;}
        [DataContract] sealed class ReserveResponse{[DataMember(Name="status")]public string Status{get;set;}[DataMember(Name="uploadId")]public string UploadId{get;set;}[DataMember(Name="uploadUrl")]public string UploadUrl{get;set;}}
        [DataContract] sealed class CommitUploadRequest{[DataMember(Name="uploadId")]public string UploadId;[DataMember(Name="bytes")]public long Bytes;[DataMember(Name="sha256")]public string Sha256;}
        [DataContract] sealed class GalleryEnvelope{[DataMember(Name="gallery")]public GalleryResponse Gallery{get;set;}}
        [DataContract] sealed class GalleryResponse{[DataMember(Name="id")]public string Id{get;set;}[DataMember(Name="url")]public string Url{get;set;}[DataMember(Name="expiresAt")]public long ExpiresAt{get;set;}}
        [DataContract] sealed class OpenBoothSessionRequest{[DataMember(Name="sessionId")]public string SessionId{get;set;}[DataMember(Name="eventId",EmitDefaultValue=false)]public string EventId{get;set;}}
        [DataContract] sealed class BoothSessionResponse{[DataMember(Name="session")]public BoothSessionValue Session{get;set;}}
        [DataContract] sealed class BoothSessionValue{[DataMember(Name="id")]public string Id{get;set;}[DataMember(Name="status")]public string Status{get;set;}}
        [DataContract] sealed class StatusResponse{[DataMember(Name="status")]public string Status{get;set;}}
        [DataContract] sealed class RevenueRequest{[DataMember(Name="boothSessionId")]public string BoothSessionId;[DataMember(Name="eventId",EmitDefaultValue=false)]public string EventId;[DataMember(Name="displayCode",EmitDefaultValue=false)]public string DisplayCode;[DataMember(Name="openedAt")]public long OpenedAt;[DataMember(Name="completedAt")]public long CompletedAt;[DataMember(Name="basePriceMinor")]public long BasePriceMinor;[DataMember(Name="includedPrints")]public int IncludedPrints;[DataMember(Name="printedCopies")]public int PrintedCopies;[DataMember(Name="additionalPrintUnitPriceMinor")]public long AdditionalPrintUnitPriceMinor;[DataMember(Name="currency")]public string Currency;}
        [DataContract] sealed class EmptyRequest{}
        [DataContract] sealed class ErrorEnvelope{[DataMember(Name="error")]public ErrorBody Error{get;set;}}
        [DataContract] sealed class ErrorBody{[DataMember(Name="code")]public string Code{get;set;}}
    }
}
