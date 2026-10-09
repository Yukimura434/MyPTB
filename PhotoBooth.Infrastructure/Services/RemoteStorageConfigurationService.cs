using System;
using System.IO;
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
    public sealed class RemoteStorageConfigurationService : IRemoteStorageConfigurationService
    {
        static readonly HttpClient Http = ApiHttpTransport.Create();
        static readonly HttpMethod Patch = new HttpMethod("PATCH");
        readonly IDeviceIdentityRepository identities;
        readonly IPcSessionContext sessions;
        readonly ApplicationOptions options;

        public RemoteStorageConfigurationService(IDeviceIdentityRepository identityRepository, IPcSessionContext sessionContext, ApplicationOptions applicationOptions)
        { identities=identityRepository;sessions=sessionContext;options=applicationOptions; }

        public async Task<RemoteStorageConfiguration> GetAsync(CancellationToken token)
        {
            var credential=await CredentialAsync(token).ConfigureAwait(false);
            if(credential==null)return Failure("device_not_enrolled","Thiết bị chưa được liên kết với tài khoản khách hàng.");
            var result=await SendAsync<EmptyRequest,SettingsResponse>(HttpMethod.Get,"api/v3/device/integrations/google-drive/status",credential,null,null,token).ConfigureAwait(false);
            return result.Error??Map(result.Value);
        }

        public async Task<RemoteStorageConfiguration> SetProviderAsync(string provider,CancellationToken token)
        {
            var normalized=RemoteStorageProviders.Normalize(provider);var credential=await CredentialAsync(token).ConfigureAwait(false);if(credential==null)return Failure("device_not_enrolled","Thiết bị chưa được liên kết với tài khoản khách hàng.");
            var employee=EmployeeCredential();if(employee==null)return Failure("employee_session_required","Phiên quản lý đã hết hạn. Vui lòng đăng nhập lại.");
            var result=await SendAsync<ProviderRequest,SettingsResponse>(Patch,"api/v3/device/delivery-settings",credential,employee,new ProviderRequest{Provider=normalized},token).ConfigureAwait(false);
            return result.Error??Map(result.Value);
        }

        async Task<Result<TResponse>> SendAsync<TRequest,TResponse>(HttpMethod method,string relative,string credential,string employee,TRequest value,CancellationToken token)
        {
            using(var request=new HttpRequestMessage(method,Api(relative)))
            {
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);request.Headers.TryAddWithoutValidation("X-Request-Id","pc-"+Guid.NewGuid().ToString("N"));
                if(!string.IsNullOrWhiteSpace(employee))request.Headers.TryAddWithoutValidation("X-Employee-Session",employee);
                if(value!=null){request.Content=new ByteArrayContent(Json(value));request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");}
                await DeviceRequestProof.SignAsync(request,identities,credential,null,token).ConfigureAwait(false);
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(15,options.UploadTimeoutSeconds)));
                    try
                    {
                        using(var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                        {
                            var content=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if(!response.IsSuccessStatusCode){var code=ErrorCode(content);return new Result<TResponse>{Error=Failure(code,Message(code))};}
                            if(string.IsNullOrWhiteSpace(content))return new Result<TResponse>{Error=Failure("invalid_response","Dịch vụ không trả về trạng thái nơi lưu ảnh.")};
                            try{return new Result<TResponse>{Value=Read<TResponse>(content)};}catch(SerializationException){return new Result<TResponse>{Error=Failure("invalid_response","Dịch vụ trả về dữ liệu không hợp lệ.")};}
                        }
                    }
                    catch(OperationCanceledException) when(!token.IsCancellationRequested){return new Result<TResponse>{Error=Failure("request_timeout","Dịch vụ phản hồi quá chậm. Vui lòng thử lại.")};}
                    catch(HttpRequestException){return new Result<TResponse>{Error=Failure("network_unavailable","Không thể kết nối dịch vụ ảnh. Vui lòng kiểm tra mạng.")};}
                }
            }
        }

        async Task<string> CredentialAsync(CancellationToken token)
        {
            var identity=await identities.GetAsync(token).ConfigureAwait(false);if(identity==null||identity.EnrollmentState!=DeviceEnrollmentState.Enrolled)return null;
            var protectedValue=await identities.GetProtectedCredentialAsync(token).ConfigureAwait(false);if(protectedValue==null||protectedValue.Length==0)return null;
            try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedValue,null,DataProtectionScope.CurrentUser));}catch(CryptographicException){return null;}
        }
        string EmployeeCredential(){var session=sessions.Current;return session!=null&&!session.IsExpired&&session.CanManageConfiguration&&!string.IsNullOrWhiteSpace(session.RemoteSessionToken)?session.RemoteSessionToken:null;}
        Uri Api(string relative){var value=(options.PhotoApiBaseUrl??string.Empty).TrimEnd('/');if(value.Length==0)throw new InvalidOperationException("Photo API base URL is not configured.");return new Uri(new Uri(value+"/"),relative);}
        static RemoteStorageConfiguration Map(SettingsResponse value){if(value==null)return Failure("invalid_response","Dịch vụ không trả về trạng thái nơi lưu ảnh.");return new RemoteStorageConfiguration{Succeeded=true,Provider=RemoteStorageProviders.Normalize(value.Provider),OrganizationProvider=RemoteStorageProviders.Normalize(value.OrganizationProvider),DeviceOverride=value.DeviceOverride,GoogleDriveConnected=value.Drive!=null&&value.Drive.Status=="connected",GoogleDriveStatus=value.Drive?.Status,GoogleEmail=value.Drive?.Email};}
        static RemoteStorageConfiguration Failure(string code,string message)=>new RemoteStorageConfiguration{Succeeded=false,ErrorCode=code,ErrorMessage=message};
        static string Message(string code){if(code=="device_locked")return "Thiết bị đang bị khóa tạm thời. Chủ tài khoản cần xác minh lại trên đúng thiết bị để mở khóa.";if(code=="device_proof_required"||code=="device_proof_invalid"||code=="device_proof_replayed")return "Không thể xác minh khóa bảo mật của thiết bị. Hãy kết nối lại thiết bị.";if(code=="manager_required"||code=="booth_manager_required")return "Nơi lưu ảnh trực tuyến cần được thay đổi trong tài khoản chủ trên web.";if(code=="drive_reconnect_required"||code=="google_drive_reconnect_required")return "Google Drive cần được kết nối lại trên Web Customer.";if(code=="google_drive_not_configured")return "Kết nối Google Drive chưa được bật cho hệ thống.";if(code=="google_drive_public_sharing_blocked")return "Tài khoản Google không cho phép chia sẻ thư mục bằng mã QR.";return "Không thể cập nhật nơi lưu ảnh. Vui lòng thử lại.";}
        static byte[] Json<T>(T value){using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);return stream.ToArray();}}
        static T Read<T>(string value){using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(value)))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);}
        static string ErrorCode(string json){try{return Read<ErrorEnvelope>(json)?.Error?.Code??"remote_error";}catch{return "remote_error";}}

        sealed class Result<T>{public T Value;public RemoteStorageConfiguration Error;}
        [DataContract]sealed class EmptyRequest{}
        [DataContract]sealed class ProviderRequest{[DataMember(Name="provider")]public string Provider{get;set;}}
        [DataContract]sealed class SettingsResponse{[DataMember(Name="provider")]public string Provider{get;set;}[DataMember(Name="organizationProvider")]public string OrganizationProvider{get;set;}[DataMember(Name="deviceOverride")]public string DeviceOverride{get;set;}[DataMember(Name="drive")]public GoogleDriveResponse Drive{get;set;}}
        [DataContract]sealed class GoogleDriveResponse{[DataMember(Name="status")]public string Status{get;set;}[DataMember(Name="email")]public string Email{get;set;}}
        [DataContract]sealed class ErrorEnvelope{[DataMember(Name="error")]public ErrorBody Error{get;set;}}
        [DataContract]sealed class ErrorBody{[DataMember(Name="code")]public string Code{get;set;}}
    }
}
