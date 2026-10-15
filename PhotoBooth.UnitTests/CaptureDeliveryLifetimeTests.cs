using System;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using PhotoBooth.Core.Models;using PhotoBooth.Core.Services;using PhotoBooth.Database;using PhotoBooth.Infrastructure.Services;using Xunit;
namespace PhotoBooth.UnitTests {
 public sealed partial class PcApiConformanceTests {
 [Fact] public async Task Gif_video_settings_persist_independently_and_default_on(){using(var f=new Fixture()){
  var repo=new SqliteSettingsRepository(f.Database);var s=await repo.GetAsync(CancellationToken.None);Assert.True(s.CreateGif);Assert.True(s.CreateVideo);
  s.CreateGif=false;s.CreateVideo=false;await repo.SaveAsync(s,CancellationToken.None);
  s=await repo.GetAsync(CancellationToken.None);Assert.False(s.CreateGif);Assert.False(s.CreateVideo);
  s.CreateGif=true;await repo.SaveAsync(s,CancellationToken.None);s=await repo.GetAsync(CancellationToken.None);Assert.True(s.CreateGif);Assert.False(s.CreateVideo);
 }}
 [Fact] public async Task Protected_delivery_jobs_resume_after_restart_and_logout_without_borrowing_next_customer(){using(var f=new Fixture()){
  var capture=await f.Capture(null);var id=Guid.NewGuid().ToString("N");var asset=Guid.NewGuid().ToString("N");var now=DateTime.UtcNow.ToString("O");
  using(var c=f.Database.OpenConnection())using(var sql=c.CreateCommand()){
   sql.CommandText=@"INSERT INTO MediaAssets(Id,BoothSessionId,Kind,RelativePath,MimeType,FileLength,ContentHashSha256,State,RetentionClass,CreatedAtUtc,UpdatedAtUtc) VALUES($asset,$session,'FinalComposite','Final/test.png','image/png',1,'hash','Ready','Deliverable',$now,$now);
INSERT INTO Deliverables(Id,BoothSessionId,FinalCompositePath,State,CreatedAtUtc) VALUES($id,$session,'Final/test.png','Ready',$now);
INSERT INTO DeliverableAssets(DeliverableId,MediaAssetId,Role,Position) VALUES($id,$asset,'Composite',1);";
   sql.Parameters.AddWithValue("$asset",asset);sql.Parameters.AddWithValue("$session",capture.Id.ToString());sql.Parameters.AddWithValue("$id",id);sql.Parameters.AddWithValue("$now",now);sql.ExecuteNonQuery();
  }
  var item=new Deliverable{Id=id,BoothSessionId=capture.Id,Assets=new[]{new DeliverableAsset{Id=asset,Role=DeliverableAssetRoles.FinalComposite,FileLength=1,ContentHashSha256="hash"}}};
  var delivery=new LifetimeDeliverables{Item=item};var client=new LifetimeClient();var jobs=new SqliteDurableOutputJobRepository(f.Database);
  Func<RemoteDeliveryQueue> make=()=>new RemoteDeliveryQueue(jobs,new SqliteMediaAssetRepository(f.Database),new SqliteMediaAssetReplicaRepository(f.Database),new SqliteGalleryLinkCacheRepository(f.Database),delivery,client,f.Sessions,f.Options,null);
  using(var producer=make()){await producer.PrepareGalleryAsync(capture.Id,id,CancellationToken.None);await producer.EnqueueAsync(item,CancellationToken.None);}
  using(var c=f.Database.OpenConnection())using(var sql=c.CreateCommand()){sql.CommandText="SELECT ProtectedAuthorization FROM OutputJobs WHERE DeliverableId=$id LIMIT 1";sql.Parameters.AddWithValue("$id",id);var stored=(string)sql.ExecuteScalar();Assert.NotEmpty(stored);Assert.DoesNotContain("du3.",stored);}
  f.Sessions.SignOut();using(var worker=make()){
   var ready=new TaskCompletionSource<bool>();worker.GalleryReady+=(_,__)=>ready.TrySetResult(true);await worker.StartAsync(CancellationToken.None);
   Assert.Same(client.Started.Task,await Task.WhenAny(client.Started.Task,Task.Delay(10000)));
   f.Sessions.SignIn(new PcSession{AccountId="next-customer",OrganizationId="org_test",ExpiresAtUtc=DateTime.UtcNow.AddHours(1),RemoteSessionToken="es3.next.customer"});
   client.Release.TrySetResult(true);Assert.Same(ready.Task,await Task.WhenAny(ready.Task,Task.Delay(15000)));
   await worker.StopAsync(CancellationToken.None);Assert.All(client.Tokens,t=>Assert.Equal("du3.synthetic.delivery",t));Assert.Equal("GalleryReady",delivery.State);
  }
 }}
 sealed class LifetimeClient:IRemoteMediaDeliveryClient {
  public Task<string> FreezeGallerySiteAsync(string e,CancellationToken t)=>Task.FromResult("test-context");
  public readonly TaskCompletionSource<bool> Started=new TaskCompletionSource<bool>(),Release=new TaskCompletionSource<bool>();public readonly List<string> Tokens=new List<string>();
  static RemoteGalleryResult Ready()=>new RemoteGalleryResult{Succeeded=true,GalleryUrl=new Uri("https://example.test/g/fixture"),ExpiresAtUtc=DateTime.UtcNow.AddDays(1),DeliveryToken="du3.synthetic.delivery"};
  public Task<RemoteGalleryResult> PrepareGalleryAsync(Guid s,string d,CancellationToken t)=>Task.FromResult(Ready());
  public async Task<RemoteDeliveryOperationResult> UploadAssetAsync(Guid s,string d,MediaAssetRecord a,string k,string auth,CancellationToken t){lock(Tokens)Tokens.Add(auth);Started.TrySetResult(true);await Release.Task;return new RemoteDeliveryOperationResult{Succeeded=true,ReplicaProvider="r2"};}
  public Task<RemoteDeliveryOperationResult> CommitDeliverableAsync(Guid s,string d,string k,string auth,CancellationToken t){lock(Tokens)Tokens.Add(auth);return Task.FromResult<RemoteDeliveryOperationResult>(Ready());}
  public Task<RemoteGalleryResult> CreateGalleryAsync(Guid s,string d,string k,string auth,CancellationToken t){lock(Tokens)Tokens.Add(auth);return Task.FromResult(Ready());}
  public Task<RemoteDeliveryOperationResult> ReportRevenueAsync(RevenueReport r,string k,string auth,CancellationToken t)=>Task.FromResult<RemoteDeliveryOperationResult>(Ready());
  public Task<RemoteDeliveryOperationResult> ReportRevenueAsync(RevenueReport r,string k,CancellationToken t)=>Task.FromResult<RemoteDeliveryOperationResult>(Ready());
 }
 sealed class LifetimeDeliverables:IDeliverableService {
  public Deliverable Item;public string State;
  public Task<Deliverable> GetAsync(string id,CancellationToken t)=>Task.FromResult(Item);public Task<Deliverable> GetAsync(Guid s,string id,CancellationToken t)=>Task.FromResult(Item);
  public Task<IReadOnlyList<Deliverable>> GetByBoothSessionAsync(Guid s,CancellationToken t)=>Task.FromResult<IReadOnlyList<Deliverable>>(new[]{Item});
  public Task UpdateSharePathAsync(string id,string path,CancellationToken t)=>Task.CompletedTask;
  public Task UpdateRemoteStateAsync(string id,string state,string error,DateTime? uploaded,CancellationToken t){State=state;return Task.CompletedTask;}
  public Task<DeliverableAsset> AddAssetAsync(string id,string path,string role,IReadOnlyList<string> sources,CancellationToken t)=>throw new NotSupportedException();
  public Task<Deliverable> CreateAsync(Guid s,Guid? f,string a,string path,IReadOnlyList<CapturedShot> shots,DateTime? exp,CancellationToken t)=>throw new NotSupportedException();
  public Task<Deliverable> CreateWithCompositeVideoAsync(Guid s,Guid? f,string a,string path,IReadOnlyList<CapturedShot> shots,string v,IReadOnlyList<string> src,DateTime? exp,CancellationToken t)=>throw new NotSupportedException();
 }
 }}
