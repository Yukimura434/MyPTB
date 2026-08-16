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
   var repository=new SqliteCaptureRepository(db);var captureId=Guid.NewGuid().ToString("N");await repository.SaveAsync(new PhotoCapture{Id=captureId,SessionId=sessionId,CompositeImageId="composite-1",CompositePath=compositePath,Status="Pending",CreatedAtUtc=DateTime.UtcNow,Photos=new[]{new CapturePhoto{Id=Guid.NewGuid().ToString("N"),CaptureId=captureId,CapturedImageId="260807000001",LocalPath=imagePath,PhotoType="Original",Position=1,IsUploaded=false},new CapturePhoto{Id=Guid.NewGuid().ToString("N"),CaptureId=captureId,LocalPath=compositePath,PhotoType="Composite",Position=1,IsUploaded=false}}},CancellationToken.None);
   var loaded=await repository.GetAsync(captureId,CancellationToken.None);Assert.NotNull(loaded);Assert.Equal(sessionId,loaded.SessionId);Assert.Equal(2,loaded.Photos.Count);
   var existingSession=await sessions.GetAsync(sessionId,CancellationToken.None);Assert.NotNull(existingSession);Assert.Equal("260807000001",existingSession.CapturedImageIds[0]);
  }
 }
}
