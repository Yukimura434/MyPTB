using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteSessionRepository : ISessionRepository
    {
        private readonly SqliteDatabase _database;
        public SqliteSessionRepository(SqliteDatabase database) { _database = database; }

        public Task<Session> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id,PresetId,StartedAtUtc,CompletedAtUtc,OutputDirectory,CapturedFiles,FinalImagePath,SessionName,SessionNumber,CapturedImageIds,IsDefault,CaptureIndex,FrameIndex,FinalImageId FROM CustomerSessions WHERE Id=$id";
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
            var result=new List<Session>();using(var connection=_database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="SELECT Id,PresetId,StartedAtUtc,CompletedAtUtc,OutputDirectory,CapturedFiles,FinalImagePath,SessionName,SessionNumber,CapturedImageIds,IsDefault,CaptureIndex,FrameIndex,FinalImageId FROM CustomerSessions ORDER BY CASE WHEN SessionName='Base_session' THEN 0 ELSE 1 END, StartedAtUtc DESC";using(var reader=command.ExecuteReader())while(reader.Read())result.Add(Read(reader));}foreach(var session in result)LoadImages(session);return Task.FromResult<IReadOnlyList<Session>>(result);
        }

        public Task SaveAsync(Session session, CancellationToken cancellationToken)
        {
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO CustomerSessions (Id,PresetId,StartedAtUtc,CompletedAtUtc,OutputDirectory,CapturedFiles,FinalImagePath,SessionName,SessionNumber,CapturedImageIds,IsDefault,CaptureIndex,FrameIndex,FinalImageId) VALUES($id,$preset,$start,$end,$path,$files,$final,$name,$number,$imageIds,$default,$captureIndex,$frameIndex,$finalId)
ON CONFLICT(Id) DO UPDATE SET PresetId=excluded.PresetId,CompletedAtUtc=excluded.CompletedAtUtc,OutputDirectory=excluded.OutputDirectory,CapturedFiles=excluded.CapturedFiles,FinalImagePath=excluded.FinalImagePath,SessionName=excluded.SessionName,SessionNumber=excluded.SessionNumber,CapturedImageIds=excluded.CapturedImageIds,IsDefault=excluded.IsDefault,CaptureIndex=MAX(CustomerSessions.CaptureIndex,excluded.CaptureIndex),FrameIndex=MAX(CustomerSessions.FrameIndex,excluded.FrameIndex),FinalImageId=excluded.FinalImageId";
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
                command.ExecuteNonQuery();
            }
            using(var connection=_database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                using(var delete=connection.CreateCommand()){delete.Transaction=transaction;delete.CommandText="DELETE FROM CapturedImages WHERE SessionId=$session";delete.Parameters.AddWithValue("$session",session.Id.ToString());delete.ExecuteNonQuery();}
                var files=(session.CapturedFiles??new string[0]).ToList();var ids=(session.CapturedImageIds??new string[0]).ToList();for(var i=0;i<files.Count;i++)using(var insert=connection.CreateCommand()){insert.Transaction=transaction;insert.CommandText="INSERT INTO CapturedImages (Id,SessionId,Sequence,FilePath,CapturedAtUtc) VALUES($id,$session,$sequence,$path,$captured)";insert.Parameters.AddWithValue("$id",i<ids.Count&&!string.IsNullOrWhiteSpace(ids[i])?ids[i]:Guid.NewGuid().ToString("N"));insert.Parameters.AddWithValue("$session",session.Id.ToString());insert.Parameters.AddWithValue("$sequence",i+1);insert.Parameters.AddWithValue("$path",files[i]);insert.Parameters.AddWithValue("$captured",DateTime.UtcNow.ToString("O"));insert.ExecuteNonQuery();}transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task SetDefaultAsync(Guid id,CancellationToken token){using(var c=_database.OpenConnection())using(var tx=c.BeginTransaction()){using(var clear=c.CreateCommand()){clear.Transaction=tx;clear.CommandText="UPDATE CustomerSessions SET IsDefault=0 WHERE IsDefault=1";clear.ExecuteNonQuery();}using(var set=c.CreateCommand()){set.Transaction=tx;set.CommandText="UPDATE CustomerSessions SET IsDefault=1 WHERE Id=$id";set.Parameters.AddWithValue("$id",id.ToString());if(set.ExecuteNonQuery()!=1)throw new InvalidOperationException("Session not found.");}tx.Commit();}return Task.CompletedTask;}
        public Task<int> GetNextCaptureSequenceAsync(Guid id,CancellationToken token){using(var c=_database.OpenConnection())using(var tx=c.BeginTransaction()){using(var update=c.CreateCommand()){update.Transaction=tx;update.CommandText="UPDATE CustomerSessions SET CaptureIndex=MAX(CaptureIndex,(SELECT COALESCE(MAX(Sequence),0) FROM CapturedImages WHERE SessionId=$id))+1 WHERE Id=$id";update.Parameters.AddWithValue("$id",id.ToString());if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Session not found.");}int value;using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT CaptureIndex FROM CustomerSessions WHERE Id=$id";read.Parameters.AddWithValue("$id",id.ToString());value=Convert.ToInt32(read.ExecuteScalar());}tx.Commit();return Task.FromResult(value);}}
        public Task AddCapturedImageAsync(Guid sessionId,int sequence,string imageId,string filePath,CancellationToken token){using(var c=_database.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="INSERT INTO CapturedImages (Id,SessionId,Sequence,FilePath,CapturedAtUtc) VALUES($id,$session,$sequence,$path,$captured)";q.Parameters.AddWithValue("$id",imageId);q.Parameters.AddWithValue("$session",sessionId.ToString());q.Parameters.AddWithValue("$sequence",sequence);q.Parameters.AddWithValue("$path",filePath);q.Parameters.AddWithValue("$captured",DateTime.UtcNow.ToString("O"));q.ExecuteNonQuery();}return Task.CompletedTask;}
        static Session Read(Microsoft.Data.Sqlite.SqliteDataReader reader)=>new Session { Id=Guid.Parse(reader.GetString(0)),PresetId=reader.IsDBNull(1)?(Guid?)null:Guid.Parse(reader.GetString(1)),StartedAtUtc=DateTime.Parse(reader.GetString(2)).ToUniversalTime(),CompletedAtUtc=reader.IsDBNull(3)?(DateTime?)null:DateTime.Parse(reader.GetString(3)).ToUniversalTime(),OutputDirectory=reader.IsDBNull(4)?null:reader.GetString(4),CapturedFiles=reader.IsDBNull(5)?new string[0]:reader.GetString(5).Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries),FinalImagePath=reader.IsDBNull(6)?null:reader.GetString(6),SessionName=reader.IsDBNull(7)?null:reader.GetString(7),SessionNumber=reader.GetInt32(8),CapturedImageIds=reader.IsDBNull(9)?new string[0]:reader.GetString(9).Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries),IsDefault=reader.GetInt32(10)!=0,CaptureIndex=reader.GetInt32(11),FrameIndex=reader.GetInt32(12),FinalImageId=reader.IsDBNull(13)?null:reader.GetString(13) };
        void LoadImages(Session session){var files=new List<string>();var ids=new List<string>();using(var connection=_database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="SELECT Id,FilePath FROM CapturedImages WHERE SessionId=$session ORDER BY Sequence";command.Parameters.AddWithValue("$session",session.Id.ToString());using(var reader=command.ExecuteReader())while(reader.Read()){ids.Add(reader.GetString(0));files.Add(reader.GetString(1));}}if(files.Count>0){session.CapturedFiles=files;session.CapturedImageIds=ids;}}
    }
}
