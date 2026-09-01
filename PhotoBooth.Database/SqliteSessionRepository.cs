using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteSessionRepository : ISessionRepository, ILocalSessionRecoveryRepository
    {
        private readonly SqliteDatabase _database;
        public SqliteSessionRepository(SqliteDatabase database) { _database = database; }

        public Task<Session> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = SelectColumns + " FROM CustomerSessions WHERE Id=$id";
                command.Parameters.AddWithValue("$id", id.ToString());
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<Session>(null);
                    var session=Read(reader);LoadImages(session);return Task.FromResult(session);
                }
            }
        }

        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();var result=new List<Session>();using(var connection=_database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText=SelectColumns+" FROM CustomerSessions WHERE Kind='Event' ORDER BY CASE WHEN SessionName IN ('Sự kiện mặc định','Base_session') THEN 0 ELSE 1 END, StartedAtUtc DESC";using(var reader=command.ExecuteReader())while(reader.Read()){cancellationToken.ThrowIfCancellationRequested();result.Add(Read(reader));}}LoadImages(result,cancellationToken);return Task.FromResult<IReadOnlyList<Session>>(result);
        }

        public Task SaveAsync(Session session, CancellationToken cancellationToken)
        {
            EnsureCapturedShots(session);
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO CustomerSessions (Id,PresetId,StartedAtUtc,CompletedAtUtc,OutputDirectory,CapturedFiles,FinalImagePath,SessionName,SessionNumber,CapturedImageIds,IsDefault,CaptureIndex,FrameIndex,FinalImageId,Kind,EventId,Status,StateVersion,TerminalReason,DisplayCode,UpdatedAtUtc) VALUES($id,$preset,$start,$end,$path,$files,$final,$name,$number,$imageIds,$default,$captureIndex,$frameIndex,$finalId,$kind,$eventId,$status,$stateVersion,$terminalReason,$displayCode,$updated)
ON CONFLICT(Id) DO UPDATE SET PresetId=excluded.PresetId,CompletedAtUtc=excluded.CompletedAtUtc,OutputDirectory=excluded.OutputDirectory,CapturedFiles=excluded.CapturedFiles,FinalImagePath=excluded.FinalImagePath,SessionName=excluded.SessionName,SessionNumber=excluded.SessionNumber,CapturedImageIds=excluded.CapturedImageIds,IsDefault=excluded.IsDefault,CaptureIndex=MAX(CustomerSessions.CaptureIndex,excluded.CaptureIndex),FrameIndex=MAX(CustomerSessions.FrameIndex,excluded.FrameIndex),FinalImageId=excluded.FinalImageId,Kind=excluded.Kind,EventId=excluded.EventId,Status=excluded.Status,StateVersion=MAX(CustomerSessions.StateVersion,excluded.StateVersion),TerminalReason=excluded.TerminalReason,DisplayCode=excluded.DisplayCode,UpdatedAtUtc=excluded.UpdatedAtUtc";
                command.Parameters.AddWithValue("$id", session.Id.ToString());
                command.Parameters.AddWithValue("$start", session.StartedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$end", session.CompletedAtUtc.HasValue ? (object)session.CompletedAtUtc.Value.ToString("O") : DBNull.Value);
                command.Parameters.AddWithValue("$path", (object)session.OutputDirectory ?? DBNull.Value);
                command.Parameters.AddWithValue("$preset", session.PresetId.HasValue?(object)session.PresetId.Value.ToString():DBNull.Value);
                command.Parameters.AddWithValue("$files", string.Join("|",session.CapturedFiles??new string[0]));
                command.Parameters.AddWithValue("$final", (object)session.FinalImagePath??DBNull.Value);
                command.Parameters.AddWithValue("$name", (object)session.SessionName??DBNull.Value);
                command.Parameters.AddWithValue("$number", session.SessionNumber);
                command.Parameters.AddWithValue("$imageIds", string.Join("|",session.CapturedImageIds??new string[0]));
                command.Parameters.AddWithValue("$default",session.IsDefault?1:0);
                command.Parameters.AddWithValue("$captureIndex",session.CaptureIndex);
                command.Parameters.AddWithValue("$frameIndex",session.FrameIndex);
                command.Parameters.AddWithValue("$finalId",(object)session.FinalImageId??DBNull.Value);
                command.Parameters.AddWithValue("$kind",string.IsNullOrWhiteSpace(session.Kind)?SessionKinds.Event:session.Kind);
                command.Parameters.AddWithValue("$eventId",session.EventId.HasValue?(object)session.EventId.Value.ToString():DBNull.Value);
                command.Parameters.AddWithValue("$status",string.IsNullOrWhiteSpace(session.Status)?BoothSessionStates.Active:session.Status);
                command.Parameters.AddWithValue("$stateVersion",session.StateVersion);
                command.Parameters.AddWithValue("$terminalReason",(object)session.TerminalReason??DBNull.Value);
                command.Parameters.AddWithValue("$displayCode",(object)session.DisplayCode??DBNull.Value);
                var updated=session.UpdatedAtUtc==default(DateTime)?DateTime.UtcNow:session.UpdatedAtUtc.ToUniversalTime();
                command.Parameters.AddWithValue("$updated",updated.ToString("O"));
                command.ExecuteNonQuery();
            }
            using(var connection=_database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                // CapturedImages is immutable capture history once CapturePhotos references it.
                // Replacing the whole collection would invoke ON DELETE SET NULL and violate
                // the Video integrity trigger. Save only adds or refreshes known rows.
                foreach(var shot in session.CapturedShots??new CapturedShot[0])using(var insert=connection.CreateCommand()){insert.Transaction=transaction;insert.CommandText="INSERT INTO CapturedImages (Id,SessionId,Sequence,FilePath,VideoPath,CapturedAtUtc,PictureAssetId,VideoAssetId) VALUES($id,$session,$sequence,$path,$video,$captured,$pictureAsset,$videoAsset) ON CONFLICT(Id) DO UPDATE SET Sequence=excluded.Sequence,FilePath=excluded.FilePath,VideoPath=excluded.VideoPath,PictureAssetId=excluded.PictureAssetId,VideoAssetId=excluded.VideoAssetId WHERE CapturedImages.SessionId=excluded.SessionId";insert.Parameters.AddWithValue("$id",shot.Id);insert.Parameters.AddWithValue("$session",session.Id.ToString());insert.Parameters.AddWithValue("$sequence",shot.Sequence);insert.Parameters.AddWithValue("$path",shot.PicturePath);insert.Parameters.AddWithValue("$video",string.IsNullOrWhiteSpace(shot.VideoPath)?(object)DBNull.Value:shot.VideoPath);insert.Parameters.AddWithValue("$captured",shot.CapturedAtUtc.ToString("O"));insert.Parameters.AddWithValue("$pictureAsset",string.IsNullOrWhiteSpace(shot.PictureAssetId)?(object)DBNull.Value:shot.PictureAssetId);insert.Parameters.AddWithValue("$videoAsset",string.IsNullOrWhiteSpace(shot.VideoAssetId)?(object)DBNull.Value:shot.VideoAssetId);insert.ExecuteNonQuery();}transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task SetDefaultAsync(Guid id,CancellationToken token){using(var c=_database.OpenConnection())using(var tx=c.BeginTransaction()){using(var clear=c.CreateCommand()){clear.Transaction=tx;clear.CommandText="UPDATE CustomerSessions SET IsDefault=0 WHERE IsDefault=1 AND Kind='Event'";clear.ExecuteNonQuery();}using(var set=c.CreateCommand()){set.Transaction=tx;set.CommandText="UPDATE CustomerSessions SET IsDefault=1 WHERE Id=$id AND Kind='Event'";set.Parameters.AddWithValue("$id",id.ToString());if(set.ExecuteNonQuery()!=1)throw new InvalidOperationException("Event not found.");}tx.Commit();}return Task.CompletedTask;}
        public Task<int> GetNextCaptureSequenceAsync(Guid id,CancellationToken token){using(var c=_database.OpenConnection())using(var tx=c.BeginTransaction()){using(var update=c.CreateCommand()){update.Transaction=tx;update.CommandText="UPDATE CustomerSessions SET CaptureIndex=MAX(CaptureIndex,(SELECT COALESCE(MAX(Sequence),0) FROM CapturedImages WHERE SessionId=$id))+1 WHERE Id=$id AND Kind='Booth'";update.Parameters.AddWithValue("$id",id.ToString());if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Booth session not found.");}int value;using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT CaptureIndex FROM CustomerSessions WHERE Id=$id";read.Parameters.AddWithValue("$id",id.ToString());value=Convert.ToInt32(read.ExecuteScalar());}tx.Commit();return Task.FromResult(value);}}
        public Task<IReadOnlyList<Session>> GetActiveBoothSessionsAsync(CancellationToken token){var values=new List<Session>();using(var c=_database.OpenConnection())using(var q=c.CreateCommand()){q.CommandText=SelectColumns+" FROM CustomerSessions WHERE Kind='Booth' AND Status IN ('Active','Finalizing') ORDER BY StartedAtUtc";using(var r=q.ExecuteReader())while(r.Read()){token.ThrowIfCancellationRequested();values.Add(Read(r));}}foreach(var value in values)LoadImages(value);return Task.FromResult<IReadOnlyList<Session>>(values);}
        public Task MarkRecoveredFailedAsync(Guid sessionId,string reason,CancellationToken token){token.ThrowIfCancellationRequested();using(var c=_database.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="UPDATE CustomerSessions SET Status='Failed',CompletedAtUtc=$now,UpdatedAtUtc=$now,TerminalReason=$reason,StateVersion=StateVersion+1 WHERE Id=$id AND Kind='Booth' AND Status IN ('Active','Finalizing')";q.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));q.Parameters.AddWithValue("$reason",reason);q.Parameters.AddWithValue("$id",sessionId.ToString());q.ExecuteNonQuery();}return Task.CompletedTask;}
        public Task AddCapturedShotAsync(Guid sessionId,CapturedShot shot,CancellationToken token)=>AddCapturedShotsAsync(sessionId,new[]{shot},token);
        public Task AddCapturedShotsAsync(Guid sessionId,IReadOnlyList<CapturedShot> shots,CancellationToken token){if(shots==null)throw new ArgumentNullException(nameof(shots));using(var c=_database.OpenConnection())using(var tx=c.BeginTransaction()){foreach(var shot in shots){token.ThrowIfCancellationRequested();using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO CapturedImages (Id,SessionId,Sequence,FilePath,VideoPath,CapturedAtUtc,PictureAssetId,VideoAssetId) VALUES($id,$session,$sequence,$path,$video,$captured,$pictureAsset,$videoAsset)";BindShot(q,sessionId,shot);q.ExecuteNonQuery();}}tx.Commit();}return Task.CompletedTask;}
        public Task ReplaceCapturedShotAsync(Guid sessionId,string previousShotId,CapturedShot replacement,CancellationToken token)=>ReplaceCapturedShotsAsync(sessionId,new Dictionary<string,CapturedShot>{{previousShotId,replacement}},token);
        public Task ReplaceCapturedShotsAsync(Guid sessionId,IReadOnlyDictionary<string,CapturedShot> replacements,CancellationToken token){if(replacements==null)throw new ArgumentNullException(nameof(replacements));using(var c=_database.OpenConnection())using(var tx=c.BeginTransaction()){foreach(var replacement in replacements){token.ThrowIfCancellationRequested();using(var remove=c.CreateCommand()){remove.Transaction=tx;remove.CommandText="DELETE FROM CapturedImages WHERE SessionId=$session AND Id=$replacement AND Id<>$previous";remove.Parameters.AddWithValue("$session",sessionId.ToString());remove.Parameters.AddWithValue("$replacement",replacement.Value.Id);remove.Parameters.AddWithValue("$previous",replacement.Key);remove.ExecuteNonQuery();}using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="UPDATE CapturedImages SET Id=$id,Sequence=$sequence,FilePath=$path,VideoPath=$video,CapturedAtUtc=$captured,PictureAssetId=$pictureAsset,VideoAssetId=$videoAsset WHERE SessionId=$session AND Id=$previous";BindShot(q,sessionId,replacement.Value);q.Parameters.AddWithValue("$previous",replacement.Key);if(q.ExecuteNonQuery()!=1)throw new InvalidOperationException("Captured shot not found.");}}tx.Commit();}return Task.CompletedTask;}
        const string SelectColumns="SELECT Id,PresetId,StartedAtUtc,CompletedAtUtc,OutputDirectory,CapturedFiles,FinalImagePath,SessionName,SessionNumber,CapturedImageIds,IsDefault,CaptureIndex,FrameIndex,FinalImageId,Kind,EventId,Status,StateVersion,TerminalReason,DisplayCode,UpdatedAtUtc";
        static Session Read(Microsoft.Data.Sqlite.SqliteDataReader reader)=>new Session { Id=Guid.Parse(reader.GetString(0)),PresetId=reader.IsDBNull(1)?(Guid?)null:Guid.Parse(reader.GetString(1)),StartedAtUtc=DateTime.Parse(reader.GetString(2)).ToUniversalTime(),CompletedAtUtc=reader.IsDBNull(3)?(DateTime?)null:DateTime.Parse(reader.GetString(3)).ToUniversalTime(),OutputDirectory=reader.IsDBNull(4)?null:reader.GetString(4),CapturedFiles=reader.IsDBNull(5)?new string[0]:reader.GetString(5).Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries),FinalImagePath=reader.IsDBNull(6)?null:reader.GetString(6),SessionName=reader.IsDBNull(7)?null:reader.GetString(7),SessionNumber=reader.GetInt32(8),CapturedImageIds=reader.IsDBNull(9)?new string[0]:reader.GetString(9).Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries),IsDefault=reader.GetInt32(10)!=0,CaptureIndex=reader.GetInt32(11),FrameIndex=reader.GetInt32(12),FinalImageId=reader.IsDBNull(13)?null:reader.GetString(13),Kind=reader.IsDBNull(14)?SessionKinds.Event:reader.GetString(14),EventId=reader.IsDBNull(15)?(Guid?)null:Guid.Parse(reader.GetString(15)),Status=reader.IsDBNull(16)?BoothSessionStates.Active:reader.GetString(16),StateVersion=reader.GetInt64(17),TerminalReason=reader.IsDBNull(18)?null:reader.GetString(18),DisplayCode=reader.IsDBNull(19)?null:reader.GetString(19),UpdatedAtUtc=reader.IsDBNull(20)?DateTime.MinValue:DateTime.Parse(reader.GetString(20)).ToUniversalTime() };
        void LoadImages(Session session){var shots=new List<CapturedShot>();using(var connection=_database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="SELECT Id,Sequence,FilePath,VideoPath,CapturedAtUtc,PictureAssetId,VideoAssetId FROM CapturedImages WHERE SessionId=$session ORDER BY Sequence";command.Parameters.AddWithValue("$session",session.Id.ToString());using(var reader=command.ExecuteReader())while(reader.Read())shots.Add(new CapturedShot{Id=reader.GetString(0),Sequence=reader.GetInt32(1),PicturePath=reader.GetString(2),VideoPath=reader.IsDBNull(3)?null:reader.GetString(3),CapturedAtUtc=DateTime.Parse(reader.GetString(4)).ToUniversalTime(),PictureAssetId=reader.IsDBNull(5)?null:reader.GetString(5),VideoAssetId=reader.IsDBNull(6)?null:reader.GetString(6)});}session.CapturedShots=shots;session.CapturedFiles=shots.Select(x=>x.PicturePath).ToList();session.CapturedVideoFiles=shots.Where(x=>x.HasVideo).Select(x=>x.VideoPath).ToList();session.CapturedImageIds=shots.Select(x=>x.Id).ToList();}
        void LoadImages(IReadOnlyList<Session> sessions,CancellationToken token){var byId=sessions.ToDictionary(x=>x.Id,x=>new List<CapturedShot>());using(var connection=_database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="SELECT SessionId,Id,Sequence,FilePath,VideoPath,CapturedAtUtc,PictureAssetId,VideoAssetId FROM CapturedImages WHERE SessionId IN (SELECT Id FROM CustomerSessions WHERE Kind='Event') ORDER BY SessionId,Sequence";using(var reader=command.ExecuteReader())while(reader.Read()){token.ThrowIfCancellationRequested();List<CapturedShot> shots;if(!Guid.TryParse(reader.GetString(0),out var sessionId)||!byId.TryGetValue(sessionId,out shots))continue;shots.Add(new CapturedShot{Id=reader.GetString(1),Sequence=reader.GetInt32(2),PicturePath=reader.GetString(3),VideoPath=reader.IsDBNull(4)?null:reader.GetString(4),CapturedAtUtc=DateTime.Parse(reader.GetString(5)).ToUniversalTime(),PictureAssetId=reader.IsDBNull(6)?null:reader.GetString(6),VideoAssetId=reader.IsDBNull(7)?null:reader.GetString(7)});}}foreach(var session in sessions)ProjectImages(session,byId[session.Id]);}
        static void ProjectImages(Session session,IReadOnlyList<CapturedShot> shots){session.CapturedShots=shots;session.CapturedFiles=shots.Select(x=>x.PicturePath).ToList();session.CapturedVideoFiles=shots.Where(x=>x.HasVideo).Select(x=>x.VideoPath).ToList();session.CapturedImageIds=shots.Select(x=>x.Id).ToList();}
        static void EnsureCapturedShots(Session session){if(session.CapturedShots!=null)return;var files=(session.CapturedFiles??new string[0]).ToList();var ids=(session.CapturedImageIds??new string[0]).ToList();var videos=(session.CapturedVideoFiles??new string[0]).ToList();var shots=new List<CapturedShot>();for(var i=0;i<files.Count;i++)shots.Add(new CapturedShot{Id=i<ids.Count&&!string.IsNullOrWhiteSpace(ids[i])?ids[i]:Guid.NewGuid().ToString("N"),Sequence=i+1,PicturePath=files[i],VideoPath=i<videos.Count?videos[i]:null,CapturedAtUtc=session.StartedAtUtc==default(DateTime)?DateTime.UtcNow:session.StartedAtUtc});session.CapturedShots=shots;}
        static void BindShot(Microsoft.Data.Sqlite.SqliteCommand q,Guid sessionId,CapturedShot shot){if(shot==null||string.IsNullOrWhiteSpace(shot.Id)||string.IsNullOrWhiteSpace(shot.PicturePath))throw new ArgumentException("Captured shot identity and picture are required.");q.Parameters.AddWithValue("$id",shot.Id);q.Parameters.AddWithValue("$session",sessionId.ToString());q.Parameters.AddWithValue("$sequence",shot.Sequence);q.Parameters.AddWithValue("$path",shot.PicturePath);q.Parameters.AddWithValue("$video",string.IsNullOrWhiteSpace(shot.VideoPath)?(object)DBNull.Value:shot.VideoPath);q.Parameters.AddWithValue("$captured",shot.CapturedAtUtc.ToString("O"));q.Parameters.AddWithValue("$pictureAsset",string.IsNullOrWhiteSpace(shot.PictureAssetId)?(object)DBNull.Value:shot.PictureAssetId);q.Parameters.AddWithValue("$videoAsset",string.IsNullOrWhiteSpace(shot.VideoAssetId)?(object)DBNull.Value:shot.VideoAssetId);}
    }
}
