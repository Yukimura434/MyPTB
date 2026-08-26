using System;using System.Collections.Generic;using System.IO;using System.Threading;using System.Threading.Tasks;using PhotoBooth.Core.Models;using PhotoBooth.Database;using Xunit;
namespace PhotoBooth.UnitTests
{
 public sealed class DatabaseNormalizationTests
 {
 [Fact] public async Task Child_entities_are_stored_in_normalized_tables()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();
   var session=new Session{Id=Guid.NewGuid(),SessionName="Base_session",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedFiles=new[]{Path.Combine(root,"IMG_0001.JPG")},CapturedImageIds=new[]{"260807000001"}};await new SqliteSessionRepository(db).SaveAsync(session,CancellationToken.None);
   var frame=new Frame{Id=Guid.NewGuid(),Name="Frame",CreatedAtUtc=DateTime.UtcNow,Slots=new[]{new FrameSlot{Id=Guid.NewGuid(),Index=0,X=1,Y=2,Width=3,Height=4}}};await new SqliteFrameRepository(db).SaveAsync(frame,CancellationToken.None);
   var preset=new Preset{Id=Guid.NewGuid(),Name="Preset",SettingsJson="{\"Brightness\":0.25}",CreatedAtUtc=DateTime.UtcNow,ModifiedAtUtc=DateTime.UtcNow};await new SqlitePresetRepository(db).SaveAsync(preset,CancellationToken.None);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT (SELECT COUNT(*) FROM CapturedImages),(SELECT COUNT(*) FROM FrameSlots),(SELECT COUNT(*) FROM PresetProcessingSettings)";using(var r=q.ExecuteReader()){Assert.True(r.Read());Assert.Equal(1,r.GetInt32(0));Assert.Equal(1,r.GetInt32(1));Assert.Equal(1,r.GetInt32(2));}}
  }

  [Fact] public async Task Capture_group_is_added_without_changing_existing_session_keys()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();
   var sessionId=Guid.NewGuid();var imagePath=Path.Combine(root,"IMG_0001.JPG");var compositePath=Path.Combine(root,"frm001.png");
   var session=new Session{Id=sessionId,SessionName="Base_session",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedFiles=new[]{imagePath},CapturedImageIds=new[]{"260807000001"}};var sessions=new SqliteSessionRepository(db);await sessions.SaveAsync(session,CancellationToken.None);
   var repository=new SqliteCaptureRepository(db);var captureId=Guid.NewGuid().ToString("N");var pictureAssetId=Guid.NewGuid().ToString("N");var compositeAssetId=Guid.NewGuid().ToString("N");await repository.SaveAsync(new PhotoCapture{Id=captureId,SessionId=sessionId,CompositeImageId="composite-1",CompositePath=compositePath,Status="Pending",CreatedAtUtc=DateTime.UtcNow,Photos=new[]{new CapturePhoto{Id=pictureAssetId,CaptureId=captureId,CapturedImageId="260807000001",LocalPath=imagePath,PhotoType=CaptureAssetTypes.Picture,Position=1,MimeType="image/jpeg",CreatedAtUtc=DateTime.UtcNow,SourceAssetIds=new string[0],IsUploaded=false},new CapturePhoto{Id=compositeAssetId,CaptureId=captureId,LocalPath=compositePath,PhotoType=CaptureAssetTypes.Composite,Position=1,MimeType="image/png",CreatedAtUtc=DateTime.UtcNow,SourceAssetIds=new[]{pictureAssetId},IsUploaded=false}}},CancellationToken.None);
   var loaded=await repository.GetAsync(captureId,CancellationToken.None);Assert.NotNull(loaded);Assert.Equal(sessionId,loaded.SessionId);Assert.Equal(2,loaded.Photos.Count);Assert.Equal(pictureAssetId,loaded.Photos[1].SourceAssetIds[0]);
   var existingSession=await sessions.GetAsync(sessionId,CancellationToken.None);Assert.NotNull(existingSession);Assert.Equal("260807000001",existingSession.CapturedImageIds[0]);
   using(var c=db.OpenConnection()){using(var q=c.CreateCommand()){q.CommandText="PRAGMA integrity_check";Assert.Equal("ok",q.ExecuteScalar());}using(var q=c.CreateCommand()){q.CommandText="PRAGMA foreign_key_check";using(var reader=q.ExecuteReader())Assert.False(reader.Read());}}
  }

  [Fact] public async Task Asset_lineage_rejects_cross_capture_relationships()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var sessions=new SqliteSessionRepository(db);await sessions.SaveAsync(new Session{Id=sessionId,SessionName="Base_session",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedFiles=new string[0],CapturedImageIds=new string[0]},CancellationToken.None);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){var capture1=Guid.NewGuid().ToString("N");var capture2=Guid.NewGuid().ToString("N");var asset1=Guid.NewGuid().ToString("N");var asset2=Guid.NewGuid().ToString("N");q.CommandText="INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc) VALUES('"+capture1+"','"+sessionId+"','a','Pending','"+DateTime.UtcNow.ToString("O")+"'),('"+capture2+"','"+sessionId+"','b','Pending','"+DateTime.UtcNow.ToString("O")+"');INSERT INTO CapturePhotos(Id,CaptureId,LocalPath,PhotoType,Position,IsUploaded,FileLength) VALUES('"+asset1+"','"+capture1+"','a','Composite',1,0,0),('"+asset2+"','"+capture2+"','b','Composite',1,0,0);INSERT INTO CaptureAssetSources(AssetId,SourceAssetId) VALUES('"+asset1+"','"+asset2+"');";Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>q.ExecuteNonQuery());}
  }

  [Fact] public void Reinitialize_backfills_only_compatible_asset_lineage()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText=@"INSERT INTO CustomerSessions(Id,StartedAtUtc,OutputDirectory,SessionName) VALUES('session','2026-08-25T00:00:00Z',$root,'Event');
INSERT INTO CapturedImages(Id,SessionId,Sequence,FilePath,CapturedAtUtc,MotionPhotoPath) VALUES('image','session',1,'picture.jpg','2026-08-25T00:00:00Z','motion_MP.jpg');
INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc,MediaMode) VALUES('capture','session','composite.png','Pending','2026-08-25T00:00:00Z','PictureAndMotion');
INSERT INTO CapturePhotos(Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,FileLength,CreatedAtUtc,AssetStatus) VALUES
('picture','capture','image','picture.jpg','Picture',1,1,'2026-08-25T00:00:00Z','Ready'),
('motion','capture','image','motion_MP.jpg','MotionPhoto',1,1,'2026-08-25T00:00:00Z','Ready'),
('composite','capture',NULL,'composite.png','Composite',1,1,'2026-08-25T00:00:00Z','Ready'),
('motion-composite','capture',NULL,'composite_MP.jpg','MotionPhotoComposite',1,1,'2026-08-25T00:00:00Z','Ready');";q.Parameters.AddWithValue("$root",root);q.ExecuteNonQuery();}
   db.Initialize();
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT AssetId||':'||SourceAssetId FROM CaptureAssetSources ORDER BY AssetId";var values=new List<string>();using(var reader=q.ExecuteReader())while(reader.Read())values.Add(reader.GetString(0));Assert.Equal(new[]{"composite:picture","motion-composite:motion"},values);}
  }

  [Fact] public async Task Data_statistics_are_aggregated_only_from_sqlite()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var captureId=Guid.NewGuid().ToString("N");var imageId="image-1";var assetId=Guid.NewGuid().ToString("N");
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="INSERT INTO CustomerSessions(Id,StartedAtUtc,OutputDirectory,SessionName) VALUES($session,$now,$root,'Event');INSERT INTO CapturedImages(Id,SessionId,Sequence,FilePath,CapturedAtUtc) VALUES($image,$session,1,'motion_MP.jpg',$now);INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc) VALUES($capture,$session,'final.png','Pending',$now);INSERT INTO CapturePhotos(Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,IsUploaded,MimeType,FileLength,CreatedAtUtc,AssetStatus) VALUES($asset,$capture,$image,'motion_MP.jpg','MotionPhoto',1,0,'image/jpeg',1024,$now,'Ready');INSERT INTO PrintJobs(Id,SessionId,CaptureId,PrinterName,Status,PrintedAtUtc) VALUES($print,$session,$capture,'Printer','Success',$now);";q.Parameters.AddWithValue("$session",sessionId.ToString());q.Parameters.AddWithValue("$capture",captureId);q.Parameters.AddWithValue("$image",imageId);q.Parameters.AddWithValue("$asset",assetId);q.Parameters.AddWithValue("$print",Guid.NewGuid().ToString());q.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));q.Parameters.AddWithValue("$root",root);q.ExecuteNonQuery();}
   var value=await new SqliteStatsRepository(db).GetDataStatisticsAsync(CancellationToken.None);Assert.Equal(1,value.SessionCount);Assert.Equal(1,value.CaptureCount);Assert.Equal(1,value.MotionPhotoCount);Assert.Equal(1,value.SuccessfulPrintCount);Assert.Equal(1024,value.TotalAssetBytes);Assert.Single(value.RecentCaptures);Assert.Equal(captureId,value.RecentCaptures[0].CaptureId);
  }

  [Fact] public async Task Saving_session_does_not_delete_captured_images_referenced_by_motion_photos()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var imageId="image-1";var sessions=new SqliteSessionRepository(db);var session=new Session{Id=sessionId,SessionName="Event",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedFiles=new[]{Path.Combine(root,"motion_MP.jpg")},CapturedImageIds=new[]{imageId}};await sessions.SaveAsync(session,CancellationToken.None);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc) VALUES($capture,$session,'final.png','Pending',$now);INSERT INTO CapturePhotos(Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,IsUploaded,FileLength) VALUES($asset,$capture,$image,'motion_MP.jpg','MotionPhoto',1,0,0)";q.Parameters.AddWithValue("$capture",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$session",sessionId.ToString());q.Parameters.AddWithValue("$image",imageId);q.Parameters.AddWithValue("$asset",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));q.ExecuteNonQuery();}
   session.CapturedFiles=new string[0];session.CapturedImageIds=new string[0];await sessions.SaveAsync(session,CancellationToken.None);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT COUNT(*) FROM CapturedImages WHERE Id=$id";q.Parameters.AddWithValue("$id",imageId);Assert.Equal(1,Convert.ToInt32(q.ExecuteScalar()));}
  }

  [Fact] public async Task Retake_replaces_picture_and_motion_as_one_captured_shot()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var repository=new SqliteSessionRepository(db);
   await repository.SaveAsync(new Session{Id=sessionId,SessionName="Event",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=new CapturedShot[0]},CancellationToken.None);
   var oldShot=new CapturedShot{Id="old",Sequence=1,PicturePath=Path.Combine(root,"old.jpg"),MotionPhotoPath=Path.Combine(root,"old_MP.jpg"),CapturedAtUtc=DateTime.UtcNow};
   var replacement=new CapturedShot{Id="new",Sequence=2,PicturePath=Path.Combine(root,"new.jpg"),MotionPhotoPath=Path.Combine(root,"new_MP.jpg"),CapturedAtUtc=DateTime.UtcNow};
   await repository.AddCapturedShotAsync(sessionId,oldShot,CancellationToken.None);await repository.AddCapturedShotAsync(sessionId,replacement,CancellationToken.None);replacement.Sequence=oldShot.Sequence;await repository.ReplaceCapturedShotAsync(sessionId,oldShot.Id,replacement,CancellationToken.None);
   var loaded=await repository.GetAsync(sessionId,CancellationToken.None);var shot=Assert.Single(loaded.CapturedShots);Assert.Equal("new",shot.Id);Assert.Equal(replacement.PicturePath,shot.PicturePath);Assert.Equal(replacement.MotionPhotoPath,shot.MotionPhotoPath);Assert.Equal(1,shot.Sequence);
   using(var c=db.OpenConnection()){using(var q=c.CreateCommand()){q.CommandText="SELECT COUNT(*) FROM CapturedImages WHERE Id='old'";Assert.Equal(0,Convert.ToInt32(q.ExecuteScalar()));}using(var q=c.CreateCommand()){q.CommandText="PRAGMA foreign_key_check";using(var reader=q.ExecuteReader())Assert.False(reader.Read());}}
  }
 }
}
