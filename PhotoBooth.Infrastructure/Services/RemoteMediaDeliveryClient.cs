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
    public sealed class RemoteMediaDeliveryClient : IRemoteMediaDeliveryClient, IEventPrintAuthorizationService
    {
        static readonly HttpClient Http = ApiHttpTransport.Create();
        readonly IDeviceIdentityRepository identities;
        readonly IPcSessionContext sessions;
        readonly ISessionRepository localSessions;
        readonly IStorageManager storage;
        readonly ApplicationOptions options;
        readonly HttpClient http = Http;
        long storageBlockedUntilUtcTicks;

        public async Task<EventPrintReservation> ReserveAsync(Guid captureId, int copies, string idempotencyKey, CancellationToken token)
        {
            var booth = await localSessions.GetAsync(captureId, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(booth?.RemoteEventId)) throw new InvalidOperationException("Lượt chụp chưa được gắn với sự kiện.");
            var credential = await CredentialAsync(token).ConfigureAwait(false);
            var employee = EmployeeCredential();
            if (credential == null || employee == null) throw new InvalidOperationException("Vui lòng đăng nhập lại để in ảnh sự kiện.");
            var opened = await EnsureBoothSessionAsync(captureId, credential, employee, token).ConfigureAwait(false);
            if (!opened.Operation.Succeeded) throw new InvalidOperationException(opened.Operation.ErrorMessage);
            var result = await SendJsonAsync<PrintRequest,PrintResponse>(HttpMethod.Post, Api("api/v3/device/events/" + Uri.EscapeDataString(booth.RemoteEventId) + "/print-reservations"), credential, employee, idempotencyKey,
                new PrintRequest { BoothSessionId = captureId.ToString("N"), Copies = copies }, token).ConfigureAwait(false);
            if (!result.Operation.Succeeded) throw new InvalidOperationException(result.Operation.ErrorMessage);
            if (string.IsNullOrWhiteSpace(result.Value?.Reservation?.Id)) throw new InvalidOperationException("Chưa xác nhận được lượt in. Vui lòng thử lại.");
            return new EventPrintReservation { EventId = booth.RemoteEventId, Id = result.Value.Reservation.Id, State = result.Value.Reservation.State, SessionToken = employee };
        }

        public Task SubmitAsync(EventPrintReservation reservation, CancellationToken token) => FinishPrintAsync(reservation, "submit", token);
        public Task CommitAsync(EventPrintReservation reservation, CancellationToken token) => FinishPrintAsync(reservation, "commit", token);
        async Task FinishPrintAsync(EventPrintReservation reservation, string action, CancellationToken token)
        {
            var credential = await CredentialAsync(token).ConfigureAwait(false);
            if (credential == null) throw new InvalidOperationException("Thiết bị chưa được xác thực.");
            var result = await SendJsonAsync<EmptyRequest,PrintResponse>(HttpMethod.Post, Api("api/v3/device/events/" + Uri.EscapeDataString(reservation.EventId) + "/print-reservations/" + Uri.EscapeDataString(reservation.Id) + "/" + action),
                credential, reservation.SessionToken, "print:" + reservation.Id + ":" + action, new EmptyRequest(), token).ConfigureAwait(false);
            if (!result.Operation.Succeeded) throw new InvalidOperationException(result.Operation.ErrorMessage);
        }

        public RemoteMediaDeliveryClient(IDeviceIdentityRepository identityRepository,IPcSessionContext sessionContext,ISessionRepository sessionRepository,IStorageManager storageManager,ApplicationOptions applicationOptions)
        { identities=identityRepository;sessions=sessionContext;localSessions=sessionRepository;storage=storageManager;options=applicationOptions; }

        internal RemoteMediaDeliveryClient(IDeviceIdentityRepository identities, IPcSessionContext sessions, ISessionRepository localSessions, IStorageManager storage, ApplicationOptions options, HttpClient client)
            : this(identities, sessions, localSessions, storage, options) { http = client; }

        public async Task<RemoteDeliveryOperationResult> UploadAssetAsync(Guid boothSessionId,string deliverableId,MediaAssetRecord asset,string idempotencyKey,string employeeSessionToken,CancellationToken token)
        {
            if(IsStorageBlocked())return Failure("storage_provider_not_configured","Tính năng tải ảnh trực tuyến chưa được thiết lập.",true);
            if(asset==null)return Failure("asset_missing","Không tìm thấy thông tin ảnh trên thiết bị.",false);
            string fullPath;
            try{fullPath=storage.GetFullPath(asset.RelativePath);}catch(Exception){return Failure("asset_path_invalid","Đường dẫn ảnh trên thiết bị không hợp lệ.",false);}
            if(!File.Exists(fullPath))return Failure("asset_missing","Ảnh trên thiết bị không còn tồn tại.",false);
            var info=new FileInfo(fullPath);if(info.Length!=asset.FileLength)return Failure("asset_changed","Ảnh đã thay đổi sau khi chụp.",false);
            var hash=await HashAsync(fullPath,token).ConfigureAwait(false);
            if(!string.Equals(hash,asset.ContentHashSha256,StringComparison.OrdinalIgnoreCase))return Failure("asset_changed","Ảnh đã thay đổi sau khi chụp.",false);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return Failure("device_not_enrolled","Thiết bị chưa được liên kết. Vui lòng kiểm tra trong trang quản lý.",true);
            var employee=EmployeeCredential(employeeSessionToken);if(employee==null)return Failure("employee_session_required","Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",true);
            var remoteSession=await EnsureBoothSessionAsync(boothSessionId,credential,employee,token).ConfigureAwait(false);if(!remoteSession.Operation.Succeeded)return remoteSession.Operation;
            var reserve=new ReserveRequest{BoothSessionId=boothSessionId.ToString("N"),ClientAssetId=asset.Id,CapturedAt=Unix(asset.CreatedAtUtc==default(DateTime)?DateTime.UtcNow:asset.CreatedAtUtc),AssetType=AssetType(asset.Kind),ContentType=asset.MimeType,Bytes=asset.FileLength,Sha256=hash};
            var reserveResult=await SendJsonAsync<ReserveRequest,ReserveResponse>(HttpMethod.Post,Api("api/v3/device/media/reserve"),credential,employee,idempotencyKey,reserve,token).ConfigureAwait(false);
            if(!reserveResult.Operation.Succeeded)return reserveResult.Operation;
            var replicaProvider=MediaReplicaProviders.FromApi(reserveResult.Value?.Destination);var replicaReference=reserveResult.Value?.ReplicaId??reserveResult.Value?.UploadId;
            if(string.Equals(reserveResult.Value?.Status,"committed",StringComparison.OrdinalIgnoreCase)||string.Equals(reserveResult.Value?.Status,"ready",StringComparison.OrdinalIgnoreCase))return Success(replicaReference,replicaProvider);
            if(reserveResult.Value==null||string.IsNullOrWhiteSpace(reserveResult.Value.UploadId)||string.IsNullOrWhiteSpace(reserveResult.Value.UploadUrl))return Failure("invalid_reservation","Dịch vụ ảnh chưa sẵn sàng nhận tệp. Vui lòng thử lại.",true);

            var uploadUri=AbsoluteApiUri(reserveResult.Value.UploadUrl);
            var expectedUploadUri=Api("api/v3/device/media/"+Uri.EscapeDataString(reserveResult.Value.UploadId));
            if(uploadUri!=expectedUploadUri)return Failure("invalid_upload_destination","Dịch vụ trả về địa chỉ tải ảnh không hợp lệ. Ảnh vẫn được giữ trên máy.",false);
            using(var file=new FileStream(fullPath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous|FileOptions.SequentialScan))
            using(var content=new StreamContent(file,81920))
            using(var request=new HttpRequestMessage(HttpMethod.Put,uploadUri))
            {
                content.Headers.ContentType=MediaTypeHeaderValue.Parse(asset.MimeType);content.Headers.ContentLength=asset.FileLength;request.Content=content;
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);request.Headers.TryAddWithoutValidation("X-Content-Sha256",hash);request.Headers.TryAddWithoutValidation("X-Request-Id",RequestId());
                request.Headers.TryAddWithoutValidation("X-Employee-Session",employee);
                await DeviceRequestProof.SignAsync(request,identities,credential,hash,token).ConfigureAwait(false);
                var uploadResult=await SendAsync(request,token).ConfigureAwait(false);if(!uploadResult.Succeeded){uploadResult.ReplicaProvider=replicaProvider;uploadResult.ExternalReference=replicaReference;return uploadResult;}
            }

            var commit=new CommitUploadRequest{UploadId=reserveResult.Value.UploadId,Bytes=asset.FileLength,Sha256=hash};
            var commitResult=await SendJsonAsync<CommitUploadRequest,StatusResponse>(HttpMethod.Post,Api($"api/v3/device/media/{Uri.EscapeDataString(reserveResult.Value.UploadId)}/commit"),credential,employee,idempotencyKey+":commit",commit,token).ConfigureAwait(false);
            if(!commitResult.Operation.Succeeded){commitResult.Operation.ReplicaProvider=replicaProvider;commitResult.Operation.ExternalReference=replicaReference;return commitResult.Operation;}
            return Success(replicaReference,replicaProvider);
        }

        public async Task<RemoteDeliveryOperationResult> CommitDeliverableAsync(Guid boothSessionId,string deliverableId,string idempotencyKey,string employeeSessionToken,CancellationToken token)
        {
            if(IsStorageBlocked())return Failure("storage_provider_not_configured","Tính năng tải ảnh trực tuyến chưa được thiết lập.",true);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return Failure("device_not_enrolled","Thiết bị chưa được liên kết. Vui lòng kiểm tra trong trang quản lý.",true);
            var employee=EmployeeCredential(employeeSessionToken);if(employee==null)return Failure("employee_session_required","Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",true);
            var result=await SendJsonAsync<EmptyRequest,GalleryEnvelope>(HttpMethod.Post,Api($"api/v3/device/booth-sessions/{boothSessionId:N}/complete"),credential,employee,idempotencyKey,new EmptyRequest(),token).ConfigureAwait(false);
            if(!result.Operation.Succeeded)return result.Operation;
            var gallery=result.Value?.Gallery;
            if(gallery?.Status!="ready"||!Uri.TryCreate(gallery.Url,UriKind.Absolute,out var url)||gallery.ExpiresAt<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Failure("deliverable_not_ready","Ảnh chưa sẵn sàng để nhận. Dữ liệu vẫn được giữ trên máy.",true);
            // Completing a PTB Capture revokes its runtime. Use the Gallery returned
            // by that same response; a second authenticated gallery call will fail.
            return new RemoteGalleryResult{Succeeded=true,ExternalReference=gallery.Id,GalleryUrl=url,ExpiresAtUtc=Unix(gallery.ExpiresAt),DeliveryToken=result.Value?.Delivery?.Token};
        }

        public async Task<RemoteGalleryResult> CreateGalleryAsync(Guid boothSessionId,string deliverableId,string idempotencyKey,string employeeSessionToken,CancellationToken token)
        {
            if(IsStorageBlocked())return GalleryFailure("storage_provider_not_configured","Tính năng tải ảnh trực tuyến chưa được thiết lập.",true);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return GalleryFailure("device_not_enrolled","Thiết bị chưa được liên kết. Vui lòng kiểm tra trong trang quản lý.",true);
            var employee=EmployeeCredential(employeeSessionToken);if(employee==null)return GalleryFailure("employee_session_required","Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",true);
            var result=await SendJsonAsync<EmptyRequest,GalleryEnvelope>(HttpMethod.Post,Api($"api/v3/device/booth-sessions/{boothSessionId:N}/gallery"),credential,employee,idempotencyKey,new EmptyRequest(),token).ConfigureAwait(false);
            if(!result.Operation.Succeeded)return GalleryFailure(result.Operation.ErrorCode,result.Operation.ErrorMessage,result.Operation.Retryable);
            Uri url;var gallery=result.Value?.Gallery;if(gallery==null||!Uri.TryCreate(gallery.Url,UriKind.Absolute,out url))return GalleryFailure("invalid_gallery_response","Không thể tạo đường dẫn nhận ảnh. Vui lòng thử lại.",true);
            return new RemoteGalleryResult{Succeeded=true,ExternalReference=gallery.Id,GalleryUrl=url,ExpiresAtUtc=Unix(gallery.ExpiresAt),DeliveryToken=result.Value?.Delivery?.Token};
        }

        public async Task<RemoteGalleryResult> PrepareGalleryAsync(Guid boothSessionId,string deliverableId,CancellationToken token)
        {
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return GalleryFailure("device_not_enrolled","Thiết bị chưa được liên kết. Vui lòng kiểm tra trong trang quản lý.",true);
            var employee=EmployeeCredential();if(employee==null)return GalleryFailure("employee_session_required","Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",true);
            var session=await EnsureBoothSessionAsync(boothSessionId,credential,employee,token).ConfigureAwait(false);if(!session.Operation.Succeeded)return GalleryFailure(session.Operation.ErrorCode,session.Operation.ErrorMessage,session.Operation.Retryable);
            return await CreateGalleryAsync(boothSessionId,deliverableId,"delivery:"+deliverableId+":gallery-prepare",employee,token).ConfigureAwait(false);
        }

        public async Task<RemoteDeliveryOperationResult> ReportRevenueAsync(RevenueReport report,string idempotencyKey,CancellationToken token)
        {
            if(report==null||report.BoothSessionId==Guid.Empty)return Failure("invalid_revenue_report","Thông tin doanh thu chưa đầy đủ.",false);
            var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return Failure("device_not_enrolled","Thiết bị chưa được liên kết. Vui lòng kiểm tra trong trang quản lý.",true);
            var employee=EmployeeCredential();if(employee==null)return Failure("employee_session_required","Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",true);
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

        string EmployeeCredential(string overrideToken=null)
        {
            if(!string.IsNullOrWhiteSpace(overrideToken))return overrideToken;
            var session=sessions.Current;
            return session!=null&&!session.IsExpired&&!string.IsNullOrWhiteSpace(session.RemoteSessionToken)?session.RemoteSessionToken:null;
        }

        async Task<JsonResult<BoothSessionResponse>> EnsureBoothSessionAsync(Guid boothSessionId,string credential,string employee,CancellationToken token)
        {
            if(employee?.StartsWith("du3.",StringComparison.Ordinal)==true)return new JsonResult<BoothSessionResponse>{Operation=Success(null)};
            string remoteEventId=null;
            var booth=await localSessions.GetAsync(boothSessionId,token).ConfigureAwait(false);
            remoteEventId=booth?.RemoteEventId;
            return await SendJsonAsync<OpenBoothSessionRequest,BoothSessionResponse>(HttpMethod.Post,Api("api/v3/device/booth-sessions"),credential,employee,"booth-session:"+boothSessionId.ToString("N"),new OpenBoothSessionRequest{SessionId=boothSessionId.ToString("N"),EventId=remoteEventId},token).ConfigureAwait(false);
        }

        async Task<JsonResult<TResponse>> SendJsonAsync<TRequest,TResponse>(HttpMethod method,Uri uri,string credential,string idempotencyKey,TRequest body,CancellationToken token)
        { return await SendJsonAsync<TRequest,TResponse>(method,uri,credential,null,idempotencyKey,body,token).ConfigureAwait(false); }

        async Task<JsonResult<TResponse>> SendJsonAsync<TRequest,TResponse>(HttpMethod method,Uri uri,string credential,string employeeCredential,string idempotencyKey,TRequest body,CancellationToken token)
        {
            using(var request=new HttpRequestMessage(method,uri))
            {
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);if(!string.IsNullOrWhiteSpace(employeeCredential))request.Headers.TryAddWithoutValidation("X-Employee-Session",employeeCredential);request.Headers.TryAddWithoutValidation("Idempotency-Key",idempotencyKey);request.Headers.TryAddWithoutValidation("X-Request-Id",RequestId());request.Content=new ByteArrayContent(Json(body));request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
                await DeviceRequestProof.SignAsync(request,identities,credential,null,token).ConfigureAwait(false);
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(15,options.UploadTimeoutSeconds)));
                    try
                    {
                        using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                        {
                            var text=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if(!response.IsSuccessStatusCode){var code=ErrorCode(text);if(code=="storage_provider_not_configured"||code=="gallery_not_configured")BlockStorage();return new JsonResult<TResponse>{Operation=Failure(code,SafeMessage(response.StatusCode,code),Retryable(response.StatusCode,code))};}
                            TResponse value=default(TResponse);if(!string.IsNullOrWhiteSpace(text))try{value=Read<TResponse>(text);}catch(SerializationException){return new JsonResult<TResponse>{Operation=Failure("invalid_response","Dịch vụ ảnh phản hồi không hợp lệ. Vui lòng thử lại.",true)};}
                            return new JsonResult<TResponse>{Operation=Success(null),Value=value};
                        }
                    }
                catch(OperationCanceledException) when(!token.IsCancellationRequested){return new JsonResult<TResponse>{Operation=Failure("request_timeout","Dịch vụ ảnh phản hồi quá chậm. Vui lòng thử lại.",true)};}
                catch(HttpRequestException){return new JsonResult<TResponse>{Operation=Failure("network_unavailable","Không thể kết nối dịch vụ ảnh. Vui lòng kiểm tra mạng.",true)};}
                }
            }
        }

        async Task<RemoteDeliveryOperationResult> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(15,options.UploadTimeoutSeconds)));
                try{using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false)){if(response.IsSuccessStatusCode)return Success(null);var body=await response.Content.ReadAsStringAsync().ConfigureAwait(false);var code=ErrorCode(body);return Failure(code,SafeMessage(response.StatusCode,code),Retryable(response.StatusCode,code));}}
                catch(OperationCanceledException) when(!token.IsCancellationRequested){return Failure("request_timeout","Dịch vụ ảnh phản hồi quá chậm. Vui lòng thử lại.",true);}
                catch(HttpRequestException){return Failure("network_unavailable","Không thể kết nối dịch vụ ảnh. Vui lòng kiểm tra mạng.",true);}
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
        static string SafeMessage(HttpStatusCode status,string code){if(code=="event_print_quota_exceeded")return "Đã đạt giới hạn in của lượt chụp hoặc sự kiện.";if(code=="event_print_outcome_pending")return "Lượt in đang chờ xác minh. Không gửi in lại.";if(code=="event_ended"||code=="event_not_open"||code=="event_session_revoked")return "Sự kiện đã kết thúc hoặc phiên đã hết hiệu lực. Ảnh vẫn được giữ trên máy.";if(code=="device_locked")return "Thiết bị đang bị khóa tạm thời. Ảnh vẫn được giữ trên máy; chủ tài khoản cần xác minh lại trên đúng thiết bị để mở khóa.";if(code=="device_proof_required"||code=="device_proof_invalid"||code=="device_proof_replayed")return "Không thể xác minh khóa bảo mật của thiết bị. Ảnh vẫn được giữ trên máy; hãy kết nối lại thiết bị.";if(code=="subscription_required"||code=="subscription_app_access_expired")return "Gói sử dụng đã hết hạn. Ảnh vẫn được giữ trên máy nhưng không thể tải mới lên thư viện.";if(code=="account_suspended"||code=="account_locked")return "Tài khoản đang bị tạm ngưng. Ảnh vẫn được giữ an toàn trên máy.";if(code=="online_storage_unavailable")return "Gói hiện tại không bao gồm lưu trữ ảnh trực tuyến. Ảnh vẫn được giữ an toàn trên máy.";if(code=="google_drive_required"||code=="google_drive_reconnect_required")return "Nơi lưu ảnh trực tuyến cần được kết nối lại. Ảnh vẫn được giữ an toàn trên máy.";if(code=="storage_limit_reached")return "Dung lượng lưu trữ đã đầy. Ảnh vẫn được giữ trên máy; vui lòng gia hạn hoặc giải phóng dung lượng.";if(status==HttpStatusCode.Unauthorized||status==HttpStatusCode.Forbidden)return "Phiên đăng nhập không còn hiệu lực. Vui lòng đăng nhập lại.";return "Không thể tải ảnh lên thư viện lúc này. Vui lòng thử lại.";}
        static RemoteDeliveryOperationResult Success(string reference,string replicaProvider=null)=>new RemoteDeliveryOperationResult{Succeeded=true,ExternalReference=reference,ReplicaProvider=replicaProvider};
        static RemoteDeliveryOperationResult Failure(string code,string message,bool retryable)=>new RemoteDeliveryOperationResult{Succeeded=false,Retryable=retryable,ErrorCode=code,ErrorMessage=message};
        static RemoteGalleryResult GalleryFailure(string code,string message,bool retryable)=>new RemoteGalleryResult{Succeeded=false,Retryable=retryable,ErrorCode=code,ErrorMessage=message};
        static DateTime? Unix(long seconds)=>seconds>0?(DateTime?)DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime:null;
        static long Unix(DateTime value)=>new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds();
        static string RequestId()=>"pc-"+Guid.NewGuid().ToString("N");
        bool IsStorageBlocked()=>DateTime.UtcNow.Ticks<Interlocked.Read(ref storageBlockedUntilUtcTicks);
        void BlockStorage()=>Interlocked.Exchange(ref storageBlockedUntilUtcTicks,DateTime.UtcNow.AddMinutes(30).Ticks);

        sealed class JsonResult<T>{public RemoteDeliveryOperationResult Operation;public T Value;}
        [DataContract] sealed class ReserveRequest{[DataMember(Name="boothSessionId")]public string BoothSessionId;[DataMember(Name="clientAssetId")]public string ClientAssetId;[DataMember(Name="capturedAt")]public long CapturedAt;[DataMember(Name="assetType")]public string AssetType;[DataMember(Name="contentType")]public string ContentType;[DataMember(Name="bytes")]public long Bytes;[DataMember(Name="sha256")]public string Sha256;}
        [DataContract] sealed class ReserveResponse{[DataMember(Name="status")]public string Status{get;set;}[DataMember(Name="uploadId")]public string UploadId{get;set;}[DataMember(Name="mediaAssetId")]public string MediaAssetId{get;set;}[DataMember(Name="replicaId")]public string ReplicaId{get;set;}[DataMember(Name="destination")]public string Destination{get;set;}[DataMember(Name="uploadUrl")]public string UploadUrl{get;set;}}
        [DataContract] sealed class CommitUploadRequest{[DataMember(Name="uploadId")]public string UploadId;[DataMember(Name="bytes")]public long Bytes;[DataMember(Name="sha256")]public string Sha256;}
        [DataContract] sealed class DeliveryGrantResponse{[DataMember(Name="token")]public string Token{get;set;}}
        [DataContract] sealed class GalleryEnvelope{[DataMember(Name="delivery")]public DeliveryGrantResponse Delivery{get;set;}[DataMember(Name="gallery")]public GalleryResponse Gallery{get;set;}}
        [DataContract] sealed class GalleryResponse{[DataMember(Name="id")]public string Id{get;set;}[DataMember(Name="url")]public string Url{get;set;}[DataMember(Name="expiresAt")]public long ExpiresAt{get;set;}[DataMember(Name="status")]public string Status{get;set;}}
        [DataContract] sealed class OpenBoothSessionRequest{[DataMember(Name="sessionId")]public string SessionId{get;set;}[DataMember(Name="eventId",EmitDefaultValue=false)]public string EventId{get;set;}}
        [DataContract] sealed class BoothSessionResponse{[DataMember(Name="session")]public BoothSessionValue Session{get;set;}}
        [DataContract] sealed class BoothSessionValue{[DataMember(Name="id")]public string Id{get;set;}[DataMember(Name="status")]public string Status{get;set;}}
        [DataContract] sealed class StatusResponse{[DataMember(Name="status")]public string Status{get;set;}}
        [DataContract] sealed class RevenueRequest{[DataMember(Name="boothSessionId")]public string BoothSessionId;[DataMember(Name="eventId",EmitDefaultValue=false)]public string EventId;[DataMember(Name="displayCode",EmitDefaultValue=false)]public string DisplayCode;[DataMember(Name="openedAt")]public long OpenedAt;[DataMember(Name="completedAt")]public long CompletedAt;[DataMember(Name="basePriceMinor")]public long BasePriceMinor;[DataMember(Name="includedPrints")]public int IncludedPrints;[DataMember(Name="printedCopies")]public int PrintedCopies;[DataMember(Name="additionalPrintUnitPriceMinor")]public long AdditionalPrintUnitPriceMinor;[DataMember(Name="currency")]public string Currency;}
        [DataContract] sealed class EmptyRequest{}
        [DataContract] sealed class PrintRequest { [DataMember(Name="boothSessionId")] public string BoothSessionId; [DataMember(Name="copies")] public int Copies; }
        [DataContract] sealed class PrintResponse { [DataMember(Name="reservation")] public PrintReservationValue Reservation; }
        [DataContract] sealed class PrintReservationValue { [DataMember(Name="id")] public string Id; [DataMember(Name="state")] public string State; }
        [DataContract] sealed class ErrorEnvelope{[DataMember(Name="error")]public ErrorBody Error{get;set;}}
        [DataContract] sealed class ErrorBody{[DataMember(Name="code")]public string Code{get;set;}}
    }
}
