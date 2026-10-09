using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
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
    public sealed class PcSessionContext : IPcSessionContext
    {
        public PcSession Current { get; private set; }
        public event EventHandler Changed;

        public void SignIn(PcSession session)
        {
            Current = session ?? throw new ArgumentNullException(nameof(session));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SignOut()
        {
            Current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class PcAuthenticationService : IPcAuthenticationService
    {
#if DEBUG
        internal const string DevelopmentAdminEmail = "admin.app.test@myptbooth.local";
        internal const string DevelopmentAdminPassword = "PhotoBoothAdmin#2026";
        internal const string DevelopmentOperatorEmail = "operator.app.test@myptbooth.local";
        internal const string DevelopmentOperatorPassword = "PhotoBoothOperator#2026";
#endif

        static readonly HttpClient Http = ApiHttpTransport.Create(TimeSpan.FromSeconds(15));
        readonly IPcAccountRepository accounts;
        readonly IPcSessionContext sessions;
        readonly IDeviceIdentityRepository deviceIdentity;
        readonly IPasswordService passwords;
        readonly ApplicationOptions options;
        readonly HttpClient http = Http;
        readonly ICommercialEntitlementService entitlements;
        readonly object attemptGate = new object();
        readonly Dictionary<string, LocalAttemptState> localAttempts = new Dictionary<string, LocalAttemptState>(StringComparer.OrdinalIgnoreCase);

        public PcAuthenticationService(IPcAccountRepository accounts, IPcSessionContext sessions, IDeviceIdentityRepository deviceIdentity, IPasswordService passwords, ApplicationOptions options)
            : this(accounts, sessions, deviceIdentity, passwords, options, (ICommercialEntitlementService)null)
        {
        }

        public PcAuthenticationService(IPcAccountRepository accounts, IPcSessionContext sessions, IDeviceIdentityRepository deviceIdentity, IPasswordService passwords, ApplicationOptions options, ICommercialEntitlementService entitlementService)
        {
            this.accounts = accounts;
            this.sessions = sessions;
            this.deviceIdentity = deviceIdentity;
            this.passwords = passwords;
            this.options = options;
            entitlements = entitlementService;
        }

        internal PcAuthenticationService(IPcAccountRepository accounts, IPcSessionContext sessions, IDeviceIdentityRepository devices, IPasswordService passwords, ApplicationOptions options, HttpClient client)
            : this(accounts, sessions, devices, passwords, options, (ICommercialEntitlementService)null) { http = client; }

        public async Task<PcAuthenticationResult> AuthenticateAsync(string loginIdentifier, string password, bool rememberLogin, CancellationToken cancellationToken)
        {
            // Login is process-local, including callers from older UI integrations.
            await accounts.ClearRememberedLoginAsync(cancellationToken).ConfigureAwait(false);
            if (System.Text.RegularExpressions.Regex.IsMatch((loginIdentifier ?? string.Empty).Trim(), "^EV[0-9A-HJKMNP-TV-Z]{8}([0-9A-HJKMNP-TV-Z]{2})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var result = await new BoothAccessService(deviceIdentity, sessions, options, http).LoginEventAsync(loginIdentifier, password, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded) return Failed(result.Error);
                await accounts.ClearRememberedLoginAsync(cancellationToken).ConfigureAwait(false);
                var eventIdentity = await deviceIdentity.GetAsync(cancellationToken).ConfigureAwait(false);
                return new PcAuthenticationResult { Succeeded = true, Session = new PcSession {
                    AccountId = result.Authorization.AccessCodeId, DisplayName = result.Authorization.DisplayName,
                    OrganizationId = eventIdentity.OrganizationId, Role = PcAppRole.BoothOperator,
                    AuthenticationMode = PcAuthenticationMode.OnlineDeviceBound, AuthenticatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = result.Authorization.ExpiresAtUtc, RemoteSessionToken = result.Authorization.SessionToken,
                    EventAuthorization = result.Authorization } };
            }
            loginIdentifier = (loginIdentifier ?? string.Empty).Trim().ToLowerInvariant();
            if (loginIdentifier.Length == 0 || string.IsNullOrEmpty(password)) return Failed("Vui lòng nhập email hoặc tên đăng nhập và mật khẩu.");
            if (IsLocallyLocked(loginIdentifier)) return Failed("Đăng nhập tạm khóa do nhập sai nhiều lần. Hãy thử lại sau 5 phút.");

            var cached = await accounts.GetByEmailAsync(loginIdentifier, cancellationToken).ConfigureAwait(false);
            var identity = await deviceIdentity.GetAsync(cancellationToken).ConfigureAwait(false);
            var protectedCredential = await deviceIdentity.GetProtectedCredentialAsync(cancellationToken).ConfigureAwait(false);
            string onlineFailure = null;
            if (identity?.EnrollmentState == DeviceEnrollmentState.Revoked || identity?.EnrollmentState == DeviceEnrollmentState.Suspended)
                return Failed("Thiết bị đã bị thu hồi hoặc tạm ngưng. Không thể mở phiên quản trị.");

            if (identity?.EnrollmentState == DeviceEnrollmentState.Enrolled && protectedCredential?.Length > 0 && !string.IsNullOrWhiteSpace(options.PhotoApiBaseUrl))
            {
                try
                {
                    var online = await AuthenticateOnlineAsync(loginIdentifier, password, identity, protectedCredential, cancellationToken).ConfigureAwait(false);
                    if (!online.Result.Succeeded) return online.Result;
                    if (!string.Equals(identity.OrganizationId, online.Result.Session.OrganizationId, StringComparison.Ordinal))
                        return Failed("Tài khoản không thuộc doanh nghiệp đã đăng ký cho thiết bị này.");
                    if (entitlements != null)
                    {
                        var access = await entitlements.CheckAppAccessAsync(cancellationToken).ConfigureAwait(false);
                        if (!access.IsAllowed) return Failed(access.UserMessage);
                    }

                    ClearLocalFailures(loginIdentifier);
                    return online.Result;
                }
                catch (HttpRequestException) { onlineFailure = "Dịch vụ xác thực đang gặp sự cố. Vui lòng thử lại sau ít phút."; }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { onlineFailure = "Dịch vụ xác thực phản hồi quá chậm. Vui lòng thử lại."; }
                catch (IOException) { onlineFailure = "Không thể kết nối dịch vụ xác thực. Vui lòng kiểm tra mạng và thử lại."; }
            }

            if (cached == null || !cached.IsDevelopmentAccount)
            {
                return Failed(onlineFailure ?? "Thiết bị chưa được liên kết đầy đủ. Vui lòng mở trang quản lý và kiểm tra trạng thái thiết bị.");
            }

            if (!cached.IsActive || !passwords.Verify(password, cached.PasswordHash))
            {
                var locked = RegisterLocalFailure(loginIdentifier);
                return Failed(locked ? "Đăng nhập tạm khóa do nhập sai nhiều lần. Hãy thử lại sau 5 phút." : "Tên đăng nhập hoặc mật khẩu không đúng.");
            }

            var mode = cached.IsDevelopmentAccount ? PcAuthenticationMode.DevelopmentAccount : PcAuthenticationMode.OfflineCache;
            ClearLocalFailures(loginIdentifier);
            return Success(cached.AccountId, cached.DisplayName, cached.OrganizationId, cached.Role, mode, TimeSpan.FromHours(mode == PcAuthenticationMode.OfflineCache ? 4 : 8));
        }

        public async Task<PcAuthenticationResult> TryResumeAsync(CancellationToken cancellationToken)
        {
            // Purge legacy persistence before any window can open. Never restore authority.
            await accounts.ClearRememberedLoginAsync(cancellationToken).ConfigureAwait(false);
            sessions.SignOut();
            return Failed("Vui lòng đăng nhập để bắt đầu phiên mới.");
        }

        [DataContract] sealed class SessionHandoff
        {
            [DataMember] public PcSession Session { get; set; }
            [DataMember] public DateTime ExpiresAtUtc { get; set; }
        }

        public Task<string> CreateHandoffAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var current = sessions.Current;
            if (current == null || current.IsExpired || current.AuthenticationMode != PcAuthenticationMode.OnlineDeviceBound || string.IsNullOrWhiteSpace(current.RemoteSessionToken))
                throw new InvalidOperationException("Vui lòng đăng nhập nhân viên hoặc sự kiện trước khi mở MiuPhotoBooth.");
            var data = Json(new SessionHandoff { Session = current, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1) });
            return Task.FromResult(Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(data), Encoding.UTF8.GetBytes("MiuPhotoBooth.Handoff.v1"), DataProtectionScope.CurrentUser)));
        }

        public async Task<PcAuthenticationResult> AcceptHandoffAsync(string protectedHandoff, CancellationToken token)
        {
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedHandoff), Encoding.UTF8.GetBytes("MiuPhotoBooth.Handoff.v1"), DataProtectionScope.CurrentUser);
                var handoff = Read<SessionHandoff>(Encoding.UTF8.GetString(data));
                var session = handoff?.Session;
                var identity = await deviceIdentity.GetAsync(token).ConfigureAwait(false);
                if (session == null || handoff.ExpiresAtUtc <= DateTime.UtcNow || handoff.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2) || session.IsExpired ||
                    session.Role != PcAppRole.BoothOperator || session.AuthenticationMode != PcAuthenticationMode.OnlineDeviceBound || identity?.EnrollmentState != DeviceEnrollmentState.Enrolled || identity.OrganizationId != session.OrganizationId)
                    return Failed("Phiên mở từ trang quản lý đã hết hạn. Vui lòng mở lại.");
                if (session.EventAuthorization != null)
                {
                    if (session.EventAuthorization.SessionToken != session.RemoteSessionToken) return Failed("Phiên sự kiện không hợp lệ.");
                    var validation = await new BoothAccessService(deviceIdentity, sessions, options, http).ValidateAsync(session.EventAuthorization, token).ConfigureAwait(false);
                    if (!validation.Succeeded) return Failed(validation.Error);
                    session.ExpiresAtUtc = validation.Authorization.ExpiresAtUtc;
                }
                else
                {
                    var credential = UnprotectDeviceCredential(await deviceIdentity.GetProtectedCredentialAsync(token).ConfigureAwait(false));
                    using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.PhotoApiBaseUrl.TrimEnd('/') + "/"), "api/v3/device/employee-session")))
                    {
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                        request.Headers.TryAddWithoutValidation("X-Employee-Session", session.RemoteSessionToken);
                        await DeviceRequestProof.SignAsync(request, deviceIdentity, credential, null, token).ConfigureAwait(false);
                        using (var response = await http.SendAsync(request, token).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode) return Failed(ApiErrorMessage(body));
                            var value = Read<VerifySessionResponse>(body);
                            if (value?.Valid != true || value.Device?.Id != identity.DeviceId || value.Employee?.Id != session.AccountId || !TryParseRemoteRole(value.AppRole, out var role)) return Failed("Phiên nhân viên không hợp lệ.");
                            session.Role = role;
                            session.ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(value.Employee.ExpiresAt).UtcDateTime;
                        }
                    }
                }
                return new PcAuthenticationResult { Succeeded = true, Session = session };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return Failed("Không thể xác minh phiên từ trang quản lý. Vui lòng mở lại khi có mạng."); }
        }

        public async Task ForgetAsync(CancellationToken cancellationToken)
        {
            // Local cleanup must complete even when offline or remote logout is cancelled.
            var outgoing = sessions.Current;
            sessions.SignOut();
            await accounts.ClearRememberedLoginAsync(CancellationToken.None).ConfigureAwait(false);
            if (outgoing == null || outgoing.EventAuthorization != null || string.IsNullOrWhiteSpace(outgoing.RemoteSessionToken)) return;
            try
            {
                var identity = await deviceIdentity.GetAsync(cancellationToken).ConfigureAwait(false);
                var protectedCredential = await deviceIdentity.GetProtectedCredentialAsync(cancellationToken).ConfigureAwait(false);
                if (identity?.EnrollmentState != DeviceEnrollmentState.Enrolled || protectedCredential?.Length <= 0 || string.IsNullOrWhiteSpace(options.PhotoApiBaseUrl)) return;
                var deviceToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedCredential, null, DataProtectionScope.CurrentUser));
                using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.PhotoApiBaseUrl.TrimEnd('/') + "/"), "api/v3/device/employees/logout")))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);
                    request.Headers.TryAddWithoutValidation("X-Employee-Session", outgoing.RemoteSessionToken);
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    await DeviceRequestProof.SignAsync(request, deviceIdentity, deviceToken, null, cancellationToken).ConfigureAwait(false);
                    using (await http.SendAsync(request, cancellationToken).ConfigureAwait(false)) { }
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
            catch (CryptographicException) { }
        }

        public async Task EnsureDevelopmentAccountsAsync(CancellationToken cancellationToken)
        {
#if DEBUG
            await EnsureDevelopmentAccountAsync("test_pc_admin", DevelopmentAdminEmail, "Admin App Test", PcAppRole.LocalAdmin, DevelopmentAdminPassword, cancellationToken).ConfigureAwait(false);
            await EnsureDevelopmentAccountAsync("test_pc_operator", DevelopmentOperatorEmail, "Operator App Test", PcAppRole.BoothOperator, DevelopmentOperatorPassword, cancellationToken).ConfigureAwait(false);
#else
            await Task.CompletedTask;
#endif
        }

        async Task EnsureDevelopmentAccountAsync(string id, string email, string name, PcAppRole role, string password, CancellationToken token)
        {
            if (await accounts.GetByEmailAsync(email, token).ConfigureAwait(false) != null) return;
            await accounts.UpsertAsync(new PcAccountRecord
            {
                AccountId = id,
                Email = email,
                DisplayName = name,
                Role = role,
                PasswordHash = passwords.Hash(password),
                IsActive = true,
                IsDevelopmentAccount = true,
                UpdatedAtUtc = DateTime.UtcNow
            }, token).ConfigureAwait(false);
        }

        async Task<RemoteAuthentication> AuthenticateOnlineAsync(string loginIdentifier, string pin, DeviceIdentity identity, byte[] protectedCredential, CancellationToken cancellationToken)
        {
            string credential;
            try { credential = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedCredential, null, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { return RemoteFailed("Credential của thiết bị không đọc được. Hãy đăng ký lại thiết bị.", true); }

            var requestBody = Json(new LoginRequest { Username = loginIdentifier, Pin = pin });
            var endpoint = new Uri(new Uri(options.PhotoApiBaseUrl.TrimEnd('/') + "/"), "api/v3/device/employees/login");
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                await DeviceRequestProof.SignAsync(request,deviceIdentity,credential,null,cancellationToken).ConfigureAwait(false);
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.BadRequest || (int)response.StatusCode == 423 || (int)response.StatusCode == 429)
                        return RemoteFailed(ApiErrorMessage(body) ?? "Tên đăng nhập hoặc mật khẩu không đúng.", false);
                    if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                        return RemoteFailed(ApiErrorMessage(body) ?? "Hệ thống đang được cập nhật. Vui lòng thử lại sau ít phút.", false);
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException("Dịch vụ đăng nhập tạm thời không phản hồi (mã " + (int)response.StatusCode + ").");

                    var value = Read<LoginResponse>(body);
                    if (value?.Employee == null || value.Device == null || string.IsNullOrWhiteSpace(value.SessionToken) || !value.ExpiresAt.HasValue)
                        return RemoteFailed("Không thể tạo phiên đăng nhập. Vui lòng thử lại.", true);
                    if(!string.Equals(identity.DeviceId,value.Device.Id,StringComparison.Ordinal))return RemoteFailed("Phiên đăng nhập không thuộc thiết bị này. Vui lòng mở trang quản lý để kiểm tra.",true);
                    if (!TryParseRemoteRole(value.AppRole, out var role)) return RemoteFailed("Tài khoản chưa được cấp quyền sử dụng MiuPhotoBooth.", true);
                    return RemoteSuccess(value,identity.OrganizationId,role);
                }
            }
        }

        static string UnprotectDeviceCredential(byte[] protectedCredential)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedCredential, null, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { return null; }
        }

        static RemoteAuthentication RemoteSuccess(LoginResponse value,string organizationId,PcAppRole role)
        {
            var result = new RemoteAuthentication
            {
                Result = Success(value.Employee.Id, value.Employee.DisplayName, organizationId, role, PcAuthenticationMode.OnlineDeviceBound, TimeSpan.FromHours(12)),
                SessionToken = value.SessionToken,
                SessionExpiresAtUtc = value.ExpiresAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(value.ExpiresAt.Value).UtcDateTime : (DateTime?)null
            };
            result.Result.Session.RemoteSessionToken = value.SessionToken;
            result.Result.Session.ExpiresAtUtc = result.SessionExpiresAtUtc.Value;
            return result;
        }

        static RemoteAuthentication RemoteFailed(string error, bool shouldForget) => new RemoteAuthentication { Result = Failed(error), ShouldForget = shouldForget };

        internal static bool TryParseRemoteRole(string value, out PcAppRole role)
        {
            if (string.Equals(value, "BoothOperator", StringComparison.Ordinal)) { role = PcAppRole.BoothOperator; return true; }
            role = default(PcAppRole);
            return false;
        }

        static PcAuthenticationResult Success(string id, string name, string organizationId, PcAppRole role, PcAuthenticationMode mode, TimeSpan lifetime)
        {
            var now = DateTime.UtcNow;
            return new PcAuthenticationResult
            {
                Succeeded = true,
                Session = new PcSession { AccountId = id, DisplayName = name, OrganizationId = organizationId, Role = role, AuthenticationMode = mode, AuthenticatedAtUtc = now, ExpiresAtUtc = now.Add(lifetime) }
            };
        }

        static PcAuthenticationResult Failed(string error) => new PcAuthenticationResult { Succeeded = false, Error = error };

        bool IsLocallyLocked(string email)
        {
            lock (attemptGate)
            {
                if (!localAttempts.TryGetValue(email, out var state)) return false;
                if (state.LockedUntilUtc > DateTime.UtcNow) return true;
                if (DateTime.UtcNow - state.WindowStartedUtc > TimeSpan.FromMinutes(5)) localAttempts.Remove(email);
                return false;
            }
        }

        bool RegisterLocalFailure(string email)
        {
            lock (attemptGate)
            {
                var now = DateTime.UtcNow;
                if (!localAttempts.TryGetValue(email, out var state) || now - state.WindowStartedUtc > TimeSpan.FromMinutes(5))
                    state = new LocalAttemptState { WindowStartedUtc = now };
                state.FailedCount++;
                if (state.FailedCount >= 5) state.LockedUntilUtc = now.AddMinutes(5);
                localAttempts[email] = state;
                return state.LockedUntilUtc > now;
            }
        }

        void ClearLocalFailures(string email) { lock (attemptGate) localAttempts.Remove(email); }
        static string Json<T>(T value) { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
        static T Read<T>(string value) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value ?? string.Empty))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        static string ApiErrorMessage(string value)
        {
            try
            {
                switch (Read<ErrorEnvelope>(value)?.Error?.Code)
                {
                    case "invalid_employee_credentials": return "Tên đăng nhập hoặc mật khẩu không đúng.";
                    case "employee_login_locked": return "Tài khoản tạm khóa do nhập sai nhiều lần. Vui lòng thử lại sau ít phút.";
                    case "device_revoked": return "Thiết bị đã bị thu hồi. Vui lòng liên hệ quản lý.";
                    case "device_locked": return "Thiết bị đang bị khóa tạm thời. Chủ tài khoản cần xác minh lại trên đúng thiết bị để mở khóa.";
                    case "device_proof_required":
                    case "device_proof_invalid":
                    case "device_proof_replayed": return "Không thể xác minh khóa bảo mật của thiết bị. Hãy kết nối lại thiết bị.";
                    case "device_suspended": return "Thiết bị đang tạm ngưng. Vui lòng liên hệ quản lý.";
                    case "account_verification_required": return "Vui lòng xác minh email trong Web Customer trước khi tiếp tục.";
                    case "account_suspended":
                    case "account_locked": return "Tài khoản hiện đang bị khóa. Vui lòng liên hệ quản lý.";
                    case "account_closed": return "Tài khoản không còn hoạt động. Vui lòng liên hệ quản lý.";
                    case "subscription_required":
                    case "subscription_app_access_expired": return "Gói sử dụng đã hết hạn. Vui lòng liên hệ chủ tài khoản hoặc nhà cung cấp để gia hạn.";
                    case "schema_not_ready": return "Hệ thống đang được cập nhật. Vui lòng thử lại sau ít phút.";
                    default: return "Không thể đăng nhập lúc này. Vui lòng thử lại.";
                }
            }
            catch { return "Không thể đăng nhập lúc này. Vui lòng thử lại."; }
        }

        [DataContract] sealed class LoginRequest { [DataMember(Name = "username")] public string Username { get; set; } [DataMember(Name = "pin")] public string Pin { get; set; } }
        [DataContract] sealed class LoginResponse { [DataMember(Name = "sessionToken")] public string SessionToken { get; set; } [DataMember(Name = "expiresAt")] public long? ExpiresAt { get; set; } [DataMember(Name = "appRole")] public string AppRole { get; set; } [DataMember(Name = "employee")] public LoginEmployee Employee { get; set; } [DataMember(Name = "device")] public LoginDevice Device { get; set; } }
        [DataContract] sealed class LoginEmployee { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "displayName")] public string DisplayName { get; set; } }
        [DataContract] sealed class LoginDevice { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "locationId")] public string LocationId { get; set; } }
        [DataContract] sealed class VerifySessionResponse { [DataMember(Name = "valid")] public bool Valid { get; set; } [DataMember(Name = "appRole")] public string AppRole { get; set; } [DataMember(Name = "employee")] public VerifiedEmployee Employee { get; set; } [DataMember(Name = "device")] public LoginDevice Device { get; set; } }
        [DataContract] sealed class VerifiedEmployee { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "displayName")] public string DisplayName { get; set; } [DataMember(Name = "expiresAt")] public long ExpiresAt { get; set; } }
        [DataContract] sealed class ErrorEnvelope { [DataMember(Name = "error")] public ApiError Error { get; set; } }
        [DataContract] sealed class ApiError { [DataMember(Name = "code")] public string Code { get; set; } [DataMember(Name = "message")] public string Message { get; set; } }
        sealed class LocalAttemptState { public int FailedCount { get; set; } public DateTime WindowStartedUtc { get; set; } public DateTime LockedUntilUtc { get; set; } }
        sealed class RemoteAuthentication { public PcAuthenticationResult Result { get; set; } public string SessionToken { get; set; } public DateTime? SessionExpiresAtUtc { get; set; } public bool ShouldForget { get; set; } }
    }
}
