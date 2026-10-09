using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CommercialEntitlementTests
    {
        [Theory]
        [InlineData("trialing", "TRIAL_7D")]
        [InlineData("active_paid", "LITE_MONTHLY")]
        [InlineData("active_paid", "BASIC_MONTHLY")]
        [InlineData("perpetual_only", "PERPETUAL")]
        public void Active_launch_plans_allow_the_pc_app(string commercialState, string plan)
        {
            var entitlement = Active(commercialState, plan);

            var result = entitlement.EvaluateAppAccess(DateTime.UtcNow);

            Assert.True(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.None, result.BlockReason);
        }

        [Fact]
        public void Suspended_account_is_distinct_from_missing_subscription()
        {
            var entitlement = Active("active_paid", "BASIC_MONTHLY");
            entitlement.AccountState = "suspended";
            entitlement.AppAccess = false;

            var result = entitlement.EvaluateAppAccess(DateTime.UtcNow);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.AccountSuspended, result.BlockReason);
            Assert.Contains("tạm ngưng", result.UserMessage);
        }

        [Fact]
        public void Expired_paid_period_is_rejected_even_if_stale_flag_says_allowed()
        {
            var entitlement = Active("active_paid", "LITE_MONTHLY");
            entitlement.CurrentPeriod.EndsAtUtc = DateTime.UtcNow.AddSeconds(-1);

            var result = entitlement.EvaluateAppAccess(DateTime.UtcNow);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.EntitlementExpired, result.BlockReason);
        }

        [Fact]
        public void Prepaid_next_period_keeps_offline_access_across_the_renewal_boundary()
        {
            var boundary = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var entitlement = Active("subscription", "LITE", "BASIC");
            entitlement.CurrentPeriod = new CommercialPeriod
            {
                PlanCode = "LITE",
                StartsAtUtc = boundary.AddMonths(-1),
                EndsAtUtc = boundary
            };
            entitlement.NextPeriod = new CommercialPeriod
            {
                PlanCode = "BASIC",
                StartsAtUtc = boundary,
                EndsAtUtc = boundary.AddMonths(1)
            };

            var result = entitlement.EvaluateAppAccess(boundary.AddMinutes(1));

            Assert.True(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.None, result.BlockReason);
        }

        [Fact]
        public void Future_next_period_does_not_bridge_a_gap_after_expiry()
        {
            var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var entitlement = Active("subscription", "LITE", "BASIC");
            entitlement.CurrentPeriod.EndsAtUtc = now.AddMinutes(-1);
            entitlement.NextPeriod = new CommercialPeriod
            {
                PlanCode = "BASIC",
                StartsAtUtc = now.AddHours(1),
                EndsAtUtc = now.AddMonths(1)
            };

            var result = entitlement.EvaluateAppAccess(now);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.EntitlementExpired, result.BlockReason);
        }

        [Fact]
        public void Perpetual_app_access_does_not_expire_with_a_cloud_period()
        {
            var entitlement = Active("perpetual_plus_cloud", "PERPETUAL", "LITE_MONTHLY");
            entitlement.CurrentPeriod.EndsAtUtc = DateTime.UtcNow.AddDays(-1);

            Assert.True(entitlement.EvaluateAppAccess(DateTime.UtcNow).IsAllowed);
        }

        [Fact]
        public void Lifetime_flag_keeps_app_access_when_cloud_subscription_period_ends()
        {
            var entitlement = Active("subscription", "LITE");
            entitlement.LifetimeAccess = true;
            entitlement.CurrentPeriod.EndsAtUtc = DateTime.UtcNow.AddDays(-1);

            Assert.True(entitlement.EvaluateAppAccess(DateTime.UtcNow).IsAllowed);
        }

        [Fact]
        public void Unverified_future_lease_never_grants_offline_access()
        {
            var entitlement = Active("free", "FREE");
            entitlement.AppAccess = false;
            entitlement.Lease = new SignedCommercialEntitlementLease
            {
                Payload = "untrusted",
                Signature = "not-verified",
                ExpiresAtUtc = DateTime.UtcNow.AddYears(1)
            };

            var result = entitlement.EvaluateAppAccess(DateTime.UtcNow);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.SubscriptionRequired, result.BlockReason);
        }

        [Fact]
        public async Task Device_contract_is_deserialized_and_authenticated_with_device_credential()
        {
            var handler = new ResponseHandler(HttpStatusCode.OK,
                "{\"entitlement\":{\"organizationId\":\"org_1\",\"accountState\":\"active\",\"commercialState\":\"trial\",\"planKind\":\"trial\",\"planCode\":\"TRIAL_7D\",\"appAccess\":true,\"uploadAccess\":true,\"webMode\":\"full\",\"deviceLimit\":1,\"storageMode\":\"platform\",\"mediaLimitBytes\":100000000000,\"mediaCommittedBytes\":12,\"mediaReservedBytes\":3,\"eventRetentionMinDays\":7,\"eventRetentionMaxDays\":30,\"startsAt\":1780000000,\"endsAt\":1890000000},\"currentPeriod\":{\"planCode\":\"TRIAL_7D\",\"startsAt\":1780000000,\"endsAt\":1890000000},\"nextPeriod\":{\"planCode\":\"LITE\",\"startsAt\":1890000000,\"endsAt\":1892678400},\"lease\":null}");
            var repository = new StubIdentityRepository("dv3.dcr_test.secret");
            var service = new CommercialEntitlementService(repository,
                new ApplicationOptions { PhotoApiBaseUrl = "https://example.test/" },
                new HttpClient(handler));

            var value = await service.GetCurrentAsync(CancellationToken.None);

            Assert.Equal("trial", value.CommercialState);
            Assert.Equal(100000000000L, value.PlatformStorage.LimitBytes);
            Assert.Equal(30, value.EventRetention.MaximumDays);
            Assert.Equal("TRIAL_7D", value.CurrentPeriod.PlanCode);
            Assert.Equal("LITE", value.NextPeriod.PlanCode);
            Assert.Equal("Bearer", handler.AuthorizationScheme);
            Assert.Equal("dv3.dcr_test.secret", handler.AuthorizationParameter);
            Assert.Equal("https://example.test/api/v3/device/entitlement", handler.RequestUri.AbsoluteUri);
        }

        [Fact]
        public async Task Canonical_subscription_required_error_is_reported_as_a_commercial_denial()
        {
            var handler = new ResponseHandler(HttpStatusCode.Forbidden,
                "{\"error\":{\"code\":\"subscription_required\"}}");
            var service = new CommercialEntitlementService(new StubIdentityRepository("dv3.dcr_test.secret"),
                new ApplicationOptions { PhotoApiBaseUrl = "https://example.test/" },
                new HttpClient(handler));

            var result = await service.CheckAppAccessAsync(CancellationToken.None);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.SubscriptionRequired, result.BlockReason);
            Assert.Contains("gia hạn", result.UserMessage);
        }

        [Fact]
        public async Task Temporary_device_lock_is_distinct_and_requires_owner_unlock()
        {
            var handler = new ResponseHandler((HttpStatusCode)423,
                "{\"error\":{\"code\":\"device_locked\"}}");
            var service = new CommercialEntitlementService(new StubIdentityRepository("dv3.dcr_test.secret"),
                new ApplicationOptions { PhotoApiBaseUrl = "https://example.test/" },
                new HttpClient(handler));

            var result = await service.CheckAppAccessAsync(CancellationToken.None);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.DeviceLocked, result.BlockReason);
            Assert.Contains("đúng thiết bị", result.UserMessage);
        }

        [Fact]
        public async Task Flat_worker_contract_keeps_period_and_storage_compatible()
        {
            var handler = new ResponseHandler(HttpStatusCode.OK,
                "{\"accountState\":\"active\",\"commercialState\":\"subscription\",\"planCode\":\"LITE_MONTHLY\",\"appAccess\":true,\"uploadAccess\":true,\"webMode\":\"full\",\"deviceLimit\":3,\"startsAt\":1780000000,\"endsAt\":1890000000,\"storageMode\":\"platform\",\"mediaLimitBytes\":100000000000,\"mediaReservedBytes\":7,\"mediaCommittedBytes\":11,\"eventRetentionMinDays\":7,\"eventRetentionMaxDays\":90,\"dataDeleteAt\":null}");
            var service = new CommercialEntitlementService(new StubIdentityRepository("dv3.dcr_test.secret"),
                new ApplicationOptions { PhotoApiBaseUrl = "https://example.test/" },
                new HttpClient(handler));

            var value = await service.GetCurrentAsync(CancellationToken.None);

            Assert.Equal("LITE_MONTHLY", Assert.Single(value.EffectivePlans));
            Assert.Equal("full", value.WebAccess);
            Assert.Equal(100000000000L, value.PlatformStorage.LimitBytes);
            Assert.Equal(11L, value.PlatformStorage.UsedBytes);
            Assert.Equal(90, value.EventRetention.MaximumDays);
            Assert.NotNull(value.CurrentPeriod.EndsAtUtc);
        }

        [Fact]
        public async Task Device_entitlement_rejects_a_device_outside_the_deterministic_seat_limit()
        {
            var handler = new ResponseHandler(HttpStatusCode.OK,
                "{\"entitlement\":{\"accountState\":\"active\",\"commercialState\":\"trial\",\"effectivePlans\":[\"TRIAL_7D\"],\"appAccess\":true,\"uploadAccess\":true,\"webAccess\":\"full\",\"deviceLimit\":1},\"deviceAccess\":{\"allowed\":false,\"rank\":2,\"limit\":1,\"denialCode\":\"device_limit_exceeded\"}}");
            var service = new CommercialEntitlementService(new StubIdentityRepository("dv3.dcr_test.secret"),
                new ApplicationOptions { PhotoApiBaseUrl = "https://example.test/" },
                new HttpClient(handler));

            var result = await service.CheckAppAccessAsync(CancellationToken.None);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.DeviceLimitReached, result.BlockReason);
            Assert.NotNull(result.Entitlement.DeviceAccess);
            Assert.Equal(2, result.Entitlement.DeviceAccess.Rank);
            Assert.Equal(1, result.Entitlement.DeviceAccess.Limit);
        }

        [Fact]
        public async Task Canonical_device_limit_exceeded_error_is_reported_as_a_seat_denial()
        {
            var handler = new ResponseHandler(HttpStatusCode.Forbidden,
                "{\"error\":{\"code\":\"device_limit_exceeded\"}}");
            var service = new CommercialEntitlementService(new StubIdentityRepository("dv3.dcr_test.secret"),
                new ApplicationOptions { PhotoApiBaseUrl = "https://example.test/" },
                new HttpClient(handler));

            var result = await service.CheckAppAccessAsync(CancellationToken.None);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.DeviceLimitReached, result.BlockReason);
        }

        [Fact]
        public async Task Fresh_in_memory_verification_does_not_make_another_request()
        {
            var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
            var handler = new SequenceResponseHandler(Ok(ActiveEnvelope("TRIAL_7D", clock.UtcNow.AddDays(7))));
            var service = Service(handler, clock);
            Assert.True((await service.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);

            clock.UtcNow = clock.UtcNow.AddMinutes(10);
            var result = await service.CheckAppAccessForNewSessionAsync(CancellationToken.None);

            Assert.True(result.IsAllowed);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task Stale_verification_is_refreshed_before_a_new_session()
        {
            var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
            var handler = new SequenceResponseHandler(
                Ok(ActiveEnvelope("TRIAL_7D", clock.UtcNow.AddDays(7))),
                Ok(ActiveEnvelope("BASIC", clock.UtcNow.AddMonths(1))));
            var service = Service(handler, clock);
            Assert.True((await service.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);

            clock.UtcNow = clock.UtcNow.AddMinutes(16);
            var result = await service.CheckAppAccessForNewSessionAsync(CancellationToken.None);

            Assert.True(result.IsAllowed);
            Assert.Equal("BASIC", Assert.Single(result.Entitlement.EffectivePlans));
            Assert.Equal(2, handler.RequestCount);
        }

        [Fact]
        public async Task Temporary_network_outage_uses_verified_state_within_offline_window()
        {
            var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
            var handler = new SequenceResponseHandler(
                Ok(ActiveEnvelope("LITE", clock.UtcNow.AddDays(7))),
                new HttpRequestException("offline"));
            var service = Service(handler, clock);
            Assert.True((await service.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);

            clock.UtcNow = clock.UtcNow.AddHours(1);
            var result = await service.CheckAppAccessForNewSessionAsync(CancellationToken.None);

            Assert.True(result.IsAllowed);
            Assert.Equal(2, handler.RequestCount);
        }

        [Theory]
        [InlineData(408)]
        [InlineData(429)]
        public async Task Retryable_http_status_uses_verified_state_within_offline_window(int statusCode)
        {
            var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
            var handler = new SequenceResponseHandler(
                Ok(ActiveEnvelope("LITE", clock.UtcNow.AddDays(7))),
                new HttpResponseMessage((HttpStatusCode)statusCode)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"temporarily_unavailable\"}}", Encoding.UTF8, "application/json")
                });
            var service = Service(handler, clock);
            Assert.True((await service.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);

            clock.UtcNow = clock.UtcNow.AddHours(1);
            var result = await service.CheckAppAccessForNewSessionAsync(CancellationToken.None);

            Assert.True(result.IsAllowed);
            Assert.Equal("LITE", Assert.Single(result.Entitlement.EffectivePlans));
            Assert.Equal(2, handler.RequestCount);
        }

        [Fact]
        public async Task Network_outage_beyond_offline_window_blocks_a_new_session()
        {
            var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
            var handler = new SequenceResponseHandler(
                Ok(ActiveEnvelope("PERPETUAL", null, true)),
                new HttpRequestException("offline"));
            var service = Service(handler, clock);
            Assert.True((await service.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);

            clock.UtcNow = clock.UtcNow.AddHours(25);
            var result = await service.CheckAppAccessForNewSessionAsync(CancellationToken.None);

            Assert.False(result.IsAllowed);
            Assert.Equal(CommercialAccessBlockReason.ServiceUnavailable, result.BlockReason);
            Assert.Contains("ngoại tuyến", result.UserMessage);
            Assert.Equal(2, handler.RequestCount);
        }

        [Fact]
        public async Task Protected_cache_survives_restart_and_allows_temporary_outage()
        {
            using (var directory = new TemporaryDirectory())
            {
                var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
                var writer = Service(new SequenceResponseHandler(
                    Ok(ActiveEnvelope("LITE", clock.UtcNow.AddDays(7)))), clock, directory.RootPath);
                Assert.True((await writer.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);
                Assert.True(File.Exists(CommercialEntitlementService.ResolveCachePath(directory.RootPath)));

                clock.UtcNow = clock.UtcNow.AddHours(1);
                var offline = new SequenceResponseHandler(new HttpRequestException("offline"));
                var restarted = Service(offline, clock, directory.RootPath);
                var result = await restarted.CheckAppAccessForNewSessionAsync(CancellationToken.None);

                Assert.True(result.IsAllowed);
                Assert.Equal("LITE", Assert.Single(result.Entitlement.EffectivePlans));
                Assert.Equal(1, offline.RequestCount);
            }
        }

        [Fact]
        public async Task Tampered_protected_cache_is_rejected_after_restart()
        {
            using (var directory = new TemporaryDirectory())
            {
                var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
                var writer = Service(new SequenceResponseHandler(
                    Ok(ActiveEnvelope("BASIC", clock.UtcNow.AddDays(30)))), clock, directory.RootPath);
                Assert.True((await writer.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);
                var cachePath = CommercialEntitlementService.ResolveCachePath(directory.RootPath);
                var bytes = File.ReadAllBytes(cachePath);
                bytes[bytes.Length / 2] ^= 0x5a;
                File.WriteAllBytes(cachePath, bytes);

                var offline = new SequenceResponseHandler(new HttpRequestException("offline"));
                var restarted = Service(offline, clock, directory.RootPath);
                var result = await restarted.CheckAppAccessForNewSessionAsync(CancellationToken.None);

                Assert.Null(restarted.LastVerified);
                Assert.False(result.IsAllowed);
                Assert.Equal(CommercialAccessBlockReason.ServiceUnavailable, result.BlockReason);
                Assert.Equal(1, offline.RequestCount);
            }
        }

        [Fact]
        public async Task Expired_persisted_entitlement_blocks_after_restart()
        {
            using (var directory = new TemporaryDirectory())
            {
                var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
                var writer = Service(new SequenceResponseHandler(
                    Ok(ActiveEnvelope("LITE", clock.UtcNow.AddMinutes(30)))), clock, directory.RootPath);
                Assert.True((await writer.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);

                clock.UtcNow = clock.UtcNow.AddHours(1);
                var offline = new SequenceResponseHandler(new HttpRequestException("offline"));
                var restarted = Service(offline, clock, directory.RootPath);
                var result = await restarted.CheckAppAccessForNewSessionAsync(CancellationToken.None);

                Assert.False(result.IsAllowed);
                Assert.Equal(CommercialAccessBlockReason.EntitlementExpired, result.BlockReason);
                Assert.Equal(1, offline.RequestCount);
            }
        }

        [Fact]
        public async Task Persisted_cache_older_than_maximum_offline_window_blocks_after_restart()
        {
            using (var directory = new TemporaryDirectory())
            {
                var clock = new MutableClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
                var writer = Service(new SequenceResponseHandler(
                    Ok(ActiveEnvelope("PERPETUAL", null, true))), clock, directory.RootPath);
                Assert.True((await writer.CheckAppAccessAsync(CancellationToken.None)).IsAllowed);

                clock.UtcNow = clock.UtcNow.AddHours(25);
                var offline = new SequenceResponseHandler(new HttpRequestException("offline"));
                var restarted = Service(offline, clock, directory.RootPath);
                var result = await restarted.CheckAppAccessForNewSessionAsync(CancellationToken.None);

                Assert.False(result.IsAllowed);
                Assert.Equal(CommercialAccessBlockReason.ServiceUnavailable, result.BlockReason);
                Assert.Contains("ngoại tuyến", result.UserMessage);
                Assert.Equal(1, offline.RequestCount);
            }
        }

        static CommercialEntitlementService Service(HttpMessageHandler handler, MutableClock clock, string dataDirectory = null) =>
            new CommercialEntitlementService(new StubIdentityRepository("dv3.dcr_test.secret"),
                new ApplicationOptions { PhotoApiBaseUrl = "https://example.test/", DataDirectory = dataDirectory },
                new HttpClient(handler), () => clock.UtcNow, TimeSpan.FromMinutes(15), TimeSpan.FromHours(24));

        static HttpResponseMessage Ok(string body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        static string ActiveEnvelope(string planCode, DateTime? endsAtUtc, bool lifetime = false)
        {
            var starts = new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var ends = endsAtUtc.HasValue ? new DateTimeOffset(endsAtUtc.Value).ToUnixTimeSeconds().ToString() : "null";
            var kind = lifetime ? "perpetual" : planCode == "TRIAL_7D" ? "trial" : "subscription";
            return "{\"entitlement\":{\"accountState\":\"active\",\"commercialState\":\"" + kind + "\",\"planKind\":\"" + kind + "\",\"planCode\":\"" + planCode + "\",\"appAccess\":true,\"uploadAccess\":true,\"webMode\":\"full\",\"deviceLimit\":3,\"startsAt\":" + starts + ",\"endsAt\":" + ends + ",\"storageMode\":\"platform\",\"mediaLimitBytes\":100000000000,\"eventRetentionMinDays\":7,\"eventRetentionMaxDays\":90,\"lifetimeAccess\":" + (lifetime ? "true" : "false") + "},\"currentPeriod\":{\"planCode\":\"" + planCode + "\",\"startsAt\":" + starts + ",\"endsAt\":" + ends + "},\"nextPeriod\":null,\"lease\":null}";
        }

        static CommercialEntitlement Active(string state, params string[] plans) => new CommercialEntitlement
        {
            AccountState = "active",
            CommercialState = state,
            EffectivePlans = plans,
            AppAccess = true,
            UploadAccess = true,
            DeviceLimit = state == "trialing" ? 1 : 3,
            CurrentPeriod = new CommercialPeriod { StartsAtUtc = DateTime.UtcNow.AddDays(-1), EndsAtUtc = DateTime.UtcNow.AddDays(1) }
        };

        sealed class ResponseHandler : HttpMessageHandler
        {
            readonly HttpStatusCode status;
            readonly string body;
            public ResponseHandler(HttpStatusCode statusCode, string responseBody) { status = statusCode; body = responseBody; }
            public Uri RequestUri { get; private set; }
            public string AuthorizationScheme { get; private set; }
            public string AuthorizationParameter { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                AuthorizationScheme = request.Headers.Authorization?.Scheme;
                AuthorizationParameter = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        sealed class SequenceResponseHandler : HttpMessageHandler
        {
            readonly Queue<object> responses;
            public SequenceResponseHandler(params object[] values) { responses = new Queue<object>(values); }
            public int RequestCount { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                if (responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request.");
                var value = responses.Dequeue();
                if (value is Exception exception) return Task.FromException<HttpResponseMessage>(exception);
                return Task.FromResult((HttpResponseMessage)value);
            }
        }

        sealed class MutableClock
        {
            public MutableClock(DateTime utcNow) { UtcNow = utcNow; }
            public DateTime UtcNow { get; set; }
        }

        sealed class TemporaryDirectory : IDisposable
        {
            public TemporaryDirectory()
            {
                RootPath = Path.Combine(Path.GetTempPath(), "photobooth-entitlement-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(RootPath);
            }

            public string RootPath { get; }

            public void Dispose()
            {
                var fullPath = Path.GetFullPath(RootPath);
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
                    Directory.Delete(fullPath, true);
            }
        }

        sealed class StubIdentityRepository : IDeviceIdentityRepository
        {
            readonly byte[] credential;
            public StubIdentityRepository(string value)
            {
                DeviceRequestProof.GetOrCreatePublicKey("installation-entitlement-tests");
                credential = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            }
            public Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new DeviceIdentity
            {
                ProfileId = "profile_1", InstallationId = "installation-entitlement-tests", DeviceId = "device_1", OrganizationId = "org_1", EnrollmentState = DeviceEnrollmentState.Enrolled
            });
            public Task<byte[]> GetProtectedCredentialAsync(CancellationToken cancellationToken) => Task.FromResult(credential);
            public Task SaveEnrolledAsync(DeviceIdentity identity, byte[] protectedCredential, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ResetRevokedInstallationAsync(string installationPublicId, string organizationId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string> GetInstallationPublicIdAsync(CancellationToken cancellationToken) => Task.FromResult("installation_1");
            public Task<IReadOnlyList<DeviceIdentity>> ListProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeviceIdentity>>(new DeviceIdentity[0]);
            public Task SelectProfileAsync(string profileId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task SaveProfileAsync(DeviceIdentity identity, byte[] protectedCredential, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
