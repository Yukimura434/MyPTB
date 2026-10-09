using System;
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
    public sealed class CommercialEntitlementService : ICommercialEntitlementService
    {
        static readonly HttpClient SharedHttp = ApiHttpTransport.Create(TimeSpan.FromSeconds(20));
        static readonly TimeSpan DefaultRevalidationInterval = TimeSpan.FromMinutes(15);
        static readonly TimeSpan DefaultMaximumOfflineWindow = TimeSpan.FromDays(7);
        static readonly byte[] CacheEntropy = Encoding.UTF8.GetBytes("PhotoBooth.CommercialEntitlementCache.v1");
        const int CacheFormatVersion = 1;
        const int MaximumCacheBytes = 512 * 1024;
        readonly IDeviceIdentityRepository identities;
        readonly ApplicationOptions options;
        readonly HttpClient http;
        readonly Func<DateTime> utcNow;
        readonly TimeSpan revalidationInterval;
        readonly TimeSpan maximumOfflineWindow;
        readonly string cachePath;
        string cachedDeviceId;
        string cachedOrganizationId;

        public CommercialEntitlement LastVerified { get; private set; }
        public DateTime? LastVerifiedAtUtc { get; private set; }

        public CommercialEntitlementService(IDeviceIdentityRepository identityRepository, ApplicationOptions applicationOptions)
            : this(identityRepository, applicationOptions, SharedHttp, () => DateTime.UtcNow, DefaultRevalidationInterval, DefaultMaximumOfflineWindow)
        {
        }

        internal CommercialEntitlementService(IDeviceIdentityRepository identityRepository, ApplicationOptions applicationOptions, HttpClient httpClient)
            : this(identityRepository, applicationOptions, httpClient, () => DateTime.UtcNow, DefaultRevalidationInterval, DefaultMaximumOfflineWindow)
        {
        }

        internal CommercialEntitlementService(IDeviceIdentityRepository identityRepository, ApplicationOptions applicationOptions, HttpClient httpClient,
            Func<DateTime> utcNowProvider, TimeSpan revalidation, TimeSpan maximumOffline)
        {
            identities = identityRepository ?? throw new ArgumentNullException(nameof(identityRepository));
            options = applicationOptions ?? throw new ArgumentNullException(nameof(applicationOptions));
            http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            utcNow = utcNowProvider ?? throw new ArgumentNullException(nameof(utcNowProvider));
            if (revalidation <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(revalidation));
            if (maximumOffline < revalidation) throw new ArgumentOutOfRangeException(nameof(maximumOffline));
            revalidationInterval = revalidation;
            maximumOfflineWindow = maximumOffline;
            cachePath = ResolveCachePath(options.DataDirectory);
            TryLoadCache();
        }

        public async Task<CommercialEntitlement> GetCurrentAsync(CancellationToken cancellationToken)
        {
            var identity = await identities.GetAsync(cancellationToken).ConfigureAwait(false);
            if (identity?.EnrollmentState != DeviceEnrollmentState.Enrolled || string.IsNullOrWhiteSpace(identity.DeviceId))
                throw new AccessException(CommercialAccessBlockReason.DeviceNotEnrolled,
                    "Thiết bị chưa được kết nối với cửa hàng. Vui lòng thiết lập thiết bị trong trang quản lý.");
            if (string.IsNullOrWhiteSpace(options.PhotoApiBaseUrl))
                throw new AccessException(CommercialAccessBlockReason.ServiceUnavailable,
                    "Không thể kiểm tra gói sử dụng. Vui lòng kiểm tra kết nối và thử lại.", true);

            var protectedCredential = await identities.GetProtectedCredentialAsync(cancellationToken).ConfigureAwait(false);
            if (protectedCredential == null || protectedCredential.Length == 0)
                throw new AccessException(CommercialAccessBlockReason.DeviceNotEnrolled,
                    "Thông tin kết nối của thiết bị không còn hợp lệ. Vui lòng thiết lập lại thiết bị.");

            string credential;
            try { credential = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedCredential, null, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException exception)
            {
                throw new AccessException(CommercialAccessBlockReason.DeviceNotEnrolled,
                    "Thông tin kết nối của thiết bị không còn đọc được. Vui lòng thiết lập lại thiết bị.", false, exception);
            }

            var endpoint = new Uri(new Uri(options.PhotoApiBaseUrl.TrimEnd('/') + "/"), "api/v3/device/entitlement");
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                request.Headers.TryAddWithoutValidation("X-Request-Id", "pc-entitlement-" + Guid.NewGuid().ToString("N"));
                await DeviceRequestProof.SignAsync(request, identities, credential, null, cancellationToken).ConfigureAwait(false);
                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode) throw ErrorFor(response.StatusCode, body);
                        EntitlementResponse wire;
                        try { wire = Read<EntitlementResponse>(body); }
                        catch (SerializationException exception)
                        {
                            throw new AccessException(CommercialAccessBlockReason.InvalidResponse,
                                "Dịch vụ trả về trạng thái gói chưa hợp lệ. Vui lòng thử lại.", true, exception);
                        }
                        if (wire?.Entitlement != null)
                        {
                            wire.Entitlement.CurrentPeriod = wire.Entitlement.CurrentPeriod ?? wire.CurrentPeriod;
                            wire.Entitlement.NextPeriod = wire.Entitlement.NextPeriod ?? wire.NextPeriod;
                            wire.Entitlement.Lease = wire.Entitlement.Lease ?? wire.Lease;
                            wire.Entitlement.DeviceAccess = wire.Entitlement.DeviceAccess ?? wire.DeviceAccess;
                            wire = wire.Entitlement;
                        }
                        if (wire == null || string.IsNullOrWhiteSpace(wire.AccountState) || string.IsNullOrWhiteSpace(wire.CommercialState))
                            throw new AccessException(CommercialAccessBlockReason.InvalidResponse,
                                "Dịch vụ trả về trạng thái gói chưa đầy đủ. Vui lòng thử lại.", true);
                        var entitlement = Map(wire);
                        var verifiedAt = utcNow();
                        var previousIssuedAt = LastVerified?.Lease?.IssuedAtUnix ?? 0;
                        if (entitlement.Lease != null && !OfflineEntitlementLeaseVerifier.TryVerify(entitlement.Lease, identity, verifiedAt, previousIssuedAt))
                            throw new AccessException(CommercialAccessBlockReason.InvalidResponse,
                                "Không thể xác minh quyền sử dụng ngoại tuyến do dịch vụ cấp. Vui lòng thử lại.", true);
                        LastVerified = entitlement;
                        LastVerifiedAtUtc = verifiedAt;
                        cachedDeviceId = identity.DeviceId;
                        cachedOrganizationId = identity.OrganizationId;
                        TrySaveCache(entitlement, verifiedAt, identity);
                        return entitlement;
                    }
                }
                catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AccessException(CommercialAccessBlockReason.ServiceUnavailable,
                        "Dịch vụ kiểm tra gói phản hồi quá chậm. Vui lòng thử lại.", true, exception);
                }
                catch (HttpRequestException exception)
                {
                    throw new AccessException(CommercialAccessBlockReason.ServiceUnavailable,
                        "Không thể kiểm tra gói sử dụng. Vui lòng kiểm tra kết nối mạng và thử lại.", true, exception);
                }
            }
        }

        public async Task<CommercialAccessDecision> CheckAppAccessAsync(CancellationToken cancellationToken)
        {
            try
            {
                var entitlement = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                return entitlement.EvaluateAppAccess(utcNow());
            }
            catch (AccessException exception)
            {
                if (exception.Reason != CommercialAccessBlockReason.ServiceUnavailable) InvalidateCache();
                return CommercialAccessDecision.Blocked(null, exception.Reason, exception.Message, exception.Retryable);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return CommercialAccessDecision.Blocked(null, CommercialAccessBlockReason.ServiceUnavailable,
                    "Không thể kiểm tra gói sử dụng. Vui lòng thử lại.", true);
            }
        }

        public async Task<CommercialAccessDecision> CheckAppAccessForNewSessionAsync(CancellationToken cancellationToken)
        {
            var now = utcNow();
            var cached = LastVerified;
            var verifiedAt = LastVerifiedAtUtc;
            if (cached == null || !verifiedAt.HasValue)
                return await CheckAppAccessAsync(cancellationToken).ConfigureAwait(false);

            if (!await CacheMatchesCurrentIdentityAsync(cancellationToken).ConfigureAwait(false))
            {
                ClearLoadedCache();
                return await CheckAppAccessAsync(cancellationToken).ConfigureAwait(false);
            }

            var cachedDecision = cached.EvaluateAppAccess(now);
            var age = now - verifiedAt.Value;
            var clockIsReliable = age >= TimeSpan.Zero;
            if (clockIsReliable && age <= revalidationInterval) return cachedDecision;

            var refreshed = await CheckAppAccessAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.IsAllowed || refreshed.BlockReason != CommercialAccessBlockReason.ServiceUnavailable)
                return refreshed;
            if (cachedDecision.IsAllowed && clockIsReliable && age <= maximumOfflineWindow)
                return cached.EvaluateAppAccess(now);
            if (!cachedDecision.IsAllowed) return cachedDecision;
            return CommercialAccessDecision.Blocked(cached, CommercialAccessBlockReason.ServiceUnavailable,
                "Không thể xác minh lại gói sử dụng sau thời gian ngoại tuyến cho phép. Vui lòng kết nối mạng và thử lại.", true);
        }

        async Task<bool> CacheMatchesCurrentIdentityAsync(CancellationToken cancellationToken)
        {
            try
            {
                var identity = await identities.GetAsync(cancellationToken).ConfigureAwait(false);
                return identity?.EnrollmentState == DeviceEnrollmentState.Enrolled
                    && string.Equals(identity.DeviceId, cachedDeviceId, StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(cachedOrganizationId)
                        || string.Equals(identity.OrganizationId, cachedOrganizationId, StringComparison.Ordinal));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return false; }
        }

        internal static string ResolveCachePath(string dataDirectory)
        {
            return string.IsNullOrWhiteSpace(dataDirectory)
                ? null
                : Path.Combine(Path.GetFullPath(dataDirectory), "Entitlement", "commercial-entitlement.cache");
        }

        void TryLoadCache()
        {
            if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath)) return;
            try
            {
                var file = new FileInfo(cachePath);
                if (file.Length <= 0 || file.Length > MaximumCacheBytes) return;
                var protectedPayload = File.ReadAllBytes(cachePath);
                var payload = ProtectedData.Unprotect(protectedPayload, CacheEntropy, DataProtectionScope.CurrentUser);
                var record = Read<PersistedEntitlementCache>(Encoding.UTF8.GetString(payload));
                if (record?.Version != CacheFormatVersion || record.Entitlement == null
                    || record.VerifiedAt <= 0 || string.IsNullOrWhiteSpace(record.DeviceId)) return;
                var verifiedAt = DateTimeOffset.FromUnixTimeSeconds(record.VerifiedAt).UtcDateTime;
                var entitlement = Map(record.Entitlement);
                if (string.IsNullOrWhiteSpace(entitlement.AccountState) || string.IsNullOrWhiteSpace(entitlement.CommercialState)) return;
                if (entitlement.Lease != null && !OfflineEntitlementLeaseVerifier.TryVerify(entitlement.Lease,
                    new DeviceIdentity { DeviceId = record.DeviceId, OrganizationId = record.OrganizationId }, utcNow())) return;
                LastVerified = entitlement;
                LastVerifiedAtUtc = verifiedAt;
                cachedDeviceId = record.DeviceId;
                cachedOrganizationId = record.OrganizationId;
            }
            catch
            {
                // A missing, corrupt, copied or tampered DPAPI payload is never trusted.
                ClearLoadedCache();
            }
        }

        void TrySaveCache(CommercialEntitlement entitlement, DateTime verifiedAt, DeviceIdentity identity)
        {
            if (string.IsNullOrWhiteSpace(cachePath) || entitlement == null || identity == null) return;
            string temporaryPath = null;
            try
            {
                var record = new PersistedEntitlementCache
                {
                    Version = CacheFormatVersion,
                    DeviceId = identity.DeviceId,
                    OrganizationId = identity.OrganizationId,
                    VerifiedAt = new DateTimeOffset(verifiedAt.ToUniversalTime()).ToUnixTimeSeconds(),
                    Entitlement = ToResponse(entitlement)
                };
                var protectedPayload = ProtectedData.Protect(Write(record), CacheEntropy, DataProtectionScope.CurrentUser);
                var directory = Path.GetDirectoryName(cachePath);
                Directory.CreateDirectory(directory);
                temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporaryPath, protectedPayload);
                if (File.Exists(cachePath)) File.Replace(temporaryPath, cachePath, null);
                else File.Move(temporaryPath, cachePath);
                temporaryPath = null;
            }
            catch
            {
                // Online authorization remains authoritative if the local cache cannot be written.
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath))
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        void InvalidateCache()
        {
            ClearLoadedCache();
            if (string.IsNullOrWhiteSpace(cachePath)) return;
            try { if (File.Exists(cachePath)) File.Delete(cachePath); }
            catch { }
        }

        void ClearLoadedCache()
        {
            LastVerified = null;
            LastVerifiedAtUtc = null;
            cachedDeviceId = null;
            cachedOrganizationId = null;
        }

        static EntitlementResponse ToResponse(CommercialEntitlement value)
        {
            return new EntitlementResponse
            {
                AccountState = value.AccountState,
                CommercialState = value.CommercialState,
                EffectivePlans = value.EffectivePlans == null ? new string[0] : new System.Collections.Generic.List<string>(value.EffectivePlans).ToArray(),
                AppAccess = value.AppAccess,
                WebAccess = value.WebAccess,
                UploadAccess = value.UploadAccess,
                DeviceLimit = value.DeviceLimit,
                DeviceAccess = value.DeviceAccess == null ? null : new DeviceAccessResponse
                {
                    Allowed = value.DeviceAccess.Allowed,
                    Rank = value.DeviceAccess.Rank,
                    Limit = value.DeviceAccess.Limit,
                    DenialCode = value.DeviceAccess.DenialCode
                },
                LifetimeAccess = value.LifetimeAccess,
                PlatformStorage = value.PlatformStorage == null ? null : new StorageResponse
                {
                    LimitBytes = value.PlatformStorage.LimitBytes,
                    UsedBytes = value.PlatformStorage.UsedBytes,
                    ReservedBytes = value.PlatformStorage.ReservedBytes,
                    HoldUntil = UnixSeconds(value.PlatformStorage.HoldUntilUtc),
                    PurgeAt = UnixSeconds(value.PlatformStorage.PurgeAtUtc)
                },
                ExternalStorage = value.ExternalStorage == null ? null : new ExternalStorageResponse
                {
                    Allowed = value.ExternalStorage.Allowed,
                    Required = value.ExternalStorage.Required
                },
                EventRetention = value.EventRetention == null ? null : new RetentionResponse
                {
                    MinimumDays = value.EventRetention.MinimumDays,
                    MaximumDays = value.EventRetention.MaximumDays
                },
                CurrentPeriod = ToResponse(value.CurrentPeriod),
                NextPeriod = ToResponse(value.NextPeriod),
                Lease = value.Lease == null ? null : new LeaseResponse
                {
                    Format = value.Lease.Format,
                    Algorithm = value.Lease.Algorithm,
                    KeyId = value.Lease.KeyId,
                    KeyVersion = value.Lease.SigningKeyVersion,
                    Payload = value.Lease.Payload,
                    Signature = value.Lease.Signature,
                    Revision = value.Lease.Revision,
                    IssuedAt = value.Lease.IssuedAtUnix,
                    NotBefore = UnixSeconds(value.Lease.NotBeforeUtc),
                    ExpiresAt = UnixSeconds(value.Lease.ExpiresAtUtc),
                    Audience = value.Lease.Audience,
                    DeviceId = value.Lease.DeviceId
                }
            };
        }

        static PeriodResponse ToResponse(CommercialPeriod value) => value == null ? null : new PeriodResponse
        {
            PlanCode = value.PlanCode,
            StartsAt = UnixSeconds(value.StartsAtUtc),
            EndsAt = UnixSeconds(value.EndsAtUtc)
        };

        static long? UnixSeconds(DateTime? value) => value.HasValue
            ? (long?)new DateTimeOffset(value.Value.ToUniversalTime()).ToUnixTimeSeconds()
            : null;

        static CommercialEntitlement Map(EntitlementResponse value)
        {
            var planCode = value.PlanCode ?? value.CurrentPeriod?.PlanCode;
            var plans = value.EffectivePlans != null && value.EffectivePlans.Length > 0
                ? value.EffectivePlans
                : string.IsNullOrWhiteSpace(planCode) ? new string[0] : new[] { planCode };
            var platformStorage = value.PlatformStorage == null
                ? new CommercialStorageAllowance
                {
                    LimitBytes = value.MediaLimitBytes ?? value.StorageLimitBytes ?? 0,
                    UsedBytes = value.MediaCommittedBytes ?? 0,
                    ReservedBytes = value.MediaReservedBytes ?? 0,
                    PurgeAtUtc = Unix(value.DataDeleteAt)
                }
                : new CommercialStorageAllowance
                {
                    LimitBytes = value.PlatformStorage.LimitBytes,
                    UsedBytes = value.PlatformStorage.UsedBytes,
                    ReservedBytes = value.PlatformStorage.ReservedBytes,
                    HoldUntilUtc = Unix(value.PlatformStorage.HoldUntil),
                    PurgeAtUtc = Unix(value.PlatformStorage.PurgeAt)
                };
            return new CommercialEntitlement
            {
                AccountState = value.AccountState,
                CommercialState = value.CommercialState ?? value.PlanKind,
                EffectivePlans = plans,
                AppAccess = value.AppAccess,
                WebAccess = value.WebAccess ?? value.WebMode,
                UploadAccess = value.UploadAccess,
                DeviceLimit = value.DeviceLimit,
                DeviceAccess = value.DeviceAccess == null ? null : new CommercialDeviceAccess
                {
                    Allowed = value.DeviceAccess.Allowed,
                    Rank = value.DeviceAccess.Rank,
                    Limit = value.DeviceAccess.Limit,
                    DenialCode = value.DeviceAccess.DenialCode
                },
                LifetimeAccess = value.LifetimeAccess,
                PlatformStorage = platformStorage,
                ExternalStorage = value.ExternalStorage == null ? new CommercialExternalStorageAllowance
                {
                    Allowed = string.Equals(value.StorageMode, "customer_drive", StringComparison.OrdinalIgnoreCase),
                    Required = string.Equals(value.StorageMode, "customer_drive", StringComparison.OrdinalIgnoreCase)
                } : new CommercialExternalStorageAllowance
                {
                    Allowed = value.ExternalStorage.Allowed,
                    Required = value.ExternalStorage.Required
                },
                EventRetention = value.EventRetention == null ? new CommercialEventRetention
                {
                    MinimumDays = value.EventRetentionMinimumDays ?? 0,
                    MaximumDays = value.EventRetentionMaximumDays ?? 0
                } : new CommercialEventRetention
                {
                    MinimumDays = value.EventRetention.MinimumDays,
                    MaximumDays = value.EventRetention.MaximumDays
                },
                CurrentPeriod = Map(value.CurrentPeriod) ?? (value.StartsAt.HasValue || value.EndsAt.HasValue ? new CommercialPeriod
                {
                    PlanCode = planCode,
                    StartsAtUtc = Unix(value.StartsAt),
                    EndsAtUtc = Unix(value.EndsAt)
                } : null),
                NextPeriod = Map(value.NextPeriod),
                Lease = value.Lease == null ? null : new SignedCommercialEntitlementLease
                {
                    Format = value.Lease.Format,
                    Algorithm = value.Lease.Algorithm,
                    KeyId = value.Lease.KeyId,
                    SigningKeyVersion = value.Lease.KeyVersion,
                    Payload = value.Lease.Payload,
                    Signature = value.Lease.Signature,
                    Revision = value.Lease.Revision,
                    IssuedAtUnix = value.Lease.IssuedAt,
                    NotBeforeUtc = Unix(value.Lease.NotBefore),
                    ExpiresAtUtc = Unix(value.Lease.ExpiresAt),
                    Audience = value.Lease.Audience,
                    DeviceId = value.Lease.DeviceId
                }
            };
        }

        static CommercialPeriod Map(PeriodResponse value) => value == null ? null : new CommercialPeriod
        {
            PlanCode = value.PlanCode,
            StartsAtUtc = Unix(value.StartsAt),
            EndsAtUtc = Unix(value.EndsAt)
        };

        static DateTime? Unix(long? value) => value.HasValue && value.Value > 0
            ? (DateTime?)DateTimeOffset.FromUnixTimeSeconds(value.Value).UtcDateTime
            : null;

        static Exception ErrorFor(HttpStatusCode status, string body)
        {
            var code = ErrorCode(body);
            switch (code)
            {
                case "account_suspended":
                case "account_locked":
                    return new AccessException(CommercialAccessBlockReason.AccountSuspended,
                        "Tài khoản đang bị tạm ngưng. Vui lòng liên hệ nhà cung cấp để được hỗ trợ.");
                case "account_closed":
                    return new AccessException(CommercialAccessBlockReason.AccountClosed,
                        "Tài khoản không còn hoạt động. Vui lòng liên hệ nhà cung cấp để được hỗ trợ.");
                case "subscription_required":
                case "subscription_app_access_expired":
                    return new AccessException(CommercialAccessBlockReason.SubscriptionRequired,
                        "Gói sử dụng chưa được kích hoạt hoặc đã hết hạn. Vui lòng liên hệ chủ tài khoản hoặc nhà cung cấp để gia hạn.");
                case "account_verification_required":
                    return new AccessException(CommercialAccessBlockReason.AccountVerificationRequired,
                        "Email chủ tài khoản chưa được xác minh. Vui lòng xác minh email trong Web Customer.");
                case "device_limit_reached":
                case "device_limit_exceeded":
                    return new AccessException(CommercialAccessBlockReason.DeviceLimitReached,
                        "Tài khoản đã dùng hết số lượng thiết bị được phép trong gói hiện tại.");
                case "device_revoked":
                case "device_suspended":
                case "device_not_found":
                    return new AccessException(CommercialAccessBlockReason.DeviceNotEnrolled,
                        "Thiết bị không còn được phép sử dụng. Vui lòng kiểm tra trong trang quản lý.");
                case "device_locked":
                    return new AccessException(CommercialAccessBlockReason.DeviceLocked,
                        "Thiết bị đang bị khóa tạm thời. Chủ tài khoản cần xác minh lại trên đúng thiết bị để mở khóa.");
                case "device_proof_required":
                case "device_proof_invalid":
                case "device_proof_replayed":
                    return new AccessException(CommercialAccessBlockReason.DeviceNotEnrolled,
                        "Không thể xác minh khóa bảo mật của thiết bị. Hãy kết nối lại thiết bị.");
                default:
                    var temporarilyUnavailable = (int)status >= 500
                        || status == HttpStatusCode.RequestTimeout
                        || (int)status == 429;
                    return new AccessException(
                        temporarilyUnavailable ? CommercialAccessBlockReason.ServiceUnavailable : CommercialAccessBlockReason.InvalidResponse,
                        temporarilyUnavailable ? "Dịch vụ kiểm tra gói đang bận. Vui lòng thử lại." : "Không thể xác nhận gói sử dụng. Vui lòng thử lại.",
                        temporarilyUnavailable);
            }
        }

        static string ErrorCode(string body)
        {
            try { return Read<ErrorEnvelope>(body)?.Error?.Code; }
            catch { return null; }
        }

        static T Read<T>(string body)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        static byte[] Write<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        sealed class AccessException : InvalidOperationException
        {
            public AccessException(CommercialAccessBlockReason reason, string message, bool retryable = false, Exception inner = null)
                : base(message, inner) { Reason = reason; Retryable = retryable; }
            public CommercialAccessBlockReason Reason { get; }
            public bool Retryable { get; }
        }

        [DataContract]
        sealed class EntitlementResponse
        {
            [DataMember(Name = "entitlement", EmitDefaultValue = false)] public EntitlementResponse Entitlement { get; set; }
            [DataMember(Name = "accountState")] public string AccountState { get; set; }
            [DataMember(Name = "commercialState")] public string CommercialState { get; set; }
            [DataMember(Name = "planKind", EmitDefaultValue = false)] public string PlanKind { get; set; }
            [DataMember(Name = "planCode", EmitDefaultValue = false)] public string PlanCode { get; set; }
            [DataMember(Name = "effectivePlans")] public string[] EffectivePlans { get; set; }
            [DataMember(Name = "appAccess")] public bool AppAccess { get; set; }
            [DataMember(Name = "webAccess")] public string WebAccess { get; set; }
            [DataMember(Name = "webMode", EmitDefaultValue = false)] public string WebMode { get; set; }
            [DataMember(Name = "uploadAccess")] public bool UploadAccess { get; set; }
            [DataMember(Name = "deviceLimit")] public int DeviceLimit { get; set; }
            [DataMember(Name = "deviceAccess", EmitDefaultValue = false)] public DeviceAccessResponse DeviceAccess { get; set; }
            [DataMember(Name = "lifetimeAccess", EmitDefaultValue = false)] public bool LifetimeAccess { get; set; }
            [DataMember(Name = "platformStorage")] public StorageResponse PlatformStorage { get; set; }
            [DataMember(Name = "externalStorage")] public ExternalStorageResponse ExternalStorage { get; set; }
            [DataMember(Name = "eventRetention")] public RetentionResponse EventRetention { get; set; }
            [DataMember(Name = "currentPeriod")] public PeriodResponse CurrentPeriod { get; set; }
            [DataMember(Name = "nextPeriod")] public PeriodResponse NextPeriod { get; set; }
            [DataMember(Name = "lease", EmitDefaultValue = false)] public LeaseResponse Lease { get; set; }
            [DataMember(Name = "startsAt", EmitDefaultValue = false)] public long? StartsAt { get; set; }
            [DataMember(Name = "endsAt", EmitDefaultValue = false)] public long? EndsAt { get; set; }
            [DataMember(Name = "storageMode", EmitDefaultValue = false)] public string StorageMode { get; set; }
            [DataMember(Name = "storageLimitBytes", EmitDefaultValue = false)] public long? StorageLimitBytes { get; set; }
            [DataMember(Name = "mediaLimitBytes", EmitDefaultValue = false)] public long? MediaLimitBytes { get; set; }
            [DataMember(Name = "mediaReservedBytes", EmitDefaultValue = false)] public long? MediaReservedBytes { get; set; }
            [DataMember(Name = "mediaCommittedBytes", EmitDefaultValue = false)] public long? MediaCommittedBytes { get; set; }
            [DataMember(Name = "eventRetentionMinDays", EmitDefaultValue = false)] public int? EventRetentionMinimumDays { get; set; }
            [DataMember(Name = "eventRetentionMaxDays", EmitDefaultValue = false)] public int? EventRetentionMaximumDays { get; set; }
            [DataMember(Name = "dataDeleteAt", EmitDefaultValue = false)] public long? DataDeleteAt { get; set; }
        }

        [DataContract] sealed class StorageResponse
        {
            [DataMember(Name = "limitBytes")] public long LimitBytes { get; set; }
            [DataMember(Name = "usedBytes")] public long UsedBytes { get; set; }
            [DataMember(Name = "reservedBytes")] public long ReservedBytes { get; set; }
            [DataMember(Name = "holdUntil")] public long? HoldUntil { get; set; }
            [DataMember(Name = "purgeAt")] public long? PurgeAt { get; set; }
        }

        [DataContract] sealed class DeviceAccessResponse
        {
            [DataMember(Name = "allowed")] public bool Allowed { get; set; }
            [DataMember(Name = "rank", EmitDefaultValue = false)] public int? Rank { get; set; }
            [DataMember(Name = "limit")] public int Limit { get; set; }
            [DataMember(Name = "denialCode", EmitDefaultValue = false)] public string DenialCode { get; set; }
        }

        [DataContract] sealed class ExternalStorageResponse
        {
            [DataMember(Name = "allowed")] public bool Allowed { get; set; }
            [DataMember(Name = "required")] public bool Required { get; set; }
        }

        [DataContract] sealed class RetentionResponse
        {
            [DataMember(Name = "minDays")] public int MinimumDays { get; set; }
            [DataMember(Name = "maxDays")] public int MaximumDays { get; set; }
        }

        [DataContract] sealed class PeriodResponse
        {
            [DataMember(Name = "planCode")] public string PlanCode { get; set; }
            [DataMember(Name = "startsAt")] public long? StartsAt { get; set; }
            [DataMember(Name = "endsAt")] public long? EndsAt { get; set; }
        }

        [DataContract] sealed class LeaseResponse
        {
            [DataMember(Name = "format")] public string Format { get; set; }
            [DataMember(Name = "algorithm")] public string Algorithm { get; set; }
            [DataMember(Name = "keyId")] public string KeyId { get; set; }
            [DataMember(Name = "keyVersion")] public int KeyVersion { get; set; }
            [DataMember(Name = "payload")] public string Payload { get; set; }
            [DataMember(Name = "signature")] public string Signature { get; set; }
            [DataMember(Name = "revision")] public long Revision { get; set; }
            [DataMember(Name = "issuedAt")] public long IssuedAt { get; set; }
            [DataMember(Name = "notBefore")] public long? NotBefore { get; set; }
            [DataMember(Name = "expiresAt")] public long? ExpiresAt { get; set; }
            [DataMember(Name = "audience")] public string Audience { get; set; }
            [DataMember(Name = "deviceId")] public string DeviceId { get; set; }
        }

        [DataContract] sealed class ErrorEnvelope { [DataMember(Name = "error")] public ErrorBody Error { get; set; } }
        [DataContract] sealed class ErrorBody { [DataMember(Name = "code")] public string Code { get; set; } }

        [DataContract]
        sealed class PersistedEntitlementCache
        {
            [DataMember(Name = "version")] public int Version { get; set; }
            [DataMember(Name = "deviceId")] public string DeviceId { get; set; }
            [DataMember(Name = "organizationId", EmitDefaultValue = false)] public string OrganizationId { get; set; }
            [DataMember(Name = "verifiedAt")] public long VerifiedAt { get; set; }
            [DataMember(Name = "entitlement")] public EntitlementResponse Entitlement { get; set; }
        }
    }
}
