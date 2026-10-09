using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class DeviceReenrollmentTests
    {
        static readonly CancellationToken None = CancellationToken.None;

        [Theory]
        [InlineData(null, "HTTP 201")]
        [InlineData("timeout", "timed out")]
        [InlineData("network", "transport failed")]
        [InlineData("owner@example.test", "Code unknown")]
        public async Task Activation_diagnostics_record_outcome_without_identity_password_or_credentials(string failure, string expected)
        {
            using (var f = new Fixture())
            {
                var attempt = await f.Begin();
                f.Error = failure;
                await Record.ExceptionAsync(() => f.Complete(attempt));
                var log = string.Join("\n", f.Logger.Messages);
                Assert.Contains(expected, log);
                Assert.Contains("pc-activation-", log);
                Assert.DoesNotContain("owner@example.test", log);
                Assert.DoesNotContain("synthetic-owner-password", log);
                Assert.DoesNotContain("dv3.", log);
                Assert.DoesNotContain("challenge", log);
                Assert.DoesNotContain(attempt.PublicKeyThumbprint, log);
                Assert.DoesNotContain(f.InitialId, log);
                Assert.DoesNotContain(f.Options.PhotoApiBaseUrl, log);
            }
        }

        [Fact]
        public async Task Revoked_activation_clears_authority_then_fresh_owner_login_enrolls_with_new_key()
        {
            using (var f = new Fixture())
            {
                var sessions = new SqliteSessionRepository(f.Database);
                var evt = new Session { Id=Guid.NewGuid(), SessionName="Keep event", OutputDirectory=f.Root, StartedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
                await sessions.SaveAsync(evt, None);
                var capture = new Session { Id=Guid.NewGuid(), Kind=SessionKinds.Booth, EventId=evt.Id, OutputDirectory=Path.Combine(f.Root,"Captures","kept"), StartedAtUtc=DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow };
                await sessions.SaveAsync(capture, None);
                var jobs = new SqliteDurableOutputJobRepository(f.Database);
                await jobs.CreateIntentAsync(new DurableOutputJobRecord { Id="kept-job", SessionId=capture.Id, JobType=DurableOutputJobTypes.UploadAsset, IdempotencyKey="kept-idempotency", ProtectedAuthorization="obsolete", AuthorizationScope="employee:old" },None);
                var accounts = new SqlitePcAccountRepository(f.Database);
                await accounts.SaveRememberedLoginAsync(new PcRememberedLogin { AccountId="employee", OrganizationId="org", ProtectedToken=new byte[]{1,2}, ExpiresAtUtc=DateTime.UtcNow.AddDays(1),UpdatedAtUtc=DateTime.UtcNow },None);
                var oldKey = DeviceRequestProof.GetOrCreatePublicKey(f.InitialId,false).Thumbprint;
                var attempt = await f.Begin();
                f.Error = "device_revoked_reenrollment_required";
                await Assert.ThrowsAsync<DeviceReenrollmentRequiredException>(() => f.Complete(attempt));
                var replacement = await f.Devices.GetInstallationPublicIdAsync(None);
                Assert.NotEqual(f.InitialId,replacement);
                Assert.Null(await f.Devices.GetAsync(None));
                Assert.Null(await f.Devices.GetProtectedCredentialAsync(None));
                Assert.Empty(await f.Devices.ListProfilesAsync(None));
                Assert.Null(await accounts.GetRememberedLoginAsync(None));
                Assert.Equal(1L,f.Scalar("SELECT COUNT(*) FROM BoothSessions"));
                Assert.Equal(1L,f.Scalar("SELECT COUNT(*) FROM OutputJobs WHERE Id='kept-job' AND ProtectedAuthorization IS NULL AND AuthorizationScope IS NULL AND IdempotencyKey='kept-idempotency'"));
                // A stale attempt cannot silently sign with the replacement key or call complete again.
                var calls=f.CompleteCalls;
                await Assert.ThrowsAsync<DeviceReenrollmentRequiredException>(() => f.Complete(attempt));
                Assert.Equal(calls,f.CompleteCalls);
                f.Error=null;
                var fresh = await f.Begin();
                Assert.NotEqual(oldKey,fresh.PublicKeyThumbprint);
                Assert.Equal(2,f.OwnerLogins);
                var enrolled=await f.Complete(fresh);
                Assert.Equal(replacement,enrolled.InstallationId);
                Assert.Equal("new-device",(await f.Devices.GetAsync(None)).DeviceId);
                Assert.Equal("dv3.new.secret",Encoding.UTF8.GetString(ProtectedData.Unprotect(await f.Devices.GetProtectedCredentialAsync(None),null,DataProtectionScope.CurrentUser)));
            }
        }

        [Theory]
        [InlineData("device_locked",423)]
        [InlineData("device_capacity_locked",423)]
        [InlineData("device_key_mismatch",409)]
        [InlineData("device_limit_reached",409)]
        [InlineData("device_proof_invalid",401)]
        [InlineData("device_revoked",401)]
        [InlineData("device_revoked_reenrollment_required",500)]
        [InlineData("network",0)]
        public async Task Nonpermanent_errors_never_rotate_identity_or_discard_credentials(string error,int status)
        {
            using(var f=new Fixture())
            {
                var credential=await f.Devices.GetProtectedCredentialAsync(None);
                var key=DeviceRequestProof.GetOrCreatePublicKey(f.InitialId,false).Thumbprint;
                var attempt=await f.Begin();
                f.Error=error;f.Status=status;
                var failure=await Record.ExceptionAsync(()=>f.Complete(attempt));
                Assert.NotNull(failure);
                Assert.IsNotType<DeviceReenrollmentRequiredException>(failure);
                Assert.Equal(f.InitialId,await f.Devices.GetInstallationPublicIdAsync(None));
                Assert.Equal(credential,await f.Devices.GetProtectedCredentialAsync(None));
                Assert.Equal(key,DeviceRequestProof.GetOrCreatePublicKey(f.InitialId,false).Thumbprint);
            }
        }

        [Fact]
        public async Task Reset_preserves_other_organization_profiles_and_stale_responses_cannot_clear_new_registration()
        {
            using(var f=new Fixture())
            {
                await f.Save("other","other-org",f.InitialId);
                await f.Devices.SelectProfileAsync("old",None);
                await f.Devices.ResetRevokedInstallationAsync(f.InitialId,"org",None);
                var replacement=await f.Devices.GetInstallationPublicIdAsync(None);
                await f.Save("new","org",replacement);
                await f.Devices.ResetRevokedInstallationAsync(f.InitialId,"org",None);
                Assert.Equal("new",(await f.Devices.GetAsync(None)).ProfileId);
                await Assert.ThrowsAsync<DeviceReenrollmentRequiredException>(()=>f.Save("stale","org",f.InitialId));
                await f.Devices.SelectProfileAsync("other",None);
                Assert.Equal(f.InitialId,await f.Devices.GetInstallationPublicIdAsync(None));
                Assert.Equal(f.InitialId,(await f.Devices.GetAsync(None)).InstallationId);
                Assert.NotNull(DeviceRequestProof.GetOrCreatePublicKey(f.InitialId,false));
                await f.Devices.SelectProfileAsync("new",None);
                Assert.Equal(replacement,await f.Devices.GetInstallationPublicIdAsync(None));
                await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Devices.SelectProfileAsync("missing",None));
                Assert.Equal("new",(await f.Devices.GetAsync(None)).ProfileId);
            }
        }

        [Fact]
        public async Task Startup_rejected_credential_requires_owner_but_does_not_assume_permanent_device_revocation()
        {
            using(var f=new Fixture())
            using(var http=new HttpClient(new Handler(_=>Task.FromResult(Response("{\"error\":{\"code\":\"device_revoked\"}}",401)))))
            {
                var service=new CommercialEntitlementService(f.Devices,f.Options,http);
                var result=await service.CheckAppAccessAsync(None);
                Assert.Equal(CommercialAccessBlockReason.DeviceNotEnrolled,result.BlockReason);
                Assert.Equal(f.InitialId,await f.Devices.GetInstallationPublicIdAsync(None));
                Assert.NotNull(await f.Devices.GetProtectedCredentialAsync(None));
            }
        }

        static HttpResponseMessage Response(string body,int status=200)=>new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        sealed class Handler:HttpMessageHandler
        {
            readonly Func<HttpRequestMessage,Task<HttpResponseMessage>> send;
            public Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> value){send=value;}
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request);
        }
        sealed class Fixture:IDisposable
        {
            public readonly string Root=Path.Combine(Path.GetTempPath(),"miu-reenroll-"+Guid.NewGuid().ToString("N"));
            public readonly SqliteDatabase Database;
            public readonly SqliteDeviceIdentityRepository Devices;
            public readonly string InitialId;
            public readonly ApplicationOptions Options;
            readonly HashSet<string> keys=new HashSet<string>();
            readonly HttpClient http;
            readonly DeviceIdentityService service;
            public readonly CaptureLogger Logger = new CaptureLogger();
            public string Error;
            public int Status=409,OwnerLogins,CompleteCalls;
            public Fixture()
            {
                Directory.CreateDirectory(Root);
                Database=new SqliteDatabase(Path.Combine(Root,"test.db"));Database.Initialize();
                Devices=new SqliteDeviceIdentityRepository(Database);
                InitialId=Devices.GetInstallationPublicIdAsync(None).GetAwaiter().GetResult();
                keys.Add(InitialId);DeviceRequestProof.GetOrCreatePublicKey(InitialId);
                Save("old","org",InitialId).GetAwaiter().GetResult();
                Options=new ApplicationOptions{DataDirectory=Root,PhotoApiBaseUrl="https://api.test/"};
                http=new HttpClient(new Handler(Send));service=new DeviceIdentityService(Devices,Options,http,Logger);
            }
            public Task Save(string id,string org,string installation)=>Devices.SaveProfileAsync(new DeviceIdentity{ProfileId=id,DeviceId=id,OrganizationId=org,LocationId="location",DeviceName="Test",InstallationId=installation},ProtectedData.Protect(Encoding.UTF8.GetBytes("dv3.old.secret"),null,DataProtectionScope.CurrentUser),None);
            public Task<DeviceActivationAttempt> Begin()=>service.BeginOwnerActivationAsync("owner@example.test","synthetic-owner-password","2.0",None);
            public Task<DeviceIdentity> Complete(DeviceActivationAttempt attempt)=>service.CompleteOwnerActivationAsync(attempt,"location","Test","2.0",None);
            async Task<HttpResponseMessage> Send(HttpRequestMessage request)
            {
                var body=await request.Content.ReadAsStringAsync();
                if(request.RequestUri.AbsolutePath.EndsWith("/start"))
                {
                    OwnerLogins++;
                    Assert.Contains("synthetic-owner-password",body);
                    var id=Regex.Match(body,"\"installationId\":\"([^\"]+)\"").Groups[1].Value;
                    keys.Add(id);
                    var key=DeviceRequestProof.GetOrCreatePublicKey(id,false);
                    return Response("{\"activation\":{\"id\":\"attempt\",\"challenge\":\"challenge\",\"expiresAt\":"+DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()+",\"deviceProofRequired\":true,\"publicKeyThumbprint\":\""+key.Thumbprint+"\",\"organization\":{\"id\":\"org\",\"name\":\"Test\"},\"locations\":[{\"id\":\"location\",\"name\":\"Store\"}]}}");
                }
                CompleteCalls++;
                Assert.DoesNotContain("synthetic-owner-password",body);
                Assert.Contains("deviceProof",body);
                if(Error=="network")throw new HttpRequestException("owner@example.test synthetic-owner-password dv3.secret");
                if(Error=="timeout")throw new TaskCanceledException("Synthetic timeout");
                if(Error!=null)return Response("{\"error\":{\"code\":\""+Error+"\"}}",Status);
                return Response("{\"device\":{\"id\":\"new-device\",\"organizationId\":\"org\",\"locationId\":\"location\",\"name\":\"Test\"},\"credential\":\"dv3.new.secret\"}",201);
            }
            public long Scalar(string sql)
            {
                using(var connection=Database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
            }
            public void Dispose()
            {
                http.Dispose();
                foreach(var id in keys)using(var sha=SHA256.Create())
                using(var key=CngKey.Open("MiuPhotoBooth.Device."+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-","").ToLowerInvariant(),CngProvider.MicrosoftSoftwareKeyStorageProvider))key.Delete();
                SqliteConnection.ClearAllPools();Directory.Delete(Root,true);
            }
        }
        sealed class CaptureLogger : ILogger<DeviceIdentityService>
        {
            public readonly List<string> Messages = new List<string>();
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Assert.Null(exception);
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
