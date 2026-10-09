using System;
using System.IO;
using System.Linq;
using System.Threading;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using PhotoBooth.Core.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class RemoteDeliveryAssetPolicyTests
    {
        [Fact]
        public void Delivery_keeps_only_the_single_composed_video()
        {
            var selected=RemoteDeliveryAssetPolicy.Select(new[]
            {
                Asset("shot-video-1",DeliverableAssetRoles.OriginalVideo,1),
                Asset("shot-photo-1",DeliverableAssetRoles.OriginalPicture,1),
                Asset("final",DeliverableAssetRoles.FinalComposite,1),
                Asset("shot-video-2",DeliverableAssetRoles.OriginalVideo,2),
                Asset("composed-video",DeliverableAssetRoles.CompositeVideo,1),
                Asset("duplicate-composed-video",DeliverableAssetRoles.CompositeVideo,2),
                Asset("gif",DeliverableAssetRoles.Gif,1)
            });

            Assert.DoesNotContain(selected,value=>value.Role==DeliverableAssetRoles.OriginalVideo);
            Assert.Single(selected,value=>value.Role==DeliverableAssetRoles.CompositeVideo);
            Assert.Contains(selected,value=>value.Id=="composed-video");
            Assert.Contains(selected,value=>value.Role==DeliverableAssetRoles.FinalComposite);
            Assert.Contains(selected,value=>value.Role==DeliverableAssetRoles.OriginalPicture);
        }

        static DeliverableAsset Asset(string id,string role,int position)=>new DeliverableAsset{Id=id,Role=role,Position=position};

        [Fact]
        public async System.Threading.Tasks.Task Durable_jobs_lease_upload_before_commit_and_gallery()
        {
            var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"queue.db"));database.Initialize();var now=DateTime.UtcNow.ToString("O");
                var sessionId=Guid.NewGuid();var deliverableId=Guid.NewGuid().ToString("N");var assetId=Guid.NewGuid().ToString("N");
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText=@"INSERT INTO Events(Id,Name,OutputDirectory,CreatedAtUtc,UpdatedAtUtc) VALUES('event','Event','.', $now,$now);
INSERT INTO ConfigurationVersions(Id,EventId,VersionNumber,ConfigurationJson,ContentHashSha256,State,CreatedAtUtc) VALUES('config','event',1,'{}','hash','Active',$now);
INSERT INTO BoothSessions(Id,EventId,ConfigurationVersionId,OriginDeviceId,State,StartedAtUtc,UpdatedAtUtc,StorageRootRelativePath) VALUES($session,'event','config','local-unenrolled','Active',$now,$now,'Captures/session');
INSERT INTO MediaAssets(Id,BoothSessionId,Kind,RelativePath,MimeType,FileLength,ContentHashSha256,State,RetentionClass,CreatedAtUtc,UpdatedAtUtc) VALUES($asset,$session,'FinalComposite','Captures/final.png','image/png',1,'hash','Ready','Deliverable',$now,$now);
INSERT INTO Deliverables(Id,BoothSessionId,FinalCompositePath,State,CreatedAtUtc) VALUES($deliverable,$session,'Captures/final.png','Ready',$now);
INSERT INTO DeliverableAssets(DeliverableId,MediaAssetId,Role,Position) VALUES($deliverable,$asset,'Composite',1);";
                    command.Parameters.AddWithValue("$now",now);command.Parameters.AddWithValue("$session",sessionId.ToString());command.Parameters.AddWithValue("$asset",assetId);command.Parameters.AddWithValue("$deliverable",deliverableId);command.ExecuteNonQuery();
                }
                var repository=new SqliteDurableOutputJobRepository(database);
                var upload=await repository.CreateIntentAsync(Job(sessionId,deliverableId,assetId,DurableOutputJobTypes.UploadAsset,"upload"),CancellationToken.None);
                await repository.CreateIntentAsync(Job(sessionId,deliverableId,null,DurableOutputJobTypes.CommitDeliverable,"commit"),CancellationToken.None);
                await repository.CreateIntentAsync(Job(sessionId,deliverableId,null,DurableOutputJobTypes.CreateGallery,"gallery"),CancellationToken.None);
                var first=await repository.LeaseNextDueAsync("lease",DateTime.UtcNow.AddMinutes(1),CancellationToken.None);Assert.Equal(upload.Id,first.Id);
                await repository.SetStateAsync(first.Id,DurableOutputJobStates.Submitting,null,CancellationToken.None);await repository.CompleteAsync(first.Id,"ready",CancellationToken.None);
                var second=await repository.LeaseNextDueAsync("lease",DateTime.UtcNow.AddMinutes(1),CancellationToken.None);Assert.Equal(DurableOutputJobTypes.CommitDeliverable,second.JobType);
                await repository.SetStateAsync(second.Id,DurableOutputJobStates.Submitting,null,CancellationToken.None);await repository.CompleteAsync(second.Id,"ready",CancellationToken.None);
                var third=await repository.LeaseNextDueAsync("lease",DateTime.UtcNow.AddMinutes(1),CancellationToken.None);Assert.Equal(DurableOutputJobTypes.CreateGallery,third.JobType);
                await repository.FailAsync(third.Id,"offline",CancellationToken.None);
                Assert.Equal(1,await repository.RetryPendingAsync(deliverableId,CancellationToken.None));
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText="SELECT State||':'||AttemptCount FROM OutputJobs WHERE Id=$id";command.Parameters.AddWithValue("$id",third.Id);
                    Assert.Equal(DurableOutputJobStates.RetryWaiting+":0",Convert.ToString(command.ExecuteScalar()));
                }
            }
            finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        static DurableOutputJobRecord Job(Guid sessionId,string deliverableId,string assetId,string type,string suffix)=>new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=sessionId,DeliverableId=deliverableId,AssetId=assetId,JobType=type,IdempotencyKey="test:"+deliverableId+":"+suffix,CreatedAtUtc=DateTime.UtcNow};
    }
}
