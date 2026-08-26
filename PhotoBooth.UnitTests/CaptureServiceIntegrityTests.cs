using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CaptureServiceIntegrityTests
    {
        [Fact]
        public async Task Motion_capture_persists_equal_original_and_composite_asset_groups()
        {
            var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var sessionRepository=new SqliteSessionRepository(db);
                var shots=new[]{Shot(root,"one"),Shot(root,"two")};
                await sessionRepository.SaveAsync(new Session{Id=sessionId,SessionName="Event",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=shots},CancellationToken.None);
                var composite=Write(root,"final.png","static");var motionComposite=Write(root,"final_MP.jpg","motion Camera:MotionPhoto ftyp");
                var capture=await new CaptureService(new SqliteCaptureRepository(db),sessionRepository).CreateWithMotionCompositeAsync(sessionId,null,"composite-id",composite,shots,motionComposite,shots.Select(x=>x.MotionPhotoPath).ToList(),null,CancellationToken.None);
                var loaded=await new SqliteCaptureRepository(db).GetAsync(capture.Id,CancellationToken.None);
                Assert.Equal(CaptureMediaModes.PictureAndMotion,loaded.MediaMode);
                Assert.Equal(2,loaded.Photos.Count(x=>x.PhotoType==CaptureAssetTypes.Picture));Assert.Equal(2,loaded.Photos.Count(x=>x.PhotoType==CaptureAssetTypes.MotionPhoto));
                Assert.Single(loaded.Photos,x=>x.PhotoType==CaptureAssetTypes.Composite);Assert.Single(loaded.Photos,x=>x.PhotoType==CaptureAssetTypes.MotionPhotoComposite);
                foreach(var shot in shots){Assert.Contains(loaded.Photos,x=>x.PhotoType==CaptureAssetTypes.Picture&&x.CapturedImageId==shot.Id);Assert.Contains(loaded.Photos,x=>x.PhotoType==CaptureAssetTypes.MotionPhoto&&x.CapturedImageId==shot.Id);}
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }
        static CapturedShot Shot(string root,string name){return new CapturedShot{Id=name,Sequence=name=="one"?1:2,PicturePath=Write(root,name+".jpg","picture "+name),MotionPhotoPath=Write(root,name+"_MP.jpg","motion Camera:MotionPhoto ftyp "+name),CapturedAtUtc=DateTime.UtcNow};}
        static string Write(string root,string name,string value){var path=Path.Combine(root,name);File.WriteAllText(path,value);return path;}
    }
}
