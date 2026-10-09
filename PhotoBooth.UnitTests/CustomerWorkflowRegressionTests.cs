using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Business.Pipelines;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CustomerWorkflowRegressionTests
    {
        [Fact]
        public async Task Retake_reassigns_a_committed_capture_to_the_original_sequence()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();
                var sessions=new SqliteSessionRepository(database);var attempts=new SqliteCaptureAttemptRepository(database);var assets=new SqliteMediaAssetRepository(database);
                var photoEvent=Event(root);await sessions.SaveAsync(photoEvent,CancellationToken.None);
                var booth=Booth(root,photoEvent.Id);await sessions.SaveAsync(booth,CancellationToken.None);
                var original=await RecordShot(root,booth.Id,1,"original",attempts,assets);await sessions.AddCapturedShotAsync(booth.Id,original,CancellationToken.None);
                var replacement=await RecordShot(root,booth.Id,2,"replacement",attempts,assets);await sessions.AddCapturedShotAsync(booth.Id,replacement,CancellationToken.None);

                replacement.Sequence=original.Sequence;
                await sessions.ReplaceCapturedShotAsync(booth.Id,original.Id,replacement,CancellationToken.None);

                var selected=Assert.Single((await sessions.GetAsync(booth.Id,CancellationToken.None)).CapturedShots);
                Assert.Equal(replacement.Id,selected.Id);Assert.Equal(1,selected.Sequence);
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText="SELECT Sequence,AttemptNumber,IsSelected FROM CaptureAttempts WHERE Id=$id";command.Parameters.AddWithValue("$id",replacement.Id);
                    using(var reader=command.ExecuteReader()){Assert.True(reader.Read());Assert.Equal(1,reader.GetInt32(0));Assert.Equal(2,reader.GetInt32(1));Assert.Equal(1,reader.GetInt32(2));}
                }
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Fact]
        public async Task Deliverables_for_the_same_session_receive_incrementing_revisions()
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);
                var photoEvent=Event(root);await sessions.SaveAsync(photoEvent,CancellationToken.None);var booth=Booth(root,photoEvent.Id);await sessions.SaveAsync(booth,CancellationToken.None);
                var repository=new SqliteCaptureRepository(database);
                await repository.SaveAsync(Deliverable(root,booth.Id,"first"),CancellationToken.None);
                await repository.SaveAsync(Deliverable(root,booth.Id,"second"),CancellationToken.None);
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {command.CommandText="SELECT Revision FROM Deliverables WHERE BoothSessionId=$session ORDER BY Revision";command.Parameters.AddWithValue("$session",booth.Id.ToString());using(var reader=command.ExecuteReader()){Assert.True(reader.Read());Assert.Equal(1,reader.GetInt32(0));Assert.True(reader.Read());Assert.Equal(2,reader.GetInt32(0));Assert.False(reader.Read());}}
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Printing_uses_the_profile_selected_for_the_customer_session_without_requiring_global_default(bool legacyMargins)
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);
                var photoEvent=Event(root);await sessions.SaveAsync(photoEvent,CancellationToken.None);var booth=Booth(root,photoEvent.Id);await sessions.SaveAsync(booth,CancellationToken.None);
                var capture=Deliverable(root,booth.Id,"print-final");await new SqliteCaptureRepository(database).SaveAsync(capture,CancellationToken.None);
                booth.FinalImageId=capture.CompositeImageId;booth.Status=BoothSessionStates.Finalizing;booth.StateVersion++;booth.UpdatedAtUtc=DateTime.UtcNow;await sessions.SaveAsync(booth,CancellationToken.None);
                var profile=new PrinterProfile{Id=Guid.NewGuid(),Name="Session printer",PrinterName="Session printer",PrinterId="session-printer",PaperSize="Postcard",DefaultCopies=1,PrintInColor=true,IsDefault=false,UseDefaultBorder=legacyMargins};
                var profiles=new SqlitePrinterProfileRepository(database);await profiles.SaveAsync(profile,CancellationToken.None);
                var printer=new RecordingPrinter();
                var pipeline=new PrintPipeline(sessions,new SqliteCaptureRepository(database),profiles,printer,null,new SqlitePrintJobRepository(database),new SqliteDurableOutputJobRepository(database));

                await pipeline.ExecuteAsync(booth.Id,profile.Id,1,CancellationToken.None);

                Assert.Equal(1,printer.PrintCount);
                Assert.False(printer.LastJob.UseDefaultBorder);
                Assert.Equal(legacyMargins,(await profiles.GetAsync(profile.Id,CancellationToken.None)).UseDefaultBorder);
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText="SELECT SettingsJson FROM PrintJobs WHERE BoothSessionId=$session";command.Parameters.AddWithValue("$session",booth.Id.ToString());
                    Assert.Contains("\"useDefaultBorder\":false",(string)command.ExecuteScalar());
                }
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        [Theory]
        [InlineData("ok",1)]
        [InlineData("quota",0)]
        [InlineData("submit",0)]
        [InlineData("commit",1)]
        public async Task Event_printing_enforces_server_quota_and_never_reprints_uncertain_submission(string failure,int expectedPrints)
        {
            var root=NewRoot();
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();var sessions=new SqliteSessionRepository(database);
                var photoEvent=Event(root);await sessions.SaveAsync(photoEvent,CancellationToken.None);var booth=Booth(root,photoEvent.Id);booth.RemoteEventId="evt_test";await sessions.SaveAsync(booth,CancellationToken.None);
                var capture=Deliverable(root,booth.Id,"print-final");await new SqliteCaptureRepository(database).SaveAsync(capture,CancellationToken.None);
                booth.FinalImageId=capture.CompositeImageId;booth.Status=BoothSessionStates.Finalizing;booth.StateVersion++;booth.UpdatedAtUtc=DateTime.UtcNow;await sessions.SaveAsync(booth,CancellationToken.None);
                var profile=new PrinterProfile{Id=Guid.NewGuid(),Name="Session printer",PrinterName="Session printer",PrinterId="session-printer",PaperSize="Postcard",DefaultCopies=1,PrintInColor=true};
                var profiles=new SqlitePrinterProfileRepository(database);await profiles.SaveAsync(profile,CancellationToken.None);
                var printer=new RecordingPrinter();var remote=new EventPrinter(failure);
                var pipeline=new PrintPipeline(sessions,new SqliteCaptureRepository(database),profiles,printer,null,new SqlitePrintJobRepository(database),new SqliteDurableOutputJobRepository(database),remote);
                if(failure=="ok"){await pipeline.ExecuteAsync(booth.Id,profile.Id,1,CancellationToken.None);await pipeline.ExecuteAsync(booth.Id,profile.Id,1,CancellationToken.None);}
                else {await Assert.ThrowsAsync<InvalidOperationException>(()=>pipeline.ExecuteAsync(booth.Id,profile.Id,1,CancellationToken.None));await Assert.ThrowsAsync<InvalidOperationException>(()=>pipeline.ExecuteAsync(booth.Id,profile.Id,1,CancellationToken.None));}
                Assert.Equal(expectedPrints,printer.PrintCount);
                Assert.Equal(failure=="quota"?0:1,remote.Submits);
                Assert.Equal("evt_test",(await sessions.GetAsync(booth.Id,CancellationToken.None)).RemoteEventId);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        }

        sealed class EventPrinter:IEventPrintAuthorizationService
        {
            readonly string failure;public int Submits;
            public EventPrinter(string failure){this.failure=failure;}
            public Task<EventPrintReservation> ReserveAsync(Guid captureId,int copies,string key,CancellationToken token)
            {if(failure=="quota")throw new InvalidOperationException("quota exceeded");return Task.FromResult(new EventPrintReservation{EventId="evt_test",Id="reservation_test",State="reserved"});}
            public Task SubmitAsync(EventPrintReservation reservation,CancellationToken token){Submits++;if(failure=="submit")throw new InvalidOperationException("unknown submission");return Task.CompletedTask;}
            public Task CommitAsync(EventPrintReservation reservation,CancellationToken token){if(failure=="commit")throw new InvalidOperationException("lost commit response");return Task.CompletedTask;}
        }

        static async Task<CapturedShot> RecordShot(string root,Guid sessionId,int sequence,string name,SqliteCaptureAttemptRepository attempts,SqliteMediaAssetRepository assets)
        {
            var now=DateTime.UtcNow;var attemptId=Guid.NewGuid().ToString("N");var assetId=Guid.NewGuid().ToString("N");var relative="Captures/"+name+".jpg";Write(root,relative,name);
            await attempts.BeginAsync(new CaptureAttemptRecord{Id=attemptId,SessionId=sessionId,Sequence=sequence,AttemptNumber=1,IntentAtUtc=now},CancellationToken.None);
            await assets.SaveAsync(new MediaAssetRecord{Id=assetId,SessionId=sessionId,CaptureAttemptId=attemptId,Kind=MediaAssetKinds.OriginalPicture,RelativePath=relative,MimeType="image/jpeg",FileLength=name.Length,Status=MediaAssetStates.Ready,RetentionClass=MediaRetentionClasses.Original,CreatedAtUtc=now,UpdatedAtUtc=now},CancellationToken.None);
            await attempts.MarkAcceptedAsync(attemptId,CancellationToken.None);
            return new CapturedShot{Id=attemptId,Sequence=sequence,PictureAssetId=assetId,PicturePath=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)),CapturedAtUtc=now};
        }

        static PhotoCapture Deliverable(string root,Guid sessionId,string name)
        {
            var id=Guid.NewGuid().ToString("N");var assetId=Guid.NewGuid().ToString("N");var relative="Captures/"+name+".png";var path=Write(root,relative,name);
            return new PhotoCapture{Id=id,SessionId=sessionId,CompositeImageId=assetId,CompositePath=path,Status="Ready",CreatedAtUtc=DateTime.UtcNow,Photos=new[]{new CapturePhoto{Id=assetId,CaptureId=id,LocalPath=path,PhotoType=CaptureAssetTypes.Composite,Position=0,MimeType="image/png",FileLength=name.Length,CreatedAtUtc=DateTime.UtcNow,AssetStatus=MediaAssetStates.Ready}}};
        }

        static Session Event(string root)=>new Session{Id=Guid.NewGuid(),Kind=SessionKinds.Event,SessionName="Regression",Status=BoothSessionStates.Active,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=root,CapturedShots=new CapturedShot[0]};
        static Session Booth(string root,Guid eventId){var id=Guid.NewGuid();var output=Path.Combine(root,"Captures",id.ToString("N"));Directory.CreateDirectory(output);return new Session{Id=id,Kind=SessionKinds.Booth,EventId=eventId,Status=BoothSessionStates.Active,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow,OutputDirectory=output,CapturedShots=new CapturedShot[0]};}
        static string Write(string root,string relative,string content){var path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,content);return path;}
        static string NewRoot(){var root=Path.Combine(Path.GetTempPath(),"PhotoBoothCustomerRegression",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}

        sealed class RecordingPrinter:IPrinterService
        {
            public int PrintCount{get;private set;}
            public PrintJob LastJob{get;private set;}
            public Task<System.Collections.Generic.IReadOnlyList<string>> GetPrintersAsync(CancellationToken token)=>Task.FromResult<System.Collections.Generic.IReadOnlyList<string>>(new[]{"Session printer"});
            public Task<System.Collections.Generic.IReadOnlyList<DiscoveredPrinter>> ScanAsync(CancellationToken token)=>Task.FromResult<System.Collections.Generic.IReadOnlyList<DiscoveredPrinter>>(new[]{new DiscoveredPrinter{Id="session-printer",Name="Session printer",IsOnline=true}});
            public async Task<DiscoveredPrinter> ConnectAsync(string printerId,CancellationToken token)=>(await ScanAsync(token))[0];
            public Task<bool> IsConnectedAsync(string printerId,CancellationToken token)=>Task.FromResult(printerId=="session-printer");
            public Task<System.Collections.Generic.IReadOnlyList<PrinterProfile>> GetProfilesAsync(CancellationToken token)=>Task.FromResult<System.Collections.Generic.IReadOnlyList<PrinterProfile>>(new PrinterProfile[0]);
            public Task SaveProfileAsync(PrinterProfile profile,CancellationToken token)=>Task.CompletedTask;
            public Task DeleteProfileAsync(Guid id,CancellationToken token)=>Task.CompletedTask;
            public Task PrintAsync(PrintJob job,CancellationToken token){PrintCount++;LastJob=job;return Task.CompletedTask;}
        }
    }
}
