using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class DeviceIdentityService : IDeviceIdentityService
    {
        static readonly HttpClient Http = ApiHttpTransport.Create(TimeSpan.FromSeconds(30));
        readonly IDeviceIdentityRepository repository;
        readonly ApplicationOptions options;
        readonly HttpClient http;
        readonly ILogger<DeviceIdentityService> logger;

        public DeviceIdentityService(IDeviceIdentityRepository identityRepository, ApplicationOptions applicationOptions, ILogger<DeviceIdentityService> logger = null)
            : this(identityRepository, applicationOptions, Http, logger)
        {
        }

        internal DeviceIdentityService(IDeviceIdentityRepository identityRepository, ApplicationOptions applicationOptions, HttpClient httpClient, ILogger<DeviceIdentityService> logger = null)
        {
            repository = identityRepository;
            options = applicationOptions;
            http = httpClient;
            this.logger = logger;
        }

        public Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken) => repository.GetAsync(cancellationToken);

        public Task<IReadOnlyList<DeviceIdentity>> ListProfilesAsync(CancellationToken cancellationToken) => repository.ListProfilesAsync(cancellationToken);

        public Task SelectProfileAsync(string profileId,CancellationToken cancellationToken) => repository.SelectProfileAsync(profileId,cancellationToken);

        public async Task<DeviceActivationAttempt> BeginOwnerActivationAsync(string email,string password,string appVersion,CancellationToken cancellationToken)
        {
            if(string.IsNullOrWhiteSpace(email))throw new ArgumentException("Hãy nhập email chủ tài khoản.",nameof(email));
            if(string.IsNullOrWhiteSpace(password))throw new ArgumentException("Hãy nhập mật khẩu.",nameof(password));
            EnsureApiConfigured();
            var installationId=await RequiredInstallationIdAsync(cancellationToken).ConfigureAwait(false);
            var existingIdentity=await repository.GetAsync(cancellationToken).ConfigureAwait(false);
            var key=DeviceRequestProof.GetOrCreatePublicKey(installationId,existingIdentity?.EnrollmentState!=DeviceEnrollmentState.Enrolled);
            var payload=new V3ActivationStartRequest{Email=email.Trim().ToLowerInvariant(),Password=password,InstallationId=installationId,DevicePublicKey=key.PublicKey};
            V3ActivationStartEnvelope value;
            try
            {
                value=await PostAsync<V3ActivationStartRequest,V3ActivationStartEnvelope>("api/v3/app/activation/start",payload,cancellationToken).ConfigureAwait(false);
            }
            catch(HttpRequestException exception){throw new InvalidOperationException("Không thể kết nối dịch vụ xác minh. Hãy kiểm tra mạng và thử lại.",exception);}
            catch(TaskCanceledException exception) when(!cancellationToken.IsCancellationRequested){throw new InvalidOperationException("Dịch vụ xác minh phản hồi quá chậm. Hãy thử lại.",exception);}
            if(value?.Activation==null||string.IsNullOrWhiteSpace(value.Activation.Id)||string.IsNullOrWhiteSpace(value.Activation.Challenge))
                throw new InvalidOperationException("Không thể bắt đầu thiết lập thiết bị. Hãy thử lại.");
            if(!value.Activation.DeviceProofRequired||string.IsNullOrWhiteSpace(value.Activation.PublicKeyThumbprint)||
               !string.Equals(value.Activation.PublicKeyThumbprint,key.Thumbprint,StringComparison.Ordinal))
                throw new InvalidOperationException("Dịch vụ không xác nhận đúng khóa bảo mật của thiết bị. Không tiếp tục thiết lập.");
            var locations=new List<DeviceActivationLocation>();
            if(value.Activation.Locations!=null)foreach(var location in value.Activation.Locations)
            {
                if(location==null||string.IsNullOrWhiteSpace(location.Id)||string.IsNullOrWhiteSpace(location.Name))continue;
                locations.Add(new DeviceActivationLocation{Id=location.Id,Name=location.Name,DeviceCount=Math.Max(0,location.DeviceCount)});
            }
            return new DeviceActivationAttempt
            {
                InstallationId=installationId,
                AttemptId=value.Activation.Id,
                Challenge=value.Activation.Challenge,
                ExpiresAtUtc=DateTimeOffset.FromUnixTimeSeconds(value.Activation.ExpiresAt).UtcDateTime,
                OwnerDisplayName=value.Activation.Owner?.DisplayName,
                OrganizationId=value.Activation.Organization?.Id,
                OrganizationName=value.Activation.Organization?.Name,
                PublicKeyThumbprint=value.Activation.PublicKeyThumbprint,
                DeviceProofRequired=value.Activation.DeviceProofRequired,
                Locations=locations
            };
        }

        public async Task<DeviceIdentity> CompleteOwnerActivationAsync(DeviceActivationAttempt attempt,string locationId,string deviceName,string appVersion,CancellationToken cancellationToken)
        {
            if(attempt==null||string.IsNullOrWhiteSpace(attempt.AttemptId)||string.IsNullOrWhiteSpace(attempt.Challenge))throw new ArgumentException("Phiên thiết lập không còn hợp lệ. Hãy xác minh lại.",nameof(attempt));
            if(attempt.ExpiresAtUtc<=DateTime.UtcNow)throw new InvalidOperationException("Phiên thiết lập đã hết hạn. Hãy xác minh lại.");
            if(string.IsNullOrWhiteSpace(locationId))throw new ArgumentException("Hãy chọn cửa hàng cho thiết bị.",nameof(locationId));
            if(attempt.Locations==null||!ContainsLocation(attempt.Locations,locationId))throw new InvalidOperationException("Cửa hàng đã chọn không còn hợp lệ. Hãy xác minh lại.");
            if(string.IsNullOrWhiteSpace(deviceName))throw new ArgumentException("Hãy nhập tên thiết bị.",nameof(deviceName));
            EnsureApiConfigured();
            if(!attempt.DeviceProofRequired||string.IsNullOrWhiteSpace(attempt.PublicKeyThumbprint)||string.IsNullOrWhiteSpace(attempt.OrganizationId))
                throw new InvalidOperationException("Phiên thiết lập chưa có xác nhận khóa bảo mật. Hãy xác minh lại từ đầu.");
            var installationId=await RequiredInstallationIdAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(attempt.InstallationId, installationId, StringComparison.Ordinal))
                throw new DeviceReenrollmentRequiredException();
            var normalizedLocationId=locationId.Trim();
            var normalizedDeviceName=deviceName.Trim();
            const string platform="windows";
            var payload=new V3ActivationCompleteRequest
            {
                AttemptId=attempt.AttemptId,
                Challenge=attempt.Challenge,
                LocationId=normalizedLocationId,
                DeviceName=normalizedDeviceName,
                Platform=platform,
                AppVersion=appVersion,
                DeviceProof=DeviceRequestProof.SignActivation(installationId,attempt.AttemptId,attempt.Challenge,attempt.OrganizationId,attempt.PublicKeyThumbprint,normalizedLocationId,normalizedDeviceName,platform,appVersion)
            };
            V3ActivationResponse value;
            try
            {
                value=await PostAsync<V3ActivationCompleteRequest,V3ActivationResponse>("api/v3/app/activation/complete",payload,cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceReenrollmentRequiredException)
            {
                await repository.ResetRevokedInstallationAsync(installationId, attempt.OrganizationId, cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch(HttpRequestException exception){throw new InvalidOperationException("Không thể kết nối thiết bị. Hãy kiểm tra mạng và thử lại.",exception);}
            catch(TaskCanceledException exception) when(!cancellationToken.IsCancellationRequested){throw new InvalidOperationException("Dịch vụ kết nối phản hồi quá chậm. Hãy thử lại.",exception);}
            return await SaveActivationAsync(value,installationId,cancellationToken).ConfigureAwait(false);
        }

        public async Task<DeviceIdentity> ActivateV3Async(string email,string password,string deviceName,string locationId,string appVersion,CancellationToken cancellationToken)
        {
            if(string.IsNullOrWhiteSpace(email))throw new ArgumentException("Email tài khoản chủ quán là bắt buộc.",nameof(email));
            if(string.IsNullOrWhiteSpace(password))throw new ArgumentException("Mật khẩu tài khoản chủ quán là bắt buộc.",nameof(password));
            var attempt=await BeginOwnerActivationAsync(email,password,appVersion,cancellationToken).ConfigureAwait(false);
            var selectedLocation=string.IsNullOrWhiteSpace(locationId)&&attempt.Locations.Count==1?attempt.Locations[0].Id:locationId;
            if(string.IsNullOrWhiteSpace(selectedLocation))throw new InvalidOperationException("Hãy chọn cửa hàng cho thiết bị.");
            return await CompleteOwnerActivationAsync(attempt,selectedLocation,string.IsNullOrWhiteSpace(deviceName)?Environment.MachineName:deviceName,appVersion,cancellationToken).ConfigureAwait(false);
        }

        public async Task<DeviceActivationAttempt> StartActivationAsync(string code,string fingerprint,string appVersion,CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("API v3 không sử dụng mã kích hoạt. Hãy kích hoạt thiết bị bằng email và mật khẩu chủ quán.");
        }

        public async Task<DeviceIdentity> CompleteActivationAsync(DeviceActivationAttempt attempt,string loginIdentifier,string password,string deviceName,string appVersion,CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Luồng hoàn tất mã kích hoạt đã bị loại bỏ khỏi API v3.");
        }

        public async Task<DeviceIdentity> EnrollAsync(string code, string deviceName, string fingerprint, string appVersion, CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Device enrollment code đã bị loại bỏ khỏi API v3.");
        }

        public Task ClearAsync(CancellationToken cancellationToken) => repository.ClearAsync(cancellationToken);

        async Task<DeviceIdentity> SaveActivationAsync(V3ActivationResponse value,string installationId,CancellationToken cancellationToken)
        {
            if(value?.Device==null||string.IsNullOrWhiteSpace(value.Device.Id)||string.IsNullOrWhiteSpace(value.Device.OrganizationId)||string.IsNullOrWhiteSpace(value.Credential))throw new InvalidOperationException("Dịch vụ xác minh trả về thông tin thiết bị không đầy đủ.");
            var identity=new DeviceIdentity{ProfileId="v3_"+value.Device.Id,InstallationId=installationId,OrganizationId=value.Device.OrganizationId,OrganizationName=value.Device.OrganizationName,LocationId=value.Device.LocationId,LocationName=value.Device.LocationName,DeviceId=value.Device.Id,DeviceName=value.Device.Name,EnrollmentState=DeviceEnrollmentState.Enrolled,CredentialVersion=3,LastAuthenticatedAtUtc=DateTime.UtcNow};
            var protectedCredential=ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Credential),null,DataProtectionScope.CurrentUser);
            await repository.SaveProfileAsync(identity,protectedCredential,cancellationToken).ConfigureAwait(false);
            return identity;
        }

        async Task<string> RequiredInstallationIdAsync(CancellationToken cancellationToken)
        {
            var value=await repository.GetInstallationPublicIdAsync(cancellationToken).ConfigureAwait(false);
            if(string.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Không thể nhận diện bản cài đặt trên máy này.");
            return value;
        }

        void EnsureApiConfigured()
        {
            if(string.IsNullOrWhiteSpace(options.PhotoApiBaseUrl))throw new InvalidOperationException("Chưa thể kết nối dịch vụ PhotoBooth. Hãy kiểm tra cấu hình ứng dụng.");
        }

        async Task<TResponse> PostAsync<TRequest,TResponse>(string relativePath,TRequest payload,CancellationToken cancellationToken)
        {
            var operation = relativePath.EndsWith("/start", StringComparison.Ordinal) ? "start" : "complete";
            var requestId = "pc-activation-" + Guid.NewGuid().ToString("N");
            var timer = Stopwatch.StartNew();
            logger?.LogInformation("Device activation {Operation} started. Request {RequestId}.", operation, requestId);
            try
            {
                using(var message=new HttpRequestMessage(HttpMethod.Post,new Uri(new Uri(options.PhotoApiBaseUrl.TrimEnd('/')+"/"),relativePath)))
                {
                    message.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
                    message.Content=new StringContent(Json(payload),Encoding.UTF8,"application/json");
                    using(var response=await http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,cancellationToken).ConfigureAwait(false))
                    {
                        var content=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        // Never log the payload, response body, URL, Owner identity, challenge or credential.
                        var errorCode = response.IsSuccessStatusCode ? "none" : DiagnosticErrorCode(content);
                        logger?.LogInformation("Device activation {Operation} returned HTTP {Status} after {ElapsedMs} ms. Code {Code}. Request {RequestId}.",
                            operation, (int)response.StatusCode, timer.ElapsedMilliseconds, errorCode, requestId);
                        if(!response.IsSuccessStatusCode)
                        {
                            // Generic device_revoked also covers stale credentials and disabled locations.
                            // Rotate identity only when Owner activation confirms permanent revocation.
                            if (response.StatusCode == System.Net.HttpStatusCode.Conflict &&
                                ErrorCode(content) == "device_revoked_reenrollment_required")
                                throw new DeviceReenrollmentRequiredException();
                            throw new InvalidOperationException(ErrorMessage(content,response.StatusCode));
                        }
                        return Read<TResponse>(content);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                logger?.LogWarning("Device activation {Operation} {Outcome} after {ElapsedMs} ms. Request {RequestId}.",
                    operation, cancellationToken.IsCancellationRequested ? "cancelled" : "timed out", timer.ElapsedMilliseconds, requestId);
                throw;
            }
            catch (HttpRequestException exception)
            {
                logger?.LogWarning("Device activation {Operation} transport failed after {ElapsedMs} ms. Failure {FailureType}, code {FailureCode}. Request {RequestId}.",
                    operation, timer.ElapsedMilliseconds, exception.GetBaseException().GetType().Name, exception.GetBaseException().HResult, requestId);
                throw;
            }
        }

        static string DiagnosticErrorCode(string content)
        {
            try
            {
                var code = ErrorCode(content);
                if (string.IsNullOrWhiteSpace(code) || code.Length > 80) return "unknown";
                foreach (var character in code)
                    if (!(character >= 'a' && character <= 'z') && character != '_') return "unknown";
                return code;
            }
            catch { return "unparsed"; }
        }

        static bool ContainsLocation(IReadOnlyList<DeviceActivationLocation> values,string locationId)
        {
            foreach(var value in values)if(string.Equals(value?.Id,locationId,StringComparison.Ordinal))return true;
            return false;
        }

        static string Json<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        static T Read<T>(string value)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        static string ErrorCode(string content)
        {
            try { return Read<ErrorEnvelope>(content)?.Error?.Code; }
            catch (SerializationException) { return null; }
        }

        static string ErrorMessage(string content, System.Net.HttpStatusCode status)
        {
            try
            {
                var error = Read<ErrorEnvelope>(content);
                switch(error?.Error?.Code)
                {
                    case "invalid_credentials": return "Email hoặc mật khẩu không đúng.";
                    case "owner_login_locked": return "Tài khoản đang tạm khóa do nhập sai nhiều lần. Hãy thử lại sau.";
                    case "email_verification_required": return "Email chủ tài khoản chưa được xác minh. Hãy hoàn tất xác minh trên Web Customer.";
                    case "device_limit_reached": return "Tài khoản đã dùng hết số lượng thiết bị được phép.";
                    case "location_not_found": return "Cửa hàng đã chọn không còn hoạt động. Hãy xác minh lại.";
                    case "app_activation_invalid": return "Phiên thiết lập đã hết hạn hoặc không hợp lệ. Hãy xác minh lại.";
                    case "app_activation_in_progress": return "Thiết bị đang được kết nối. Vui lòng chờ trong giây lát.";
                    case "device_activation_proof_required": return "Thiết bị cần xác nhận khóa bảo mật. Hãy bắt đầu lại quá trình kết nối.";
                    case "device_proof_invalid": return "Không thể xác minh khóa bảo mật của thiết bị. Hãy thử lại trên đúng máy đã bắt đầu kết nối.";
                    case "device_key_mismatch": return "Khóa bảo mật không khớp với thiết bị đã đăng ký. Không thể mở khóa trên máy khác.";
                    case "device_revoked_reenrollment_required": return "Thiết bị đã bị thu hồi. Hãy đăng ký lại như một thiết bị mới.";
                    case "account_suspended":
                    case "account_locked": return "Tài khoản đang bị tạm ngưng. Vui lòng liên hệ nhà cung cấp.";
                    case "account_closed": return "Tài khoản không còn hoạt động. Vui lòng liên hệ nhà cung cấp.";
                    case "subscription_required":
                    case "subscription_app_access_expired": return "Gói sử dụng đã hết hạn. Vui lòng liên hệ chủ tài khoản hoặc nhà cung cấp để gia hạn.";
                }
            }
            catch { }
            return (int)status>=500?"Dịch vụ đang gặp sự cố. Hãy thử lại sau.":"Không thể hoàn tất thiết lập thiết bị. Hãy kiểm tra thông tin và thử lại.";
        }

        [DataContract]
        sealed class EnrollRequest
        {
            [DataMember(Name = "code", EmitDefaultValue = false)] public string Code { get; set; }
            [DataMember(Name = "name", EmitDefaultValue = false)] public string Name { get; set; }
            [DataMember(Name = "fingerprint", EmitDefaultValue = false)] public string Fingerprint { get; set; }
            [DataMember(Name = "appVersion", EmitDefaultValue = false)] public string AppVersion { get; set; }
            [DataMember(Name = "platform", EmitDefaultValue = false)] public string Platform { get; set; }
        }

        [DataContract]
        sealed class DeviceEnrollResponse
        {
            [DataMember(Name = "device")] public EnrolledDevice Device { get; set; }
            [DataMember(Name = "credential")] public string Credential { get; set; }
        }

        [DataContract]
        sealed class EnrolledDevice
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "organizationId")] public string OrganizationId { get; set; }
            [DataMember(Name = "locationId")] public string LocationId { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
        }

        [DataContract]
        sealed class ErrorEnvelope
        {
            [DataMember(Name = "error")] public ApiError Error { get; set; }
        }

        [DataContract]
        sealed class ApiError
        {
            [DataMember(Name = "code")] public string Code { get; set; }
            [DataMember(Name = "message")] public string Message { get; set; }
        }

        [DataContract] sealed class V3ActivationResponse { [DataMember(Name="device")] public V3Device Device{get;set;} [DataMember(Name="credential")] public string Credential{get;set;} }
        [DataContract] sealed class V3Device { [DataMember(Name="id")] public string Id{get;set;} [DataMember(Name="organizationId")] public string OrganizationId{get;set;} [DataMember(Name="organizationName")] public string OrganizationName{get;set;} [DataMember(Name="locationId")] public string LocationId{get;set;} [DataMember(Name="locationName")] public string LocationName{get;set;} [DataMember(Name="name")] public string Name{get;set;} }

        [DataContract] sealed class V3ActivationStartRequest { [DataMember(Name="email")] public string Email{get;set;} [DataMember(Name="password")] public string Password{get;set;} [DataMember(Name="installationId")] public string InstallationId{get;set;} [DataMember(Name="devicePublicKey")] public DevicePublicKey DevicePublicKey{get;set;} }
        [DataContract] sealed class V3ActivationCompleteRequest { [DataMember(Name="attemptId")] public string AttemptId{get;set;} [DataMember(Name="challenge")] public string Challenge{get;set;} [DataMember(Name="locationId")] public string LocationId{get;set;} [DataMember(Name="deviceName")] public string DeviceName{get;set;} [DataMember(Name="platform")] public string Platform{get;set;} [DataMember(Name="appVersion",EmitDefaultValue=false)] public string AppVersion{get;set;} [DataMember(Name="deviceProof")] public string DeviceProof{get;set;} }
        [DataContract] sealed class V3ActivationStartEnvelope { [DataMember(Name="activation")] public V3ActivationStartPayload Activation{get;set;} }
        [DataContract] sealed class V3ActivationStartPayload { [DataMember(Name="id")] public string Id{get;set;} [DataMember(Name="challenge")] public string Challenge{get;set;} [DataMember(Name="expiresAt")] public long ExpiresAt{get;set;} [DataMember(Name="deviceProofRequired")] public bool DeviceProofRequired{get;set;} [DataMember(Name="publicKeyThumbprint")] public string PublicKeyThumbprint{get;set;} [DataMember(Name="owner")] public V3ActivationOwner Owner{get;set;} [DataMember(Name="organization")] public NamedEntity Organization{get;set;} [DataMember(Name="locations")] public V3ActivationLocation[] Locations{get;set;} }
        [DataContract] sealed class V3ActivationOwner { [DataMember(Name="displayName")] public string DisplayName{get;set;} }
        [DataContract] sealed class V3ActivationLocation { [DataMember(Name="id")] public string Id{get;set;} [DataMember(Name="name")] public string Name{get;set;} [DataMember(Name="deviceCount")] public int DeviceCount{get;set;} }

        [DataContract] sealed class ActivationStartRequest { [DataMember(Name="code")] public string Code{get;set;} [DataMember(Name="installationId")] public string InstallationId{get;set;} [DataMember(Name="fingerprint")] public string Fingerprint{get;set;} [DataMember(Name="appVersion")] public string AppVersion{get;set;} [DataMember(Name="platform")] public string Platform{get;set;} }
        [DataContract] sealed class ActivationCompleteRequest { [DataMember(Name="attemptId")] public string AttemptId{get;set;} [DataMember(Name="challenge")] public string Challenge{get;set;} [DataMember(Name="identifier")] public string Identifier{get;set;} [DataMember(Name="password")] public string Password{get;set;} [DataMember(Name="name")] public string Name{get;set;} [DataMember(Name="appVersion")] public string AppVersion{get;set;} [DataMember(Name="platform")] public string Platform{get;set;} }
        [DataContract] sealed class NamedEntity { [DataMember(Name="id")] public string Id{get;set;} [DataMember(Name="name")] public string Name{get;set;} }
        [DataContract] sealed class ActivationStartResponse { [DataMember(Name="attemptId")] public string AttemptId{get;set;} [DataMember(Name="challenge")] public string Challenge{get;set;} [DataMember(Name="expiresAt")] public long ExpiresAt{get;set;} [DataMember(Name="organization")] public NamedEntity Organization{get;set;} [DataMember(Name="location")] public NamedEntity Location{get;set;} }
        [DataContract] sealed class ActivationProfile { [DataMember(Name="id")] public string Id{get;set;} [DataMember(Name="installationId")] public string InstallationId{get;set;} [DataMember(Name="organizationId")] public string OrganizationId{get;set;} [DataMember(Name="locationId")] public string LocationId{get;set;} }
        [DataContract] sealed class ActivationDevice { [DataMember(Name="id")] public string Id{get;set;} [DataMember(Name="name")] public string Name{get;set;} }
        [DataContract] sealed class ActivationCompleteResponse { [DataMember(Name="profile")] public ActivationProfile Profile{get;set;} [DataMember(Name="device")] public ActivationDevice Device{get;set;} [DataMember(Name="credential")] public string Credential{get;set;} }
    }
}
