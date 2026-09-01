using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CaptureServiceIntegrityTests
    {
        [Fact]
        public async Task Video_capture_persists_equal_original_and_composite_asset_groups()
        {
            var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var sessionRepository=new SqliteSessionRepository(db);
                var shots=new[]{Shot(root,"one"),Shot(root,"two")};
                await sessionRepository.SaveAsync(new Session{Id=sessionId,SessionName="Event",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=shots},CancellationToken.None);
                var composite=Write(root,"final.png","static");var videoComposite=Write(root,"final.mp4","video video");
                var deliverableService=(IDeliverableService)new DeliverableService(new SqliteDeliverableRepository(db),sessionRepository);
                var deliverable=await deliverableService.CreateWithCompositeVideoAsync(sessionId,null,"composite-id",composite,shots,videoComposite,shots.Select(x=>x.VideoPath).ToList(),null,CancellationToken.None);
                var loaded=await ((IDeliverableRepository)new SqliteDeliverableRepository(db)).GetAsync(deliverable.Id,CancellationToken.None);
                Assert.Equal(CaptureMediaModes.PictureAndVideo,loaded.MediaMode);
                Assert.All(loaded.Assets,asset=>Assert.IsType<DeliverableAsset>(asset));
                Assert.Equal(2,loaded.Assets.Count(x=>x.Role==DeliverableAssetRoles.OriginalPicture));Assert.Equal(2,loaded.Assets.Count(x=>x.Role==DeliverableAssetRoles.OriginalVideo));
                Assert.Single(loaded.Assets,x=>x.Role==DeliverableAssetRoles.FinalComposite);Assert.Single(loaded.Assets,x=>x.Role==DeliverableAssetRoles.CompositeVideo);
                Assert.All(loaded.Assets.Where(x=>x.Role==DeliverableAssetRoles.OriginalVideo||x.Role==DeliverableAssetRoles.CompositeVideo),x=>Assert.Equal("video/mp4",x.MimeType));
                foreach(var shot in shots){Assert.Contains(loaded.Assets,x=>x.Role==DeliverableAssetRoles.OriginalPicture&&x.CapturedShotId==shot.Id);Assert.Contains(loaded.Assets,x=>x.Role==DeliverableAssetRoles.OriginalVideo&&x.CapturedShotId==shot.Id);}
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }
        static CapturedShot Shot(string root,string name){return new CapturedShot{Id=name,Sequence=name=="one"?1:2,PicturePath=Write(root,name+".jpg","picture "+name),VideoPath=Write(root,name+".mp4","video "+name),CapturedAtUtc=DateTime.UtcNow};}
        static string Write(string root,string name,string value){var path=Path.Combine(root,name);File.WriteAllText(path,value);return path;}
    }
}
