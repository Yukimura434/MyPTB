using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed partial class PcApiConformanceTests
    {
        [Fact]
        public async Task Concurrent_Manager_and_Booth_workers_only_lease_their_own_actor_jobs()
        {
            using(var f=new Fixture())
            {
                var capture=await f.Capture(null);
                var jobs=new SqliteDurableOutputJobRepository(f.Database);
                foreach(var scope in new[]{"employee:org_test:employee_test","PhotoBooth:code_test"})
                    await jobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=capture.Id,JobType=DurableOutputJobTypes.UploadAsset,IdempotencyKey=Guid.NewGuid().ToString("N"),AuthorizationScope=scope},CancellationToken.None);
                var employee=await jobs.LeaseNextDueAsync("worker-manager",DateTime.UtcNow.AddMinutes(1),CancellationToken.None,"employee:org_test:employee_test");
                Assert.Equal("employee:org_test:employee_test",employee.AuthorizationScope);
                Assert.Null(employee.ProtectedAuthorization);
                Assert.Null(await jobs.LeaseNextDueAsync("worker-manager",DateTime.UtcNow.AddMinutes(1),CancellationToken.None,"employee:org_test:employee_test"));
                var booth=await jobs.LeaseNextDueAsync("worker-booth",DateTime.UtcNow.AddMinutes(1),CancellationToken.None,"PhotoBooth:code_test");
                Assert.Equal("PhotoBooth:code_test",booth.AuthorizationScope);
            }
        }

        [Fact]
        public void New_event_login_cannot_inherit_previous_event_capture_authority()
        {
            var session=new PcSession{ExpiresAtUtc=DateTime.UtcNow.AddHours(1),RemoteSessionToken="es3.first.secret",EventAuthorization=new BoothAccessAuthorization{CodeType=BoothAccessCodeType.Event,EventId="event_test",AccessCodeId="event_test"}};
            var previous=RemoteDeliveryQueue.AuthorityScope(session);
            session.RemoteSessionToken="es3.second.secret";
            Assert.NotEqual(previous,RemoteDeliveryQueue.AuthorityScope(session));
            session.EventAuthorization.CodeType=BoothAccessCodeType.PhotoBooth;
            previous=RemoteDeliveryQueue.AuthorityScope(session);
            session.RemoteSessionToken="es3.third.secret";
            Assert.Equal(previous,RemoteDeliveryQueue.AuthorityScope(session));
        }
        [Fact]
        public async Task Enrolled_sqlite_profile_reopens_the_activation_key()
        {
            using(var f=new Fixture())
            {
                var publicId=await f.Devices.GetInstallationPublicIdAsync(CancellationToken.None);
                var identity=await f.Devices.GetAsync(CancellationToken.None);
                Assert.Equal(publicId,identity.InstallationId);
                Assert.Equal(publicId,Assert.Single(await f.Devices.ListProfilesAsync(CancellationToken.None)).InstallationId);
                Assert.Equal(DeviceRequestProof.GetOrCreatePublicKey(publicId,false).Thumbprint,DeviceRequestProof.GetOrCreatePublicKey(identity.InstallationId,false).Thumbprint);
                using(var request=new HttpRequestMessage(HttpMethod.Get,"https://api.test/api/v3/device/entitlement"))
                {
                    await DeviceRequestProof.SignAsync(request,f.Devices,"dv3.device_secret.synthetic",null,CancellationToken.None);
                    Assert.True(request.Headers.Contains("X-Device-Signature"));
                }
            }
        }

        [Fact]
        public void Missing_private_key_is_not_replaced_during_signing()
        {
            var missing="missing-x43-"+Guid.NewGuid().ToString("N");
            Assert.Throws<CryptographicException>(()=>DeviceRequestProof.SignActivation(missing,"attempt","challenge","org","thumb","loc","Test","windows","1"));
            Assert.Throws<CryptographicException>(()=>DeviceRequestProof.GetOrCreatePublicKey(missing,false));
        }

        [Fact]
        public async Task Manager_handoff_is_encrypted_and_revalidated_on_the_same_device()
        {
            using(var f=new Fixture())
            {
                var handler=new Handler((request,body)=>"{\"valid\":true,\"appRole\":\"BoothOperator\",\"device\":{\"id\":\"device_test\"},\"employee\":{\"id\":\"employee_test\",\"displayName\":\"Test\",\"expiresAt\":2000000000}}");
                var service=new PcAuthenticationService(new SqlitePcAccountRepository(f.Database),f.Sessions,f.Devices,new PasswordService(),f.Options,new HttpClient(handler));
                var handoff=await service.CreateHandoffAsync(CancellationToken.None);
                Assert.DoesNotContain("es3.employee",handoff);
                var accepted=await service.AcceptHandoffAsync(handoff,CancellationToken.None);
                Assert.True(accepted.Succeeded,accepted.Error);
                Assert.Equal(PcAppRole.BoothOperator,accepted.Session.Role);
                Assert.True(accepted.Session.CanManageConfiguration);
                Assert.Single(handler.Paths);
                Assert.Equal("/api/v3/device/employee-session",handler.Paths[0]);
                Assert.False((await service.AcceptHandoffAsync(handoff.Substring(1),CancellationToken.None)).Succeeded);
                handler.Response=(request,body)=>"{\"valid\":true,\"appRole\":\"LocalAdmin\",\"device\":{\"id\":\"device_other\"},\"employee\":{\"id\":\"employee_test\",\"expiresAt\":2000000000}}";
                Assert.False((await service.AcceptHandoffAsync(handoff,CancellationToken.None)).Succeeded);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Upload_uses_capture_scope_drive_mapping_and_completion_gallery(bool maliciousUpload)
        {
            using(var f=new Fixture())
            {
                var capture=await f.Capture("evt_test");
                var path=Path.Combine(f.Root,"image.jpg");File.WriteAllBytes(path,new byte[]{1,2,3,4});
                var asset=new MediaAssetRecord{Id="asset_test_123",SessionId=capture.Id,RelativePath="image.jpg",Kind=MediaAssetKinds.FinalComposite,MimeType="image/jpeg",FileLength=4,CreatedAtUtc=DateTime.UtcNow};
                using(var sha=SHA256.Create())asset.ContentHashSha256=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();
                var handler=new Handler((request,body)=>{
                    if(request.RequestUri.AbsolutePath.EndsWith("/booth-sessions")){Assert.Contains("\"eventId\":\"evt_test\"",body);return "{\"session\":{\"id\":\""+capture.Id.ToString("N")+"\",\"status\":\"active\"}}";}
                    if(request.RequestUri.AbsolutePath.EndsWith("/reserve")){Assert.Contains(asset.ContentHashSha256,body);return "{\"status\":\"reserved\",\"uploadId\":\"upload_test_123\",\"replicaId\":\"replica_test\",\"destination\":\"drive\",\"uploadUrl\":\"https://"+(maliciousUpload?"other.test":"api.test")+"/api/v3/device/media/upload_test_123\"}";}
                    if(request.RequestUri.AbsolutePath.EndsWith("/complete"))return "{\"session\":{\"status\":\"completed\"},\"gallery\":{\"id\":\"gallery_test\",\"status\":\"ready\",\"url\":\"https://gallery.test/g/test\",\"expiresAt\":2000000000}}";
                    return "{\"status\":\"committed\"}";
                });
                var client=new RemoteMediaDeliveryClient(f.Devices,f.Sessions,f.LocalSessions,new StorageManager(f.Options,null,f.LocalSessions,null),f.Options,new HttpClient(handler));
                var upload=await client.UploadAssetAsync(capture.Id,"deliverable_test",asset,"upload:test:1","es3.employee.secret",CancellationToken.None);
                Assert.Equal(!maliciousUpload,upload.Succeeded);
                if(maliciousUpload){Assert.Equal("invalid_upload_destination",upload.ErrorCode);Assert.Equal(2,handler.Paths.Count);return;}
                Assert.Equal(MediaReplicaProviders.GoogleDrive,upload.ReplicaProvider);
                Assert.Contains("PUT",handler.Methods);
                var completed=Assert.IsType<RemoteGalleryResult>(await client.CommitDeliverableAsync(capture.Id,"deliverable_test","complete:test:1","es3.employee.secret",CancellationToken.None));
                Assert.True(completed.Succeeded);
                Assert.Equal("https://gallery.test/g/test",completed.GalleryUrl.AbsoluteUri);
                Assert.DoesNotContain(handler.Paths,p=>p.EndsWith("/gallery"));
            }
        }

        sealed class Handler:HttpMessageHandler
        {
            public Func<HttpRequestMessage,string,string> Response;
            public readonly List<string> Paths=new List<string>();
            public readonly List<string> Methods=new List<string>();
            public Handler(Func<HttpRequestMessage,string,string> response){Response=response;}
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                Assert.True(request.Headers.Contains("X-Device-Signature"));
                Assert.Equal("Bearer",request.Headers.Authorization.Scheme);
                Paths.Add(request.RequestUri.AbsolutePath);Methods.Add(request.Method.Method);
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Response(request,request.Content==null?null:await request.Content.ReadAsStringAsync()),Encoding.UTF8,"application/json")};
            }
        }

        sealed class Fixture:IDisposable
        {
            public readonly string Root=Path.Combine(Path.GetTempPath(),"miu-x43-"+Guid.NewGuid().ToString("N"));
            public readonly SqliteDatabase Database;
            public readonly SqliteDeviceIdentityRepository Devices;
            public readonly SqliteSessionRepository LocalSessions;
            public readonly PcSessionContext Sessions=new PcSessionContext();
            public readonly ApplicationOptions Options;
            readonly string installation;
            public Fixture()
            {
                Directory.CreateDirectory(Root);Database=new SqliteDatabase(Path.Combine(Root,"test.db"));Database.Initialize();
                Devices=new SqliteDeviceIdentityRepository(Database);LocalSessions=new SqliteSessionRepository(Database);
                installation=Devices.GetInstallationPublicIdAsync(CancellationToken.None).GetAwaiter().GetResult();
                DeviceRequestProof.GetOrCreatePublicKey(installation);
                Devices.SaveProfileAsync(new DeviceIdentity{ProfileId="profile_test",DeviceId="device_test",OrganizationId="org_test",LocationId="location_test",DeviceName="Test",InstallationId=installation},ProtectedData.Protect(Encoding.UTF8.GetBytes("dv3.device_secret.synthetic"),null,DataProtectionScope.CurrentUser),CancellationToken.None).GetAwaiter().GetResult();
                Options=new ApplicationOptions{DataDirectory=Root,PhotoApiBaseUrl="https://api.test/",UploadTimeoutSeconds=15};
                Sessions.SignIn(new PcSession{AccountId="employee_test",OrganizationId="org_test",DisplayName="Test",Role=PcAppRole.BoothOperator,AuthenticationMode=PcAuthenticationMode.OnlineDeviceBound,AuthenticatedAtUtc=DateTime.UtcNow,ExpiresAtUtc=DateTime.UtcNow.AddHours(1),RemoteSessionToken="es3.employee.secret"});
            }
            public async Task<Session> Capture(string remoteEvent)
            {
                var evt=new Session{Id=Guid.NewGuid(),SessionName="Test",OutputDirectory=Root,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,RemoteEventId="other_event"};
                await LocalSessions.SaveAsync(evt,CancellationToken.None);
                var capture=new Session{Id=Guid.NewGuid(),Kind=SessionKinds.Booth,EventId=evt.Id,RemoteEventId=remoteEvent,OutputDirectory=Path.Combine(Root,"Captures","test"),StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow};
                await LocalSessions.SaveAsync(capture,CancellationToken.None);return capture;
            }
            public void Dispose()
            {
                using(var sha=SHA256.Create())
                using(var key=CngKey.Open("MiuPhotoBooth.Device."+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(installation))).Replace("-","").ToLowerInvariant(),CngProvider.MicrosoftSoftwareKeyStorageProvider))key.Delete();
                SqliteConnection.ClearAllPools();Directory.Delete(Root,true);
            }
        }
    }
}
