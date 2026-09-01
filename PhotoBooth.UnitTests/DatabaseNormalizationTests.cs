using System;using System.Collections.Generic;using System.IO;using System.Linq;using System.Threading;using System.Threading.Tasks;using PhotoBooth.Core.Models;using PhotoBooth.Database;using Xunit;
namespace PhotoBooth.UnitTests
{
 public sealed class DatabaseNormalizationTests
 {
  [Fact] public async Task Event_display_names_are_not_unique_database_keys()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var repository=new SqliteSessionRepository(db);var name="Sự kiện trùng tên";
   await repository.SaveAsync(new Session{Id=Guid.NewGuid(),Kind=SessionKinds.Event,SessionName=name,SessionNumber=1,StartedAtUtc=DateTime.UtcNow,OutputDirectory=Path.Combine(root,"one"),CapturedShots=new CapturedShot[0]},CancellationToken.None);
   await repository.SaveAsync(new Session{Id=Guid.NewGuid(),Kind=SessionKinds.Event,SessionName=name,SessionNumber=2,StartedAtUtc=DateTime.UtcNow,OutputDirectory=Path.Combine(root,"two"),CapturedShots=new CapturedShot[0]},CancellationToken.None);
   db.Initialize();var values=await repository.GetAllAsync(CancellationToken.None);Assert.Equal(2,values.Count(x=>x.SessionName==name));
   using(var connection=db.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="SELECT COUNT(*) FROM SchemaMigrations WHERE Version=12 AND Name='event_names_are_non_unique_labels'";Assert.Equal(1,Convert.ToInt32(command.ExecuteScalar()));}
  }

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
INSERT INTO CapturedImages(Id,SessionId,Sequence,FilePath,CapturedAtUtc,VideoPath) VALUES('image','session',1,'picture.jpg','2026-08-25T00:00:00Z','video.mp4');
INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc,MediaMode) VALUES('capture','session','composite.png','Pending','2026-08-25T00:00:00Z','PictureAndVideo');
INSERT INTO CapturePhotos(Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,FileLength,CreatedAtUtc,AssetStatus) VALUES
('picture','capture','image','picture.jpg','Picture',1,1,'2026-08-25T00:00:00Z','Ready'),
('video','capture','image','video.mp4','Video',1,1,'2026-08-25T00:00:00Z','Ready'),
('composite','capture',NULL,'composite.png','Composite',1,1,'2026-08-25T00:00:00Z','Ready'),
('video-composite','capture',NULL,'composite.mp4','CompositeVideo',1,1,'2026-08-25T00:00:00Z','Ready');";q.Parameters.AddWithValue("$root",root);q.ExecuteNonQuery();}
   db.Initialize();
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT AssetId||':'||SourceAssetId FROM CaptureAssetSources ORDER BY AssetId";var values=new List<string>();using(var reader=q.ExecuteReader())while(reader.Read())values.Add(reader.GetString(0));Assert.Equal(new[]{"composite:picture","video-composite:video"},values);}
  }

  [Fact] public void Reinitialize_preserves_retired_assets_before_replacing_validation_triggers()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText=@"DROP TRIGGER TR_CapturePhotos_Validate_Insert;
DROP TRIGGER TR_CapturePhotos_Validate_Update;
INSERT INTO CustomerSessions(Id,StartedAtUtc,OutputDirectory,SessionName) VALUES('session','2026-08-25T00:00:00Z',$root,'Event');
INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc) VALUES('capture','session','composite.png','Pending','2026-08-25T00:00:00Z');
INSERT INTO CapturePhotos(Id,CaptureId,LocalPath,PhotoType,Position,FileLength,AssetStatus) VALUES('retired','capture','retired.bin','RetiredAsset',1,1,'Ready');
CREATE TRIGGER TR_CapturePhotos_Validate_Update BEFORE UPDATE ON CapturePhotos BEGIN SELECT CASE WHEN NEW.PhotoType NOT IN ('Picture','Video','CompositeVideo','Composite','Gif','ShareArchive') THEN RAISE(ABORT,'Invalid capture asset type') END; END;";q.Parameters.AddWithValue("$root",root);q.ExecuteNonQuery();}
   db.Initialize();
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT PhotoType FROM CapturePhotos WHERE Id='retired'";Assert.Equal("RetiredAsset",Convert.ToString(q.ExecuteScalar()));}
  }

  [Fact] public async Task Data_statistics_are_aggregated_only_from_sqlite()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var captureId=Guid.NewGuid().ToString("N");var imageId="image-1";var assetId=Guid.NewGuid().ToString("N");
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="INSERT INTO CustomerSessions(Id,StartedAtUtc,OutputDirectory,Kind,Status) VALUES($session,$now,$root,'Booth','Completed');INSERT INTO CapturedImages(Id,SessionId,Sequence,FilePath,CapturedAtUtc) VALUES($image,$session,1,'video.mp4',$now);INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc) VALUES($capture,$session,'final.png','Pending',$now);INSERT INTO CapturePhotos(Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,IsUploaded,MimeType,FileLength,CreatedAtUtc,AssetStatus) VALUES($asset,$capture,$image,'video.mp4','Video',1,0,'image/jpeg',1024,$now,'Ready');INSERT INTO MediaAssets(Id,SessionId,Kind,RelativePath,MimeType,FileLength,Status,RetentionClass,CreatedAtUtc,UpdatedAtUtc) VALUES($asset,$session,'OriginalVideo','Captures/video.mp4','video/mp4',1024,'Ready','Original',$now,$now);INSERT INTO PrintJobs(Id,SessionId,CaptureId,PrinterName,Status,PrintedAtUtc) VALUES($print,$session,$capture,'Printer','Success',$now);";q.Parameters.AddWithValue("$session",sessionId.ToString());q.Parameters.AddWithValue("$capture",captureId);q.Parameters.AddWithValue("$image",imageId);q.Parameters.AddWithValue("$asset",assetId);q.Parameters.AddWithValue("$print",Guid.NewGuid().ToString());q.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));q.Parameters.AddWithValue("$root",root);q.ExecuteNonQuery();}
   var value=await new SqliteStatsRepository(db).GetDataStatisticsAsync(CancellationToken.None);Assert.Equal(1,value.SessionCount);Assert.Equal(1,value.CaptureCount);Assert.Equal(1,value.VideoCount);Assert.Equal(1,value.SuccessfulPrintCount);Assert.Equal(1024,value.TotalAssetBytes);Assert.Single(value.RecentCaptures);Assert.Equal(captureId,value.RecentCaptures[0].CaptureId);
  }

  [Fact] public async Task Capture_library_filters_by_event_date_and_session_and_counts_extra_prints()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();
   var eventId=Guid.NewGuid();var firstSession=Guid.NewGuid();var secondSession=Guid.NewGuid();var firstCapture=Guid.NewGuid().ToString("N");var secondCapture=Guid.NewGuid().ToString("N");var firstShot=Guid.NewGuid().ToString("N");var secondShot=Guid.NewGuid().ToString("N");var firstAsset=Guid.NewGuid().ToString("N");var secondAsset=Guid.NewGuid().ToString("N");var firstTime=new DateTime(2026,8,12,3,30,0,DateTimeKind.Utc);var secondTime=firstTime.AddDays(1);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand())
   {
    q.CommandText=@"INSERT INTO CustomerSessions(Id,SessionName,StartedAtUtc,OutputDirectory,Kind,Status,IsDefault) VALUES($event,$eventName,$first,$root,'Event','Active',1);
INSERT INTO CustomerSessions(Id,StartedAtUtc,OutputDirectory,Kind,EventId,Status,DisplayCode) VALUES($session1,$first,$root,'Booth',$event,'Completed','S-001'),($session2,$second,$root,'Booth',$event,'Completed','S-002');
INSERT INTO CapturedImages(Id,SessionId,Sequence,FilePath,CapturedAtUtc) VALUES($shot1,$session1,1,$picture1,$first),($shot2,$session2,1,$picture2,$second);
INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc) VALUES($capture1,$session1,$picture1,'Completed',$first),($capture2,$session2,$picture2,'Completed',$second);
INSERT INTO CapturePhotos(Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,IsUploaded,MimeType,FileLength,CreatedAtUtc,AssetStatus) VALUES($asset1,$capture1,$shot1,$picture1,'Picture',1,0,'image/jpeg',10,$first,'Ready'),($asset2,$capture2,$shot2,$picture2,'Picture',1,0,'image/jpeg',20,$second,'Ready');
INSERT INTO MediaAssets(Id,SessionId,Kind,RelativePath,MimeType,FileLength,Status,RetentionClass,CreatedAtUtc,UpdatedAtUtc) VALUES($asset1,$session1,'OriginalPicture','Captures/first.jpg','image/jpeg',10,'Ready','Original',$first,$first),($asset2,$session2,'OriginalPicture','Captures/second.jpg','image/jpeg',20,'Ready','Original',$second,$second);
INSERT INTO PrintJobs(Id,SessionId,CaptureId,PrinterName,Copies,Status,PrintedAtUtc) VALUES($print1,$session1,$capture1,'Printer',3,'Success',$first),($printExtra,$session1,$capture1,'Printer',1,'Success',$first),($print2,$session2,$capture2,'Printer',4,'Failed',$second);";
    q.Parameters.AddWithValue("$event",eventId.ToString());q.Parameters.AddWithValue("$eventName","Summer Festival");q.Parameters.AddWithValue("$session1",firstSession.ToString());q.Parameters.AddWithValue("$session2",secondSession.ToString());q.Parameters.AddWithValue("$capture1",firstCapture);q.Parameters.AddWithValue("$capture2",secondCapture);q.Parameters.AddWithValue("$shot1",firstShot);q.Parameters.AddWithValue("$shot2",secondShot);q.Parameters.AddWithValue("$asset1",firstAsset);q.Parameters.AddWithValue("$asset2",secondAsset);q.Parameters.AddWithValue("$picture1",Path.Combine(root,"first.jpg"));q.Parameters.AddWithValue("$picture2",Path.Combine(root,"second.jpg"));q.Parameters.AddWithValue("$first",firstTime.ToString("O"));q.Parameters.AddWithValue("$second",secondTime.ToString("O"));q.Parameters.AddWithValue("$root",root);q.Parameters.AddWithValue("$print1",Guid.NewGuid().ToString());q.Parameters.AddWithValue("$printExtra",Guid.NewGuid().ToString());q.Parameters.AddWithValue("$print2",Guid.NewGuid().ToString());q.ExecuteNonQuery();
   }
   var repository=new SqliteStatsRepository(db);
   var byEvent=await repository.SearchCaptureLibraryAsync(new CaptureLibraryFilter{Mode=CaptureLibraryFilterModes.Event,Query="festival"},CancellationToken.None);Assert.Equal(2,byEvent.CaptureCount);Assert.Equal(4,byEvent.PrintedPhotoCount);Assert.Equal(3,byEvent.ExtraPrintCount);Assert.False(byEvent.HasRevenueData);Assert.Equal("Captures/second.jpg",byEvent.Captures[0].ThumbnailManagedRelativePath);
   var byDate=await repository.SearchCaptureLibraryAsync(new CaptureLibraryFilter{Mode=CaptureLibraryFilterModes.Date,FromUtc=secondTime.Date,ToUtc=secondTime.Date.AddDays(1)},CancellationToken.None);Assert.Equal(1,byDate.CaptureCount);Assert.Equal(secondCapture,Assert.Single(byDate.Captures).CaptureId);
   var bySession=await repository.SearchCaptureLibraryAsync(new CaptureLibraryFilter{Mode=CaptureLibraryFilterModes.Session,Query=firstSession.ToString("N").Substring(0,12)},CancellationToken.None);Assert.Equal(firstCapture,Assert.Single(bySession.Captures).CaptureId);
   Assert.Contains("Summer Festival",await repository.GetEventSuggestionsAsync(CancellationToken.None));var media=await repository.GetCaptureMediaAsync(firstCapture,CancellationToken.None);Assert.Equal(firstAsset,Assert.Single(media).AssetId);Assert.Equal("Captures/first.jpg",media[0].ManagedRelativePath);
  }

  [Fact] public async Task Saving_session_does_not_delete_captured_images_referenced_by_videos()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var imageId="image-1";var sessions=new SqliteSessionRepository(db);var session=new Session{Id=sessionId,SessionName="Event",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedFiles=new[]{Path.Combine(root,"video.mp4")},CapturedImageIds=new[]{imageId}};await sessions.SaveAsync(session,CancellationToken.None);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="INSERT INTO Captures(Id,SessionId,CompositePath,Status,CreatedAtUtc) VALUES($capture,$session,'final.png','Pending',$now);INSERT INTO CapturePhotos(Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,IsUploaded,FileLength) VALUES($asset,$capture,$image,'video.mp4','Video',1,0,0)";q.Parameters.AddWithValue("$capture",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$session",sessionId.ToString());q.Parameters.AddWithValue("$image",imageId);q.Parameters.AddWithValue("$asset",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));q.ExecuteNonQuery();}
   session.CapturedFiles=new string[0];session.CapturedImageIds=new string[0];await sessions.SaveAsync(session,CancellationToken.None);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT COUNT(*) FROM CapturedImages WHERE Id=$id";q.Parameters.AddWithValue("$id",imageId);Assert.Equal(1,Convert.ToInt32(q.ExecuteScalar()));}
  }

  [Fact] public async Task Retake_replaces_picture_and_video_as_one_captured_shot()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var sessionId=Guid.NewGuid();var repository=new SqliteSessionRepository(db);
   await repository.SaveAsync(new Session{Id=sessionId,SessionName="Event",StartedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=new CapturedShot[0]},CancellationToken.None);
   var oldShot=new CapturedShot{Id="old",Sequence=1,PicturePath=Path.Combine(root,"old.jpg"),VideoPath=Path.Combine(root,"old.mp4"),CapturedAtUtc=DateTime.UtcNow};
   var replacement=new CapturedShot{Id="new",Sequence=2,PicturePath=Path.Combine(root,"new.jpg"),VideoPath=Path.Combine(root,"new.mp4"),CapturedAtUtc=DateTime.UtcNow};
   await repository.AddCapturedShotAsync(sessionId,oldShot,CancellationToken.None);await repository.AddCapturedShotAsync(sessionId,replacement,CancellationToken.None);replacement.Sequence=oldShot.Sequence;await repository.ReplaceCapturedShotAsync(sessionId,oldShot.Id,replacement,CancellationToken.None);
   var loaded=await repository.GetAsync(sessionId,CancellationToken.None);var shot=Assert.Single(loaded.CapturedShots);Assert.Equal("new",shot.Id);Assert.Equal(replacement.PicturePath,shot.PicturePath);Assert.Equal(replacement.VideoPath,shot.VideoPath);Assert.Equal(1,shot.Sequence);
   using(var c=db.OpenConnection()){using(var q=c.CreateCommand()){q.CommandText="SELECT COUNT(*) FROM CapturedImages WHERE Id='old'";Assert.Equal(0,Convert.ToInt32(q.ExecuteScalar()));}using(var q=c.CreateCommand()){q.CommandText="PRAGMA foreign_key_check";using(var reader=q.ExecuteReader())Assert.False(reader.Read());}}
  }

  [Fact] public async Task Settings_save_rolls_back_workflow_when_production_write_fails()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var repository=new SqliteSettingsRepository(db);
   var original=new Settings{Culture="en",Theme="original"};await repository.SaveAsync(original,CancellationToken.None);
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="CREATE TRIGGER FailProductionSettings BEFORE INSERT ON ProductionSettings WHEN NEW.Theme='force-failure' BEGIN SELECT RAISE(ABORT,'forced settings failure'); END;";q.ExecuteNonQuery();}
   var changed=await repository.GetAsync(CancellationToken.None);changed.Culture="vi";changed.Theme="force-failure";
   await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>repository.SaveAsync(changed,CancellationToken.None));
   var loaded=await repository.GetAsync(CancellationToken.None);Assert.Equal("en",loaded.Culture);Assert.Equal("original",loaded.Theme);
  }

  [Fact] public async Task Bulk_loading_preserves_frame_slots_and_session_shot_order()
  {
   var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var frames=new SqliteFrameRepository(db);var sessions=new SqliteSessionRepository(db);
   for(var f=0;f<3;f++){var frame=new Frame{Id=Guid.NewGuid(),Name="Frame "+f,CreatedAtUtc=DateTime.UtcNow.AddMinutes(f),Slots=new[]{new FrameSlot{Id=Guid.NewGuid(),Index=1,X=10,Y=20,Width=30,Height=40},new FrameSlot{Id=Guid.NewGuid(),Index=0,X=1,Y=2,Width=3,Height=4}}};await frames.SaveAsync(frame,CancellationToken.None);}
   for(var s=0;s<3;s++){var session=new Session{Id=Guid.NewGuid(),SessionName="Event "+s,StartedAtUtc=DateTime.UtcNow.AddMinutes(s),OutputDirectory=root,CapturedShots=new[]{new CapturedShot{Id=Guid.NewGuid().ToString("N"),Sequence=2,PicturePath="second-"+s+".jpg",CapturedAtUtc=DateTime.UtcNow},new CapturedShot{Id=Guid.NewGuid().ToString("N"),Sequence=1,PicturePath="first-"+s+".jpg",CapturedAtUtc=DateTime.UtcNow}}};await sessions.SaveAsync(session,CancellationToken.None);}
   var loadedFrames=await frames.GetAllAsync(CancellationToken.None);Assert.Equal(3,loadedFrames.Count);Assert.All(loadedFrames,frame=>Assert.Equal(new[]{0,1},frame.Slots.Select(x=>x.Index).ToArray()));
   var loadedSessions=await sessions.GetAllAsync(CancellationToken.None);Assert.Equal(3,loadedSessions.Count);Assert.All(loadedSessions,session=>{Assert.Equal(new[]{1,2},session.CapturedShots.Select(x=>x.Sequence).ToArray());Assert.StartsWith("first-",session.CapturedFiles[0]);});
  }
 }
}
