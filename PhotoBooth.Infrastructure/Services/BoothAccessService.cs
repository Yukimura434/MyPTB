using System;
using System.IO;
using System.Net.Http;
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
    public sealed class BoothAccessService : IBoothAccessService
    {
        static readonly HttpClient Http = ApiHttpTransport.Create(TimeSpan.FromSeconds(15));
        readonly IDeviceIdentityRepository devices;
        readonly IPcSessionContext sessions;
        readonly ApplicationOptions options;
        readonly HttpClient http;

        public BoothAccessService(IDeviceIdentityRepository devices, IPcSessionContext sessions, ApplicationOptions options)
            : this(devices, sessions, options, Http)
        {
        }

        internal BoothAccessService(IDeviceIdentityRepository devices, IPcSessionContext sessions, ApplicationOptions options, HttpClient http)
        {
            this.devices = devices;
            this.sessions = sessions;
            this.options = options;
            this.http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<BoothAccessResult> RedeemAsync(string code, CancellationToken cancellationToken)
        {
            code = (code ?? string.Empty).Trim().ToUpperInvariant();
            if (code.Length != 8) return Failed("Mã sử dụng phải gồm đúng 8 ký tự.");
            try
            {
                var credential = await DeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
                var value = await SendAsync<RedeemResponse>("api/v3/device/booth-access-codes/redeem", new RedeemRequest { Code = code }, credential, null, cancellationToken).ConfigureAwait(false);
                if (value?.Authorization == null || string.IsNullOrWhiteSpace(value.Authorization.SessionToken))
                    return Failed("Máy chủ trả về phiên sử dụng không hợp lệ.");
                var remote = value.Authorization;
                var codeType = ResolveCodeType(remote.CodeType, remote.Reusable);
                if(codeType==BoothAccessCodeType.Event||remote.ExpiresAt<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return Failed("Mã đăng nhập không hợp lệ hoặc đã hết hạn.");
                return new BoothAccessResult
                {
                    Succeeded = true,
                    Authorization = new BoothAccessAuthorization
                    {
                        CodeType = codeType,
                        AccessCodeId = remote.AccessCodeId,
                        AlbumUrl = remote.AlbumUrl,
                        EventId = remote.EventId,
                        BoothSessionId = remote.BoothSessionId,
                        SessionToken = remote.SessionToken,
                        DisplayName = codeType == BoothAccessCodeType.SelfBooth ? "Khách SelfBooth" : "Khách PhotoBooth",
                        ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(remote.ExpiresAt).UtcDateTime,
                        Settings = remote.ToSettings()
                    }
                };
            }
            catch (BoothAccessException e) { return Failed(e.Message); }
            catch (HttpRequestException) { return Failed("Không thể kết nối dịch vụ. Hãy kiểm tra mạng và thử lại."); }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed("Dịch vụ phản hồi quá chậm. Hãy thử lại."); }
            catch (CryptographicException) { return Failed("Thông tin xác thực thiết bị không còn đọc được. Hãy đăng ký lại thiết bị."); }
            catch (SerializationException) { return Failed("Dịch vụ trả về phiên đăng nhập không hợp lệ."); }
        }

        public async Task<BoothAccessResult> LoginEventAsync(string code, string password, CancellationToken token)
        {
            try
            {
                var credential = await DeviceCredentialAsync(token).ConfigureAwait(false);
                var value = await SendAsync<RedeemResponse>("api/v3/device/events/login", new EventLoginRequest { Code = (code ?? "").Trim().ToUpperInvariant(), Password = password }, credential, null, token).ConfigureAwait(false);
                var remote = value?.Authorization;
                if (remote?.CodeType != "event" || string.IsNullOrWhiteSpace(remote.SessionToken) || string.IsNullOrWhiteSpace(remote.EventId) || remote.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return Failed("Phiên sự kiện không hợp lệ hoặc đã hết hạn.");
                return new BoothAccessResult { Succeeded = true, Authorization = new BoothAccessAuthorization {
                    CodeType = BoothAccessCodeType.Event, AccessCodeId = remote.AccessCodeId, EventId = remote.EventId,
                    DisplayName = remote.EventName ?? "Nhân viên sự kiện", SessionToken = remote.SessionToken,
                    ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(remote.ExpiresAt).UtcDateTime, Settings = remote.ToSettings() } };
            }
            catch (BoothAccessException e) { return Failed(e.Message); }
            catch (HttpRequestException) { return Failed("Không thể kết nối để đăng nhập sự kiện. Vui lòng thử lại."); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { return Failed("Dịch vụ phản hồi quá chậm. Vui lòng thử lại."); }
            catch (SerializationException) { return Failed("Dịch vụ trả về phiên sự kiện không hợp lệ."); }
            catch (CryptographicException) { return Failed("Không thể xác minh thiết bị. Vui lòng mở trang quản lý."); }
        }

        public async Task<BoothAccessResult> ValidateAsync(BoothAccessAuthorization authorization, CancellationToken token)
        {
            if (authorization == null || string.IsNullOrWhiteSpace(authorization.SessionToken)) return Failed("Vui lòng đăng nhập lại.");
            try
            {
                var credential = await DeviceCredentialAsync(token).ConfigureAwait(false);
                var value = await SendAsync<StatusResponse>("api/v3/device/booth-access-session", null, credential, authorization.SessionToken, token).ConfigureAwait(false);
                var remote = value?.Authorization;
                if (value?.Valid != true || remote == null || ResolveCodeType(remote.CodeType, remote.Reusable) != authorization.CodeType ||
                    remote.AccessCodeId != authorization.AccessCodeId || remote.EventId != authorization.EventId || remote.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return Failed("Phiên đăng nhập không còn hiệu lực. Vui lòng đăng nhập lại.");
                authorization.ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(remote.ExpiresAt).UtcDateTime;
                authorization.BoothSessionId = remote.BoothSessionId;
                return new BoothAccessResult { Succeeded = true, Authorization = authorization };
            }
            catch (BoothAccessException e) { return Failed(e.Message); }
            catch (HttpRequestException) { return Failed("Cần kết nối mạng để tiếp tục phiên đăng nhập bằng mã."); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { return Failed("Không thể xác minh phiên. Vui lòng thử lại."); }
            catch (SerializationException) { return Failed("Dịch vụ trả về phiên không hợp lệ."); }
            catch (CryptographicException) { return Failed("Không thể xác minh thiết bị. Vui lòng mở trang quản lý."); }
        }

        public Task<CreatedBoothAccessCode> CreatePhotoBoothCodeAsync(CreatePhotoBoothCodeRequest request, CancellationToken cancellationToken)
        {
            var settings = request?.Settings ?? new BoothAccessSettings();
            return CreateAsync("api/v3/device/photobooth-codes", new CreatePhotoBoothRequest
            {
                EventId = request?.EventId,
                LocationId = request?.LocationId,
                PhotoCount = Clamp(settings.PhotoCount, 1, 8),
                CountdownSeconds = Clamp(settings.CountdownSeconds, 1, 10),
                GifFrameDurationMilliseconds = Clamp(settings.GifFrameDurationMilliseconds, 200, 5000),
                ResultDisplaySeconds = Clamp(settings.ResultDisplaySeconds, 10, 600),
                PrintCopies = Clamp(settings.PrintCopies, 0, 10)
            }, BoothAccessCodeType.PhotoBooth, cancellationToken);
        }

        public Task<CreatedBoothAccessCode> CreateSelfBoothPassAsync(CreateSelfBoothPassRequest request, CancellationToken cancellationToken)
        {
            var settings = request?.Settings ?? new BoothAccessSettings { GifFrameDurationMilliseconds = 0, PrintCopies = 0 };
            return CreateAsync("api/v3/device/selfbooth-passes", new CreateSelfBoothRequest
            {
                EventId = request?.EventId,
                LocationId = request?.LocationId,
                DurationMinutes = Clamp(request?.DurationMinutes ?? 120, 30, 10080),
                PhotoCount = Clamp(settings.PhotoCount, 1, 8),
                CountdownSeconds = Clamp(settings.CountdownSeconds, 1, 10),
                ResultDisplaySeconds = Clamp(settings.ResultDisplaySeconds, 10, 600),
                PrintCopies = Clamp(settings.PrintCopies, 0, 10)
            }, BoothAccessCodeType.SelfBooth, cancellationToken);
        }

        async Task<CreatedBoothAccessCode> CreateAsync(string path, object payload, BoothAccessCodeType expectedCodeType, CancellationToken cancellationToken)
        {
            if (sessions.Current == null || string.IsNullOrWhiteSpace(sessions.Current.RemoteSessionToken))
                throw new InvalidOperationException("Trang quản lý cần đăng nhập quản lý trực tuyến để tạo mã.");
            var credential = await DeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
            var value = await SendAsync<CreateResponse>(path, payload, credential, sessions.Current.RemoteSessionToken, cancellationToken).ConfigureAwait(false);
            if (value?.Session == null || string.IsNullOrWhiteSpace(value.Code)) throw new InvalidOperationException("Máy chủ không trả về mã sử dụng.");
            var codeType = ResolveCodeType(value.Session.CodeType, value.Session.Reusable);
            if (codeType != expectedCodeType) throw new InvalidOperationException("Máy chủ trả về loại mã không phù hợp.");
            return new CreatedBoothAccessCode
            {
                CodeType = codeType,
                Id = value.Session.Id,
                Code = value.Code,
                ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(value.Session.ExpiresAt).UtcDateTime,
                AlbumUrl = value.Session.AlbumUrl,
                Settings = value.Session.ToSettings()
            };
        }

        async Task<string> DeviceCredentialAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(options.PhotoApiBaseUrl)) throw new BoothAccessException("Chưa cấu hình địa chỉ dịch vụ.");
            var identity = await devices.GetAsync(token).ConfigureAwait(false);
            var protectedCredential = await devices.GetProtectedCredentialAsync(token).ConfigureAwait(false);
            if (identity?.EnrollmentState != DeviceEnrollmentState.Enrolled || protectedCredential?.Length <= 0)
                throw new BoothAccessException("Thiết bị chưa được xác thực trong trang quản lý.");
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedCredential, null, DataProtectionScope.CurrentUser));
        }

        async Task<T> SendAsync<T>(string path, object payload, string deviceToken, string employeeToken, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(payload == null ? HttpMethod.Get : HttpMethod.Post, new Uri(new Uri(options.PhotoApiBaseUrl.TrimEnd('/') + "/"), path)))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);
                if (!string.IsNullOrWhiteSpace(employeeToken)) request.Headers.TryAddWithoutValidation("X-Employee-Session", employeeToken);
                if (payload != null) request.Content = new StringContent(Json(payload), Encoding.UTF8, "application/json");
                await DeviceRequestProof.SignAsync(request,devices,deviceToken,null,token).ConfigureAwait(false);
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw new BoothAccessException(ErrorMessage(body));
                    return Read<T>(body);
                }
            }
        }

        static BoothAccessResult Failed(string error) => new BoothAccessResult { Error = error };
        static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
        static BoothAccessCodeType ResolveCodeType(string value, bool reusable)
        {
            if (string.Equals(value, "selfbooth", StringComparison.OrdinalIgnoreCase)) return BoothAccessCodeType.SelfBooth;
            if (string.Equals(value, "photobooth", StringComparison.OrdinalIgnoreCase)) return BoothAccessCodeType.PhotoBooth;
            if (string.Equals(value, "event", StringComparison.OrdinalIgnoreCase)) return BoothAccessCodeType.Event;
            throw new SerializationException("Unknown booth authorization type.");
        }
        static string Json(object value) { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(value.GetType()).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
        static T Read<T>(string value) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value ?? string.Empty))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        static string ErrorMessage(string value)
        {
            try
            {
                switch (Read<ApiError>(value)?.Error?.Code)
                {
                    case "invalid_event_credentials": return "Mã sự kiện hoặc mật khẩu không đúng.";
                    case "event_not_started": return "Sự kiện chưa đến thời gian sử dụng.";
                    case "event_ended":
                    case "event_not_open":
                    case "event_session_revoked": return "Sự kiện đã kết thúc hoặc phiên đã bị thu hồi.";
                    case "configuration_required": return "Sự kiện chưa có cấu hình chụp. Vui lòng liên hệ chủ tài khoản.";
                    case "invalid_booth_code": return "Mã đăng nhập không đúng hoặc đã hết hạn.";
                    case "invalid_selfbooth_code": return "Mã đăng nhập không đúng hoặc đã hết hạn.";
                    case "booth_code_locked": return "Bạn đã nhập sai quá nhiều lần. Vui lòng thử lại sau ít phút.";
                    case "booth_code_already_used": return "Mã đăng nhập này đã được sử dụng.";
                    case "booth_code_in_progress": return "Phiên chụp này đang được xử lý trên thiết bị đã đăng nhập.";
                    case "selfbooth_pass_expired": return "Mã SelfBooth đã hết hiệu lực.";
                    case "selfbooth_location_mismatch": return "Mã SelfBooth không dùng được tại cửa hàng này.";
                    case "manager_required": return "Tài khoản cần quyền quản lý để tạo mã.";
                    case "device_locked": return "Thiết bị đang bị khóa tạm thời. Chủ tài khoản cần xác minh lại trên đúng thiết bị để mở khóa.";
                    case "device_proof_required":
                    case "device_proof_invalid":
                    case "device_proof_replayed": return "Không thể xác minh khóa bảo mật của thiết bị. Hãy kết nối lại thiết bị.";
                    case "account_verification_required": return "Vui lòng xác minh email trong Web Customer trước khi tiếp tục.";
                    case "account_suspended":
                    case "account_locked": return "Tài khoản hiện đang bị khóa. Vui lòng liên hệ quản lý.";
                    case "account_closed": return "Tài khoản không còn hoạt động. Vui lòng liên hệ quản lý.";
                    case "subscription_required":
                    case "subscription_app_access_expired": return "Gói sử dụng đã hết hạn. Vui lòng liên hệ chủ tài khoản hoặc nhà cung cấp để gia hạn.";
                    case "authentication_required": return "Thiết bị chưa sẵn sàng. Vui lòng mở trang quản lý để kiểm tra.";
                    default: return "Không thể xử lý yêu cầu lúc này. Vui lòng thử lại.";
                }
            }
            catch { return "Không thể xử lý yêu cầu lúc này. Vui lòng thử lại."; }
        }

        sealed class BoothAccessException : Exception { public BoothAccessException(string message) : base(message) { } }
        [DataContract] sealed class RedeemRequest { [DataMember(Name = "code")] public string Code { get; set; } }
        [DataContract] sealed class EventLoginRequest { [DataMember(Name = "code")] public string Code { get; set; } [DataMember(Name = "password")] public string Password { get; set; } }
        [DataContract] sealed class StatusResponse { [DataMember(Name = "valid")] public bool Valid { get; set; } [DataMember(Name = "authorization")] public RemoteAuthorization Authorization { get; set; } }
        [DataContract] sealed class RedeemResponse { [DataMember(Name = "authorization")] public RemoteAuthorization Authorization { get; set; } }
        [DataContract] sealed class CreateResponse { [DataMember(Name = "session")] public RemoteSession Session { get; set; } [DataMember(Name = "code")] public string Code { get; set; } }
        [DataContract] sealed class CreatePhotoBoothRequest
        {
            [DataMember(Name = "locationId", EmitDefaultValue = false)] public string LocationId { get; set; }
            [DataMember(Name = "eventId", EmitDefaultValue = false)] public string EventId { get; set; }
            [DataMember(Name = "photoCount")] public int PhotoCount { get; set; }
            [DataMember(Name = "countdownSeconds")] public int CountdownSeconds { get; set; }
            [DataMember(Name = "gifFrameDurationMilliseconds")] public int GifFrameDurationMilliseconds { get; set; }
            [DataMember(Name = "resultDisplaySeconds")] public int ResultDisplaySeconds { get; set; }
            [DataMember(Name = "printCopies")] public int PrintCopies { get; set; }
        }
        [DataContract] sealed class CreateSelfBoothRequest
        {
            [DataMember(Name = "locationId", EmitDefaultValue = false)] public string LocationId { get; set; }
            [DataMember(Name = "eventId", EmitDefaultValue = false)] public string EventId { get; set; }
            [DataMember(Name = "durationMinutes")] public int DurationMinutes { get; set; }
            [DataMember(Name = "photoCount")] public int PhotoCount { get; set; }
            [DataMember(Name = "countdownSeconds")] public int CountdownSeconds { get; set; }
            [DataMember(Name = "resultDisplaySeconds")] public int ResultDisplaySeconds { get; set; }
            [DataMember(Name = "printCopies")] public int PrintCopies { get; set; }
        }
        [DataContract] sealed class RemoteAuthorization : RemoteSettings
        {
            [DataMember(Name = "accessCodeId")] public string AccessCodeId { get; set; }
            [DataMember(Name = "eventId")] public string EventId { get; set; }
            [DataMember(Name = "eventName")] public string EventName { get; set; }
            [DataMember(Name = "boothSessionId")] public string BoothSessionId { get; set; }
            [DataMember(Name = "sessionToken")] public string SessionToken { get; set; }
            [DataMember(Name = "expiresAt")] public long ExpiresAt { get; set; }
            [DataMember(Name = "albumUrl")] public string AlbumUrl { get; set; }
            [DataMember(Name = "reusable")] public bool Reusable { get; set; }
            [DataMember(Name = "codeType")] public string CodeType { get; set; }
        }
        [DataContract] sealed class RemoteSession : RemoteSettings
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "expiresAt")] public long ExpiresAt { get; set; }
            [DataMember(Name = "albumUrl")] public string AlbumUrl { get; set; }
            [DataMember(Name = "reusable")] public bool Reusable { get; set; }
            [DataMember(Name = "codeType")] public string CodeType { get; set; }
        }
        [DataContract] class RemoteSettings
        {
            [DataMember(Name = "photoCount")] public int PhotoCount { get; set; }
            [DataMember(Name = "countdownSeconds")] public int CountdownSeconds { get; set; }
            [DataMember(Name = "gifFrameDurationMilliseconds")] public int GifFrameDurationMilliseconds { get; set; }
            [DataMember(Name = "resultDisplaySeconds")] public int ResultDisplaySeconds { get; set; }
            [DataMember(Name = "printCopies")] public int PrintCopies { get; set; }
            public BoothAccessSettings ToSettings() => new BoothAccessSettings { PhotoCount = PhotoCount, CountdownSeconds = CountdownSeconds, GifFrameDurationMilliseconds = GifFrameDurationMilliseconds, ResultDisplaySeconds = ResultDisplaySeconds, PrintCopies = PrintCopies };
        }
        [DataContract] sealed class ApiError { [DataMember(Name = "error")] public ApiErrorBody Error { get; set; } }
        [DataContract] sealed class ApiErrorBody { [DataMember(Name = "code")] public string Code { get; set; } [DataMember(Name = "message")] public string Message { get; set; } }
    }
}
