using System;
using System.Collections.Generic;
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
    public sealed class BoothAccessServiceTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Event_login_uses_event_credentials_and_preserves_binding(bool legacySettings)
        {
            var handler = new RecordingHandler("{\"authorization\":{\"accessCodeId\":\"evt_1\",\"eventId\":\"evt_1\",\"eventName\":\"Test Event\",\"codeType\":\"event\",\"sessionToken\":\"es3.event.secret\",\"expiresAt\":2000000000" + (legacySettings ? ",\"printCopies\":2,\"photoCount\":3" : "") + "}}", HttpStatusCode.OK);
            var result = await CreateService(handler).LoginEventAsync("ev23456789", "synthetic-password", CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal("/api/v3/device/events/login", handler.Path);
            Assert.Contains("\"password\":\"synthetic-password\"", handler.Body);
            Assert.Null(handler.EmployeeSession);
            Assert.True(result.Authorization.IsEvent);
            Assert.Equal("evt_1", result.Authorization.EventId);
            Assert.Equal(1, result.Authorization.Settings.PrintCopies);
            Assert.Equal(8, result.Authorization.Settings.PhotoCount);
        }

        [Theory]
        [InlineData("event", "evt_1", true)]
        [InlineData("photobooth", "evt_1", false)]
        [InlineData("event", "evt_other", false)]
        [InlineData("unknown", "evt_1", false)]
        public async Task Runtime_validation_rejects_changed_principal_or_event(string type, string eventId, bool expected)
        {
            var handler = new RecordingHandler("{\"valid\":true,\"authorization\":{\"accessCodeId\":\"evt_1\",\"eventId\":\"" + eventId + "\",\"codeType\":\"" + type + "\",\"expiresAt\":2000000000}}", HttpStatusCode.OK);
            var result = await CreateService(handler).ValidateAsync(new BoothAccessAuthorization { CodeType = BoothAccessCodeType.Event, AccessCodeId = "evt_1", EventId = "evt_1", SessionToken = "es3.event.secret" }, CancellationToken.None);
            Assert.Equal(expected, result.Succeeded);
            Assert.Equal("/api/v3/device/booth-access-session", handler.Path);
            Assert.Equal("es3.event.secret", handler.EmployeeSession);
            Assert.Null(handler.Body);
        }

        [Fact]
        public async Task PhotoBooth_redeem_preserves_pending_capture_identity()
        {
            var handler = new RecordingHandler("{\"authorization\":{\"accessCodeId\":\"code_1\",\"boothSessionId\":\"capture_existing\",\"codeType\":\"photobooth\",\"sessionToken\":\"es3.code.secret\",\"expiresAt\":2000000000}}", HttpStatusCode.OK);
            var result = await CreateService(handler).RedeemAsync("23456789", CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal("capture_existing", result.Authorization.BoothSessionId);
        }
        [Fact]
        public async Task PhotoBooth_create_uses_the_explicit_one_run_contract()
        {
            var handler = new RecordingHandler(
                "{\"session\":{\"id\":\"photo-1\",\"codeType\":\"photobooth\",\"expiresAt\":2000000000,\"photoCount\":6,\"countdownSeconds\":3,\"gifFrameDurationMilliseconds\":1200,\"resultDisplaySeconds\":30,\"printCopies\":1,\"reusable\":false},\"code\":\"PHTA2345\"}",
                HttpStatusCode.Created);
            var service = CreateService(handler);

            var created = await service.CreatePhotoBoothCodeAsync(new CreatePhotoBoothCodeRequest
            {
                Settings = new BoothAccessSettings
                {
                    PhotoCount = 6,
                    CountdownSeconds = 3,
                    GifFrameDurationMilliseconds = 1200,
                    ResultDisplaySeconds = 30,
                    PrintCopies = 1
                }
            }, CancellationToken.None);

            Assert.Equal("/api/v3/device/photobooth-codes", handler.Path);
            Assert.Contains("\"gifFrameDurationMilliseconds\":1200", handler.Body);
            Assert.DoesNotContain("durationMinutes", handler.Body);
            Assert.Equal("employee-token", handler.EmployeeSession);
            Assert.Equal(BoothAccessCodeType.PhotoBooth, created.CodeType);
            Assert.Equal("PHTA2345", created.Code);
        }

        [Fact]
        public async Task SelfBooth_create_uses_the_explicit_reusable_contract()
        {
            var handler = new RecordingHandler(
                "{\"session\":{\"id\":\"self-1\",\"codeType\":\"selfbooth\",\"expiresAt\":2000000000,\"photoCount\":8,\"countdownSeconds\":3,\"gifFrameDurationMilliseconds\":0,\"resultDisplaySeconds\":30,\"printCopies\":0,\"albumUrl\":\"https://gallery.test/g/album\",\"reusable\":true},\"code\":\"SELF2345\"}",
                HttpStatusCode.Created);
            var service = CreateService(handler);

            var created = await service.CreateSelfBoothPassAsync(new CreateSelfBoothPassRequest
            {
                DurationMinutes = 240,
                Settings = new BoothAccessSettings
                {
                    PhotoCount = 8,
                    CountdownSeconds = 3,
                    GifFrameDurationMilliseconds = 0,
                    ResultDisplaySeconds = 30,
                    PrintCopies = 0
                }
            }, CancellationToken.None);

            Assert.Equal("/api/v3/device/selfbooth-passes", handler.Path);
            Assert.Contains("\"durationMinutes\":240", handler.Body);
            Assert.DoesNotContain("gifFrameDurationMilliseconds", handler.Body);
            Assert.Equal(BoothAccessCodeType.SelfBooth, created.CodeType);
            Assert.Equal("https://gallery.test/g/album", created.AlbumUrl);
        }

        [Theory]
        [InlineData("photobooth", false, BoothAccessCodeType.PhotoBooth, true, false)]
        [InlineData("selfbooth", true, BoothAccessCodeType.SelfBooth, false, true)]
        public async Task Redeem_preserves_the_server_product_discriminator(
            string codeType,
            bool reusable,
            BoothAccessCodeType expectedType,
            bool expectedOneTime,
            bool expectedReusable)
        {
            var handler = new RecordingHandler(
                "{\"authorization\":{\"accessCodeId\":\"code-1\",\"codeType\":\"" + codeType + "\",\"sessionToken\":\"session-token\",\"expiresAt\":2000000000,\"photoCount\":8,\"countdownSeconds\":3,\"gifFrameDurationMilliseconds\":800,\"resultDisplaySeconds\":30,\"printCopies\":1,\"reusable\":" + reusable.ToString().ToLowerInvariant() + "}}",
                HttpStatusCode.OK);
            var service = CreateService(handler);

            var result = await service.RedeemAsync("PHTA2345", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("/api/v3/device/booth-access-codes/redeem", handler.Path);
            Assert.Equal(expectedType, result.Authorization.CodeType);
            Assert.Equal(expectedOneTime, result.Authorization.IsOneTimeCode);
            Assert.Equal(expectedReusable, result.Authorization.IsReusableSelfBooth);
            Assert.True(result.Authorization.UsesAccessCodeSettings);
        }

        static BoothAccessService CreateService(RecordingHandler handler)
        {
            var sessions = new PcSessionContext();
            sessions.SignIn(new PcSession
            {
                AccountId = "manager-1",
                DisplayName = "Manager",
                OrganizationId = "org-1",
                Role = PcAppRole.LocalAdmin,
                AuthenticationMode = PcAuthenticationMode.OnlineDeviceBound,
                AuthenticatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                RemoteSessionToken = "employee-token"
            });
            return new BoothAccessService(
                new DeviceRepository("dv3.dcr_test.secret"),
                sessions,
                new ApplicationOptions { PhotoApiBaseUrl = "https://api.test/" },
                new HttpClient(handler));
        }

        sealed class RecordingHandler : HttpMessageHandler
        {
            readonly string responseBody;
            readonly HttpStatusCode responseStatus;

            public RecordingHandler(string responseBody, HttpStatusCode responseStatus)
            {
                this.responseBody = responseBody;
                this.responseStatus = responseStatus;
            }

            public string Path { get; private set; }
            public string Body { get; private set; }
            public string EmployeeSession { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Path = request.RequestUri.AbsolutePath;
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                EmployeeSession = request.Headers.Contains("X-Employee-Session")
                    ? string.Join(",", request.Headers.GetValues("X-Employee-Session"))
                    : null;
                return new HttpResponseMessage(responseStatus)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                };
            }
        }

        sealed class DeviceRepository : IDeviceIdentityRepository
        {
            readonly byte[] protectedCredential;
            readonly DeviceIdentity identity = new DeviceIdentity
            {
                DeviceId = "device-1",
                InstallationId = "installation-booth-access-tests",
                OrganizationId = "org-1",
                EnrollmentState = DeviceEnrollmentState.Enrolled
            };

            public DeviceRepository(string credential)
            {
                DeviceRequestProof.GetOrCreatePublicKey(identity.InstallationId);
                protectedCredential = ProtectedData.Protect(Encoding.UTF8.GetBytes(credential), null, DataProtectionScope.CurrentUser);
            }

            public Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken) => Task.FromResult(identity);
            public Task<byte[]> GetProtectedCredentialAsync(CancellationToken cancellationToken) => Task.FromResult(protectedCredential);
            public Task SaveEnrolledAsync(DeviceIdentity value, byte[] credential, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ResetRevokedInstallationAsync(string installationPublicId, string organizationId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string> GetInstallationPublicIdAsync(CancellationToken cancellationToken) => Task.FromResult("installation-1");
            public Task<IReadOnlyList<DeviceIdentity>> ListProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeviceIdentity>>(new[] { identity });
            public Task SelectProfileAsync(string profileId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task SaveProfileAsync(DeviceIdentity value, byte[] credential, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
