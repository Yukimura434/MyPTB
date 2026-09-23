using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    /// <summary>Maps the existing application contracts to the canonical v2 Event/BoothSession tables.</summary>
    public sealed class SqliteSessionRepository : ISessionRepository, ILocalSessionRecoveryRepository
    {
        readonly SqliteDatabase database;
        public SqliteSessionRepository(SqliteDatabase value){database=value;}

        public Task<Session> GetAsync(Guid id,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())
            {
                var value=ReadEvent(connection,id)??ReadBoothSession(connection,id);
                if(value!=null&&value.IsBoothSession)LoadShots(connection,value);
                else if(value!=null)ProjectShots(value,new CapturedShot[0]);
                return Task.FromResult(value);
            }
        }

        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken token)
        {
            var result=new List<Session>();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT Id,PresetId,Name,OutputDirectory,IsDefault,SessionNumber,CreatedAtUtc,UpdatedAtUtc,RemoteEventId FROM Events ORDER BY CASE WHEN IsDefault=1 THEN 0 ELSE 1 END,CreatedAtUtc DESC";
                using(var reader=command.ExecuteReader())while(reader.Read()){token.ThrowIfCancellationRequested();result.Add(ReadEvent(reader));}
            }
            return Task.FromResult<IReadOnlyList<Session>>(result);
        }

        public Task SaveAsync(Session session,CancellationToken token)
        {
            if(session==null||session.Id==Guid.Empty)throw new ArgumentException("Session identity is required.");
            token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                if(session.IsBoothSession)SaveBoothSession(connection,transaction,session);
                else SaveEvent(connection,transaction,session);
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task SetDefaultAsync(Guid id,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                using(var clear=connection.CreateCommand()){clear.Transaction=transaction;clear.CommandText="UPDATE Events SET IsDefault=0 WHERE IsDefault=1";clear.ExecuteNonQuery();}
                using(var set=connection.CreateCommand()){set.Transaction=transaction;set.CommandText="UPDATE Events SET IsDefault=1,RowVersion=RowVersion+1,UpdatedAtUtc=$now WHERE Id=$id";set.Parameters.AddWithValue("$id",id.ToString());set.Parameters.AddWithValue("$now",Utc(DateTime.UtcNow));if(set.ExecuteNonQuery()!=1)throw new InvalidOperationException("Event not found.");}
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task<int> GetNextCaptureSequenceAsync(Guid id,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                using(var update=connection.CreateCommand())
                {
                    update.Transaction=transaction;
                    update.CommandText="UPDATE BoothSessions SET CaptureIndex=MAX(CaptureIndex,(SELECT COALESCE(MAX(Sequence),0) FROM CaptureAttempts WHERE BoothSessionId=$id))+1,UpdatedAtUtc=$now WHERE Id=$id AND State='Active'";
                    update.Parameters.AddWithValue("$id",id.ToString());update.Parameters.AddWithValue("$now",Utc(DateTime.UtcNow));
                    if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Active booth session not found.");
                }
                int value;using(var read=connection.CreateCommand()){read.Transaction=transaction;read.CommandText="SELECT CaptureIndex FROM BoothSessions WHERE Id=$id";read.Parameters.AddWithValue("$id",id.ToString());value=Convert.ToInt32(read.ExecuteScalar());}
                transaction.Commit();return Task.FromResult(value);
            }
        }

        public Task AddCapturedShotAsync(Guid sessionId,CapturedShot shot,CancellationToken token)=>AddCapturedShotsAsync(sessionId,new[]{shot},token);
        public Task AddCapturedShotsAsync(Guid sessionId,IReadOnlyList<CapturedShot> shots,CancellationToken token)
        {
            if(shots==null)throw new ArgumentNullException(nameof(shots));token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                EnsureBoothSession(connection,transaction,sessionId);
                foreach(var shot in shots){token.ThrowIfCancellationRequested();SelectAttempt(connection,transaction,sessionId,shot);}
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task ReplaceCapturedShotAsync(Guid sessionId,string previousShotId,CapturedShot replacement,CancellationToken token)=>ReplaceCapturedShotsAsync(sessionId,new Dictionary<string,CapturedShot>{{previousShotId,replacement}},token);
        public Task ReplaceCapturedShotsAsync(Guid sessionId,IReadOnlyDictionary<string,CapturedShot> replacements,CancellationToken token)
        {
            if(replacements==null)throw new ArgumentNullException(nameof(replacements));token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                EnsureBoothSession(connection,transaction,sessionId);
                foreach(var pair in replacements)
                {
                    token.ThrowIfCancellationRequested();
                    using(var previous=connection.CreateCommand()){previous.Transaction=transaction;previous.CommandText="UPDATE CaptureAttempts SET IsSelected=0 WHERE Id=$id AND BoothSessionId=$session AND IsSelected=1";previous.Parameters.AddWithValue("$id",pair.Key);previous.Parameters.AddWithValue("$session",sessionId.ToString());if(previous.ExecuteNonQuery()!=1)throw new InvalidOperationException("Selected captured shot not found.");}
                    SelectAttempt(connection,transaction,sessionId,pair.Value);
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Session>> GetActiveBoothSessionsAsync(CancellationToken token)
        {
            var values=new List<Session>();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {command.CommandText=BoothSelect+" WHERE b.State IN ('Created','Active','Finalizing') ORDER BY b.StartedAtUtc";using(var reader=command.ExecuteReader())while(reader.Read()){token.ThrowIfCancellationRequested();values.Add(ReadBoothSession(reader));}foreach(var value in values)LoadShots(connection,value);}
            return Task.FromResult<IReadOnlyList<Session>>(values);
        }

        public Task MarkRecoveredFailedAsync(Guid sessionId,string reason,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {command.CommandText="UPDATE BoothSessions SET State='Failed',CompletedAtUtc=$now,UpdatedAtUtc=$now,TerminalReason=$reason,StateVersion=StateVersion+1 WHERE Id=$id AND State IN ('Created','Active','Finalizing')";command.Parameters.AddWithValue("$now",Utc(DateTime.UtcNow));command.Parameters.AddWithValue("$reason",string.IsNullOrWhiteSpace(reason)?"Application restarted.":reason);command.Parameters.AddWithValue("$id",sessionId.ToString());command.ExecuteNonQuery();}
            return Task.CompletedTask;
        }

        static void SaveEvent(SqliteConnection connection,SqliteTransaction transaction,Session value)
        {
            var now=value.UpdatedAtUtc==DateTime.MinValue?DateTime.UtcNow:value.UpdatedAtUtc;
            using(var command=connection.CreateCommand())
            {
                command.Transaction=transaction;command.CommandText=@"INSERT INTO Events(Id,PresetId,Name,OutputDirectory,Status,IsDefault,SessionNumber,CreatedAtUtc,UpdatedAtUtc,RowVersion)
VALUES($id,$preset,$name,$path,'Active',$default,$number,$created,$updated,1)
ON CONFLICT(Id) DO UPDATE SET PresetId=excluded.PresetId,Name=excluded.Name,OutputDirectory=excluded.OutputDirectory,IsDefault=excluded.IsDefault,SessionNumber=excluded.SessionNumber,UpdatedAtUtc=excluded.UpdatedAtUtc,RowVersion=Events.RowVersion+1";
                command.Parameters.AddWithValue("$id",value.Id.ToString());command.Parameters.AddWithValue("$preset",Db(value.PresetId));command.Parameters.AddWithValue("$name",value.SessionName??"Sự kiện");command.Parameters.AddWithValue("$path",value.OutputDirectory??string.Empty);command.Parameters.AddWithValue("$default",value.IsDefault?1:0);command.Parameters.AddWithValue("$number",value.SessionNumber);command.Parameters.AddWithValue("$created",Utc(value.StartedAtUtc));command.Parameters.AddWithValue("$updated",Utc(now));command.ExecuteNonQuery();
            }
            EnsureBootstrapConfiguration(connection,transaction,value.Id,now);
        }

        void SaveBoothSession(SqliteConnection connection,SqliteTransaction transaction,Session value)
        {
            if(!value.EventId.HasValue)throw new InvalidOperationException("A booth session must belong to an event.");
            var configurationId=GetActiveConfiguration(connection,transaction,value.EventId.Value);
            var deviceId=GetDeviceId(connection,transaction);
            var relative=ToManagedRelativePath(value.OutputDirectory);
            var version=Math.Max(1,value.StateVersion);
            if(value.BasePriceMinor==0&&value.AdditionalPrintPriceMinor==0)
            {
                using(var pricing=connection.CreateCommand())
                {
                    pricing.Transaction=transaction;pricing.CommandText="SELECT BasePriceMinor,AdditionalPrintPriceMinor,Currency FROM EventConfigurations WHERE EventId=$event";pricing.Parameters.AddWithValue("$event",value.EventId.Value.ToString());
                    using(var reader=pricing.ExecuteReader())if(reader.Read()){value.BasePriceMinor=reader.GetInt64(0);value.AdditionalPrintPriceMinor=reader.GetInt64(1);value.Currency=reader.GetString(2);}
                }
            }
            using(var command=connection.CreateCommand())
            {
                command.Transaction=transaction;command.CommandText=@"INSERT INTO BoothSessions(Id,EventId,ConfigurationVersionId,OriginDeviceId,PresetId,DisplayCode,BasePriceMinor,AdditionalPrintPriceMinor,Currency,State,StateVersion,CaptureIndex,FrameIndex,StartedAtUtc,UpdatedAtUtc,CompletedAtUtc,TerminalReason,StorageRootRelativePath,FinalImageAssetId)
VALUES($id,$event,$configuration,$device,$preset,$display,$basePrice,$additionalPrintPrice,$currency,$state,$version,$captureIndex,$frameIndex,$started,$updated,$completed,$reason,$path,$final)
ON CONFLICT(Id) DO UPDATE SET PresetId=excluded.PresetId,DisplayCode=excluded.DisplayCode,State=excluded.State,StateVersion=excluded.StateVersion,CaptureIndex=MAX(BoothSessions.CaptureIndex,excluded.CaptureIndex),FrameIndex=MAX(BoothSessions.FrameIndex,excluded.FrameIndex),UpdatedAtUtc=excluded.UpdatedAtUtc,CompletedAtUtc=excluded.CompletedAtUtc,TerminalReason=excluded.TerminalReason,FinalImageAssetId=excluded.FinalImageAssetId
WHERE excluded.StateVersion=BoothSessions.StateVersion+1 AND
((BoothSessions.State=excluded.State) OR
 (BoothSessions.State='Created' AND excluded.State IN ('Active','Abandoned','Failed')) OR
 (BoothSessions.State='Active' AND excluded.State IN ('Finalizing','Abandoned','Failed')) OR
 (BoothSessions.State='Finalizing' AND excluded.State IN ('Completed','Abandoned','Failed')))";
                command.Parameters.AddWithValue("$id",value.Id.ToString());command.Parameters.AddWithValue("$event",value.EventId.Value.ToString());command.Parameters.AddWithValue("$configuration",configurationId);command.Parameters.AddWithValue("$device",deviceId);command.Parameters.AddWithValue("$preset",Db(value.PresetId));command.Parameters.AddWithValue("$display",Db(value.DisplayCode));command.Parameters.AddWithValue("$basePrice",Math.Max(0,value.BasePriceMinor));command.Parameters.AddWithValue("$additionalPrintPrice",Math.Max(0,value.AdditionalPrintPriceMinor));command.Parameters.AddWithValue("$currency",string.IsNullOrWhiteSpace(value.Currency)?"VND":value.Currency);command.Parameters.AddWithValue("$state",NormalizeState(value));command.Parameters.AddWithValue("$version",version);command.Parameters.AddWithValue("$captureIndex",value.CaptureIndex);command.Parameters.AddWithValue("$frameIndex",value.FrameIndex);command.Parameters.AddWithValue("$started",Utc(value.StartedAtUtc));command.Parameters.AddWithValue("$updated",Utc(value.UpdatedAtUtc==DateTime.MinValue?DateTime.UtcNow:value.UpdatedAtUtc));command.Parameters.AddWithValue("$completed",Db(value.CompletedAtUtc));command.Parameters.AddWithValue("$reason",Db(value.TerminalReason));command.Parameters.AddWithValue("$path",relative);command.Parameters.AddWithValue("$final",Db(value.FinalImageId));
                if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Booth session state changed concurrently or the requested state transition is invalid.");
                value.StateVersion=version;
            }
        }

        static void EnsureBootstrapConfiguration(SqliteConnection connection,SqliteTransaction transaction,Guid eventId,DateTime now)
        {
            using(var command=connection.CreateCommand())
            {command.Transaction=transaction;command.CommandText="INSERT OR IGNORE INTO ConfigurationVersions(Id,EventId,VersionNumber,ConfigurationJson,ContentHashSha256,State,CreatedAtUtc,ActivatedAtUtc) VALUES($id,$event,1,'{}',$hash,'Active',$now,$now)";command.Parameters.AddWithValue("$id","cfg-"+eventId.ToString("N")+"-1");command.Parameters.AddWithValue("$event",eventId.ToString());command.Parameters.AddWithValue("$hash",new string('0',64));command.Parameters.AddWithValue("$now",Utc(now));command.ExecuteNonQuery();}
        }

        static string GetActiveConfiguration(SqliteConnection connection,SqliteTransaction transaction,Guid eventId)
        {
            using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="SELECT Id FROM ConfigurationVersions WHERE EventId=$event AND State='Active' LIMIT 1";command.Parameters.AddWithValue("$event",eventId.ToString());var result=command.ExecuteScalar() as string;if(string.IsNullOrWhiteSpace(result))throw new InvalidOperationException("The event has no active configuration version.");return result;}
        }

        static string GetDeviceId(SqliteConnection connection,SqliteTransaction transaction)
        {using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="SELECT DeviceId FROM DeviceIdentity WHERE Id=1";return command.ExecuteScalar() as string??"local-unenrolled";}}

        static void EnsureBoothSession(SqliteConnection connection,SqliteTransaction transaction,Guid id)
        {using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="SELECT COUNT(*) FROM BoothSessions WHERE Id=$id AND State IN ('Active','Finalizing')";command.Parameters.AddWithValue("$id",id.ToString());if(Convert.ToInt32(command.ExecuteScalar())!=1)throw new InvalidOperationException("Active booth session not found.");}}

        static void SelectAttempt(SqliteConnection connection,SqliteTransaction transaction,Guid sessionId,CapturedShot shot)
        {
            if(shot==null||string.IsNullOrWhiteSpace(shot.Id))throw new ArgumentException("Captured shot identity is required.");
            using(var deselect=connection.CreateCommand()){deselect.Transaction=transaction;deselect.CommandText="UPDATE CaptureAttempts SET IsSelected=0 WHERE BoothSessionId=$session AND Sequence=$sequence AND IsSelected=1";deselect.Parameters.AddWithValue("$session",sessionId.ToString());deselect.Parameters.AddWithValue("$sequence",shot.Sequence);deselect.ExecuteNonQuery();}
            using(var select=connection.CreateCommand()){select.Transaction=transaction;select.CommandText="UPDATE CaptureAttempts SET IsSelected=1 WHERE Id=$id AND BoothSessionId=$session AND Sequence=$sequence";select.Parameters.AddWithValue("$id",shot.Id);select.Parameters.AddWithValue("$session",sessionId.ToString());select.Parameters.AddWithValue("$sequence",shot.Sequence);if(select.ExecuteNonQuery()!=1)throw new InvalidOperationException("Capture attempt was not recorded before selection.");}
        }

        Session ReadEvent(SqliteConnection connection,Guid id)
        {using(var command=connection.CreateCommand()){command.CommandText="SELECT Id,PresetId,Name,OutputDirectory,IsDefault,SessionNumber,CreatedAtUtc,UpdatedAtUtc,RemoteEventId FROM Events WHERE Id=$id";command.Parameters.AddWithValue("$id",id.ToString());using(var reader=command.ExecuteReader())return reader.Read()?ReadEvent(reader):null;}}
        static Session ReadEvent(SqliteDataReader reader)=>new Session{Id=Guid.Parse(reader.GetString(0)),PresetId=GuidValue(reader,1),SessionName=reader.GetString(2),OutputDirectory=reader.GetString(3),IsDefault=reader.GetInt32(4)!=0,SessionNumber=reader.GetInt32(5),StartedAtUtc=Parse(reader.GetString(6)),UpdatedAtUtc=Parse(reader.GetString(7)),RemoteEventId=Text(reader,8),Kind=SessionKinds.Event,Status=BoothSessionStates.Active,CapturedShots=new CapturedShot[0],CapturedFiles=new string[0],CapturedVideoFiles=new string[0],CapturedImageIds=new string[0]};

        Session ReadBoothSession(SqliteConnection connection,Guid id)
        {using(var command=connection.CreateCommand()){command.CommandText=BoothSelect+" WHERE b.Id=$id";command.Parameters.AddWithValue("$id",id.ToString());using(var reader=command.ExecuteReader())return reader.Read()?ReadBoothSession(reader):null;}}
        Session ReadBoothSession(SqliteDataReader reader)
        {var relative=reader.GetString(16);return new BoothSession{Id=Guid.Parse(reader.GetString(0)),EventId=Guid.Parse(reader.GetString(1)),PresetId=GuidValue(reader,2),DisplayCode=Text(reader,3),BasePriceMinor=reader.GetInt64(4),AdditionalPrintPriceMinor=reader.GetInt64(5),Currency=reader.GetString(6),Status=reader.GetString(7),StateVersion=reader.GetInt64(8),CaptureIndex=reader.GetInt32(9),FrameIndex=reader.GetInt32(10),StartedAtUtc=Parse(reader.GetString(11)),UpdatedAtUtc=Parse(reader.GetString(12)),CompletedAtUtc=DateValue(reader,13),TerminalReason=Text(reader,14),FinalImageId=Text(reader,15),OutputDirectory=ToFullPath(relative),FinalImagePath=reader.IsDBNull(17)?null:ToFullPath(reader.GetString(17)),Kind=SessionKinds.Booth};}
        const string BoothSelect="SELECT b.Id,b.EventId,b.PresetId,b.DisplayCode,b.BasePriceMinor,b.AdditionalPrintPriceMinor,b.Currency,b.State,b.StateVersion,b.CaptureIndex,b.FrameIndex,b.StartedAtUtc,b.UpdatedAtUtc,b.CompletedAtUtc,b.TerminalReason,b.FinalImageAssetId,b.StorageRootRelativePath,(SELECT RelativePath FROM MediaAssets WHERE Id=b.FinalImageAssetId AND State<>'Deleted') FROM BoothSessions b";

        void LoadShots(SqliteConnection connection,Session session)
        {
            var shots=new List<CapturedShot>();using(var command=connection.CreateCommand())
            {
                command.CommandText=@"SELECT a.Id,a.Sequence,a.IntentAtUtc,p.Id,p.RelativePath,v.Id,v.RelativePath
FROM CaptureAttempts a
LEFT JOIN MediaAssets p ON p.CaptureAttemptId=a.Id AND p.Kind='OriginalPicture' AND p.State<>'Deleted'
LEFT JOIN MediaAssets v ON v.CaptureAttemptId=a.Id AND v.Kind='OriginalVideo' AND v.State<>'Deleted'
WHERE a.BoothSessionId=$session AND a.IsSelected=1 AND a.State='Accepted' ORDER BY a.Sequence";
                command.Parameters.AddWithValue("$session",session.Id.ToString());using(var reader=command.ExecuteReader())while(reader.Read())shots.Add(new CapturedShot{Id=reader.GetString(0),Sequence=reader.GetInt32(1),CapturedAtUtc=Parse(reader.GetString(2)),PictureAssetId=Text(reader,3),PicturePath=reader.IsDBNull(4)?null:ToFullPath(reader.GetString(4)),VideoAssetId=Text(reader,5),VideoPath=reader.IsDBNull(6)?null:ToFullPath(reader.GetString(6))});
            }
            ProjectShots(session,shots);
        }
        static void ProjectShots(Session session,IReadOnlyList<CapturedShot> shots){session.CapturedShots=shots;session.CapturedFiles=shots.Where(x=>!string.IsNullOrWhiteSpace(x.PicturePath)).Select(x=>x.PicturePath).ToList();session.CapturedVideoFiles=shots.Where(x=>x.HasVideo).Select(x=>x.VideoPath).ToList();session.CapturedImageIds=shots.Select(x=>x.Id).ToList();}

        string ToManagedRelativePath(string path)
        {
            var full=Path.GetFullPath(path??string.Empty);var root=database.DataRoot.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Booth-session storage must stay inside the managed data root.");return full.Substring(root.Length).Replace('\\','/');
        }
        string ToFullPath(string relative){if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new InvalidOperationException("Managed path must be relative.");var root=database.DataRoot.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;var full=Path.GetFullPath(Path.Combine(database.DataRoot,relative.Replace('/',Path.DirectorySeparatorChar)));if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Managed path escapes the data root.");return full;}

        static string NormalizeState(Session value){if(value.CompletedAtUtc.HasValue&&value.Status!=BoothSessionStates.Completed&&value.Status!=BoothSessionStates.Abandoned&&value.Status!=BoothSessionStates.Failed)return BoothSessionStates.Completed;return string.IsNullOrWhiteSpace(value.Status)?BoothSessionStates.Active:value.Status;}
        static object Db(Guid? value)=>value.HasValue?(object)value.Value.ToString():System.DBNull.Value;
        static object Db(string value)=>string.IsNullOrWhiteSpace(value)?(object)System.DBNull.Value:value;
        static object Db(DateTime? value)=>value.HasValue?(object)Utc(value.Value):System.DBNull.Value;
        static string Text(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
        static Guid? GuidValue(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?(Guid?)null:Guid.Parse(reader.GetString(index));
        static DateTime? DateValue(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?(DateTime?)null:Parse(reader.GetString(index));
        static DateTime Parse(string value)=>DateTime.Parse(value).ToUniversalTime();
        static string Utc(DateTime value)=>(value==default(DateTime)?DateTime.UtcNow:value.ToUniversalTime()).ToString("O");
    }
}
