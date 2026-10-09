using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteCaptureAttemptRepository : ICaptureAttemptRepository
    {
        readonly SqliteDatabase database;
        public SqliteCaptureAttemptRepository(SqliteDatabase value) { database = value; }

        public Task BeginAsync(CaptureAttemptRecord value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (value == null || string.IsNullOrWhiteSpace(value.Id) || value.SessionId == Guid.Empty) throw new ArgumentException("Capture attempt identity is required.");
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO CaptureAttempts
(Id,BoothSessionId,Sequence,AttemptNumber,CameraId,State,IntentAtUtc,CompletedAtUtc,LastError)
VALUES($id,$session,$sequence,$attempt,$camera,$status,$intent,NULL,NULL)";
                command.Parameters.AddWithValue("$id", value.Id);
                command.Parameters.AddWithValue("$session", value.SessionId.ToString());
                command.Parameters.AddWithValue("$sequence", value.Sequence);
                command.Parameters.AddWithValue("$attempt", Math.Max(1, value.AttemptNumber));
                command.Parameters.AddWithValue("$camera", Db(value.CameraId));
                command.Parameters.AddWithValue("$status", CaptureAttemptStates.IntentRecorded);
                command.Parameters.AddWithValue("$intent", value.IntentAtUtc.ToUniversalTime().ToString("O"));
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task MarkAcceptedAsync(string attemptId, CancellationToken token) => Complete(attemptId, CaptureAttemptStates.Accepted, null, token);
        public Task MarkFailedAsync(string attemptId, string error, bool outcomeUnknown, CancellationToken token) => Complete(attemptId, outcomeUnknown ? CaptureAttemptStates.Unknown : CaptureAttemptStates.Failed, error, token);

        Task Complete(string id, string status, string error, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE CaptureAttempts SET State=$status,CompletedAtUtc=$completed,LastError=$error WHERE Id=$id";
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$error", Db(error));
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Capture attempt was not found.");
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CaptureAttemptRecord>> GetIncompleteAsync(CancellationToken token)
        {
            var result = new List<CaptureAttemptRecord>();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT a.Id,a.BoothSessionId,a.Sequence,a.AttemptNumber,a.CameraId,
(SELECT Id FROM MediaAssets WHERE CaptureAttemptId=a.Id AND Kind='OriginalPicture' LIMIT 1),
(SELECT Id FROM MediaAssets WHERE CaptureAttemptId=a.Id AND Kind='OriginalVideo' LIMIT 1),
a.State,a.IntentAtUtc,a.CompletedAtUtc,a.LastError FROM CaptureAttempts a WHERE a.State IN ('IntentRecorded','Unknown') ORDER BY a.IntentAtUtc";
                using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(ReadAttempt(reader));
                }
            }
            return Task.FromResult<IReadOnlyList<CaptureAttemptRecord>>(result);
        }

        static CaptureAttemptRecord ReadAttempt(SqliteDataReader reader) => new CaptureAttemptRecord
        {
            Id = reader.GetString(0), SessionId = Guid.Parse(reader.GetString(1)), Sequence = reader.GetInt32(2), AttemptNumber = reader.GetInt32(3),
            CameraId = Text(reader, 4), PictureAssetId = Text(reader,5), VideoAssetId = Text(reader, 6), Status = reader.GetString(7),
            IntentAtUtc = DateTime.Parse(reader.GetString(8)).ToUniversalTime(), CompletedAtUtc = reader.IsDBNull(9) ? (DateTime?)null : DateTime.Parse(reader.GetString(9)).ToUniversalTime(), LastError = Text(reader, 10)
        };

        static object Db(string value) => string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    public sealed class SqliteMediaAssetRepository : IMediaAssetRepository
    {
        readonly SqliteDatabase database;
        public SqliteMediaAssetRepository(SqliteDatabase value) { database = value; }

        public Task SaveAsync(MediaAssetRecord value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (value == null || string.IsNullOrWhiteSpace(value.Id) || value.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(value.RelativePath)) throw new ArgumentException("Media asset identity and managed path are required.");
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO MediaAssets
(Id,BoothSessionId,CaptureAttemptId,Kind,RelativePath,MimeType,FileLength,ContentHashSha256,State,RetentionClass,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$session,$attempt,$kind,$path,$mime,$length,$hash,$status,$retention,$created,$updated)
ON CONFLICT(Id) DO UPDATE SET RelativePath=excluded.RelativePath,MimeType=excluded.MimeType,FileLength=excluded.FileLength,
ContentHashSha256=COALESCE(excluded.ContentHashSha256,MediaAssets.ContentHashSha256),State=excluded.State,RetentionClass=excluded.RetentionClass,UpdatedAtUtc=excluded.UpdatedAtUtc";
                Bind(command, value);
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task<MediaAssetRecord> GetAsync(string assetId, CancellationToken token)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = Select + " WHERE Id=$id";
                command.Parameters.AddWithValue("$id", assetId);
                using (var reader = command.ExecuteReader()) return Task.FromResult(reader.Read() ? Read(reader) : null);
            }
        }

        public Task<IReadOnlyList<MediaAssetRecord>> GetBySessionAsync(Guid sessionId, CancellationToken token)
        {
            var result = new List<MediaAssetRecord>();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = Select + " WHERE BoothSessionId=$session ORDER BY CreatedAtUtc,Id";
                command.Parameters.AddWithValue("$session", sessionId.ToString());
                using (var reader = command.ExecuteReader()) while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(Read(reader)); }
            }
            return Task.FromResult<IReadOnlyList<MediaAssetRecord>>(result);
        }

        public Task<bool> HasPendingOutputAsync(Guid sessionId, CancellationToken token)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT EXISTS(
 SELECT 1 FROM OutputJobs WHERE BoothSessionId=$session AND State NOT IN ('Completed','Cancelled')
 UNION ALL
 SELECT 1 FROM EventExportJobs WHERE BoothSessionId=$session AND State NOT IN ('Completed','Cancelled')
 UNION ALL
 SELECT 1 FROM MediaAssets m
 WHERE m.BoothSessionId=$session AND m.RetentionClass<>'Work' AND m.State<>'Deleted' AND (
  NOT EXISTS(SELECT 1 FROM BoothSessions b JOIN Events e ON e.Id=b.EventId WHERE b.Id=m.BoothSessionId AND e.LifecycleState IN ('Delivered','Archived'))
  OR EXISTS(SELECT 1 FROM MediaAssetReplicas r WHERE r.MediaAssetId=m.Id AND r.Provider<>'Local' AND r.IsRequired=1 AND r.State NOT IN ('Verified','Deleted'))
 ))";
                command.Parameters.AddWithValue("$session", sessionId.ToString());
                return Task.FromResult(Convert.ToInt32(command.ExecuteScalar()) != 0);
            }
        }

        public Task MarkDeletedBySessionAsync(Guid sessionId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE MediaAssets SET State='Deleted',UpdatedAtUtc=$updated WHERE BoothSessionId=$session AND State<>'Deleted'";
                command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$session", sessionId.ToString());
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task MarkDeletedAsync(string assetId,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(assetId))return Task.CompletedTask;using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="UPDATE MediaAssets SET State='Deleted',UpdatedAtUtc=$updated WHERE Id=$id";command.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",assetId);command.ExecuteNonQuery();}return Task.CompletedTask;
        }

        static void Bind(SqliteCommand command, MediaAssetRecord value)
        {
            var created = value.CreatedAtUtc == default(DateTime) ? DateTime.UtcNow : value.CreatedAtUtc.ToUniversalTime();
            var updated = value.UpdatedAtUtc == default(DateTime) ? created : value.UpdatedAtUtc.ToUniversalTime();
            command.Parameters.AddWithValue("$id", value.Id); command.Parameters.AddWithValue("$session", value.SessionId.ToString());
            command.Parameters.AddWithValue("$attempt", Db(value.CaptureAttemptId)); command.Parameters.AddWithValue("$kind", value.Kind);
            command.Parameters.AddWithValue("$path", value.RelativePath.Replace('\\', '/')); command.Parameters.AddWithValue("$mime", value.MimeType);
            command.Parameters.AddWithValue("$length", Math.Max(0, value.FileLength)); command.Parameters.AddWithValue("$hash", Db(value.ContentHashSha256));
            command.Parameters.AddWithValue("$status", value.Status ?? MediaAssetStates.Ready); command.Parameters.AddWithValue("$retention", value.RetentionClass ?? MediaRetentionClasses.Original);
            command.Parameters.AddWithValue("$created", created.ToString("O")); command.Parameters.AddWithValue("$updated", updated.ToString("O"));
        }

        const string Select = "SELECT Id,BoothSessionId,CaptureAttemptId,Kind,RelativePath,MimeType,FileLength,ContentHashSha256,State,RetentionClass,CreatedAtUtc,UpdatedAtUtc FROM MediaAssets";
        static MediaAssetRecord Read(SqliteDataReader reader) => new MediaAssetRecord
        {
            Id=reader.GetString(0),SessionId=Guid.Parse(reader.GetString(1)),CaptureAttemptId=Text(reader,2),Kind=reader.GetString(3),RelativePath=reader.GetString(4),MimeType=reader.GetString(5),FileLength=reader.GetInt64(6),ContentHashSha256=Text(reader,7),Status=reader.GetString(8),RetentionClass=reader.GetString(9),CreatedAtUtc=DateTime.Parse(reader.GetString(10)).ToUniversalTime(),UpdatedAtUtc=DateTime.Parse(reader.GetString(11)).ToUniversalTime()
        };
        static object Db(string value) => string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    public sealed class SqliteMediaAssetReplicaRepository : IMediaAssetReplicaRepository
    {
        readonly SqliteDatabase database;
        public SqliteMediaAssetReplicaRepository(SqliteDatabase value){database=value;}

        public Task UpsertAsync(MediaAssetReplicaRecord value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(value==null||string.IsNullOrWhiteSpace(value.AssetId)||string.IsNullOrWhiteSpace(value.Provider)||string.IsNullOrWhiteSpace(value.State))throw new ArgumentException("Media replica identity is required.");
            var now=value.UpdatedAtUtc==default(DateTime)?DateTime.UtcNow:value.UpdatedAtUtc.ToUniversalTime();var created=value.CreatedAtUtc==default(DateTime)?now:value.CreatedAtUtc.ToUniversalTime();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"INSERT INTO MediaAssetReplicas(
Id,MediaAssetId,Provider,IsRequired,PolicyReason,State,RemoteReference,AttemptCount,NextAttemptAtUtc,LastErrorCategory,LastError,ContentHashSha256,FileLength,VerifiedAtUtc,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$asset,$provider,$required,$reason,$state,$reference,$attempts,$next,$category,$error,$hash,$length,$verified,$created,$updated)
ON CONFLICT(MediaAssetId,Provider) DO UPDATE SET
 IsRequired=excluded.IsRequired,PolicyReason=excluded.PolicyReason,State=excluded.State,
 RemoteReference=COALESCE(excluded.RemoteReference,MediaAssetReplicas.RemoteReference),
 AttemptCount=MAX(MediaAssetReplicas.AttemptCount,excluded.AttemptCount),NextAttemptAtUtc=excluded.NextAttemptAtUtc,
 LastErrorCategory=excluded.LastErrorCategory,LastError=excluded.LastError,
 ContentHashSha256=COALESCE(excluded.ContentHashSha256,MediaAssetReplicas.ContentHashSha256),
 FileLength=MAX(MediaAssetReplicas.FileLength,excluded.FileLength),
 VerifiedAtUtc=COALESCE(excluded.VerifiedAtUtc,MediaAssetReplicas.VerifiedAtUtc),UpdatedAtUtc=excluded.UpdatedAtUtc";
                command.Parameters.AddWithValue("$id",string.IsNullOrWhiteSpace(value.Id)?"rep-"+Guid.NewGuid().ToString("N"):value.Id);command.Parameters.AddWithValue("$asset",value.AssetId);command.Parameters.AddWithValue("$provider",value.Provider);command.Parameters.AddWithValue("$required",value.IsRequired?1:0);command.Parameters.AddWithValue("$reason",string.IsNullOrWhiteSpace(value.PolicyReason)?"SessionDelivery":value.PolicyReason);command.Parameters.AddWithValue("$state",value.State);command.Parameters.AddWithValue("$reference",Db(value.RemoteReference));command.Parameters.AddWithValue("$attempts",Math.Max(0,value.AttemptCount));command.Parameters.AddWithValue("$next",Db(value.NextAttemptAtUtc));command.Parameters.AddWithValue("$category",Db(value.LastErrorCategory));command.Parameters.AddWithValue("$error",Db(value.LastError));command.Parameters.AddWithValue("$hash",Db(value.ContentHashSha256));command.Parameters.AddWithValue("$length",Math.Max(0,value.FileLength));command.Parameters.AddWithValue("$verified",Db(value.VerifiedAtUtc));command.Parameters.AddWithValue("$created",created.ToString("O"));command.Parameters.AddWithValue("$updated",now.ToString("O"));command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MediaAssetReplicaRecord>> GetByAssetAsync(string assetId,CancellationToken token)
        {
            var values=new List<MediaAssetReplicaRecord>();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=Select+" WHERE MediaAssetId=$asset ORDER BY CASE Provider WHEN 'Local' THEN 0 WHEN 'SystemLibrary' THEN 1 WHEN 'GoogleDrive' THEN 2 ELSE 3 END";command.Parameters.AddWithValue("$asset",assetId);
                using(var reader=command.ExecuteReader())while(reader.Read()){token.ThrowIfCancellationRequested();values.Add(ReadReplica(reader));}
            }
            return Task.FromResult<IReadOnlyList<MediaAssetReplicaRecord>>(values);
        }

        public Task SetStateAsync(string assetId,string provider,string state,string remoteReference,DateTime? nextAttemptAtUtc,string errorCategory,string error,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"UPDATE MediaAssetReplicas SET State=$state,
 AttemptCount=AttemptCount+CASE WHEN $state='Uploading' THEN 1 ELSE 0 END,
 NextAttemptAtUtc=$next,RemoteReference=COALESCE($reference,RemoteReference),LastErrorCategory=$category,LastError=$error,
 VerifiedAtUtc=CASE WHEN $state='Verified' THEN COALESCE(VerifiedAtUtc,$now) ELSE VerifiedAtUtc END,UpdatedAtUtc=$now
WHERE MediaAssetId=$asset AND Provider=$provider";
                command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$next",Db(nextAttemptAtUtc));command.Parameters.AddWithValue("$reference",Db(remoteReference));command.Parameters.AddWithValue("$category",Db(errorCategory));command.Parameters.AddWithValue("$error",Db(error));command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$asset",assetId);command.Parameters.AddWithValue("$provider",provider);
                if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Media replica was not found.");
            }
            return Task.CompletedTask;
        }

        public Task ReplaceOnlineProviderAsync(string assetId,string provider,string state,string remoteReference,string contentHashSha256,long fileLength,DateTime? verifiedAtUtc,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(provider!=MediaReplicaProviders.SystemLibrary&&provider!=MediaReplicaProviders.GoogleDrive)throw new ArgumentException("A concrete online provider is required.",nameof(provider));
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                var now=DateTime.UtcNow;var required=true;var attempts=0;var created=now;
                using(var read=connection.CreateCommand())
                {
                    read.Transaction=transaction;read.CommandText="SELECT IsRequired,AttemptCount,CreatedAtUtc FROM MediaAssetReplicas WHERE MediaAssetId=$asset AND Provider='Online'";read.Parameters.AddWithValue("$asset",assetId);
                    using(var reader=read.ExecuteReader())if(reader.Read()){required=reader.GetInt32(0)!=0;attempts=reader.GetInt32(1);created=DateTime.Parse(reader.GetString(2)).ToUniversalTime();}
                }
                using(var remove=connection.CreateCommand()){remove.Transaction=transaction;remove.CommandText="DELETE FROM MediaAssetReplicas WHERE MediaAssetId=$asset AND Provider='Online'";remove.Parameters.AddWithValue("$asset",assetId);remove.ExecuteNonQuery();}
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;command.CommandText=@"INSERT INTO MediaAssetReplicas(Id,MediaAssetId,Provider,IsRequired,PolicyReason,State,RemoteReference,AttemptCount,ContentHashSha256,FileLength,VerifiedAtUtc,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$asset,$provider,$required,'SessionDelivery',$state,$reference,$attempts,$hash,$length,$verified,$created,$updated)
ON CONFLICT(MediaAssetId,Provider) DO UPDATE SET IsRequired=excluded.IsRequired,State=excluded.State,
 RemoteReference=COALESCE(excluded.RemoteReference,MediaAssetReplicas.RemoteReference),AttemptCount=MAX(MediaAssetReplicas.AttemptCount,excluded.AttemptCount),
 ContentHashSha256=COALESCE(excluded.ContentHashSha256,MediaAssetReplicas.ContentHashSha256),FileLength=MAX(MediaAssetReplicas.FileLength,excluded.FileLength),
 VerifiedAtUtc=COALESCE(excluded.VerifiedAtUtc,MediaAssetReplicas.VerifiedAtUtc),NextAttemptAtUtc=NULL,LastErrorCategory=NULL,LastError=NULL,UpdatedAtUtc=excluded.UpdatedAtUtc";
                    command.Parameters.AddWithValue("$id","rep-"+Guid.NewGuid().ToString("N"));command.Parameters.AddWithValue("$asset",assetId);command.Parameters.AddWithValue("$provider",provider);command.Parameters.AddWithValue("$required",required?1:0);command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$reference",Db(remoteReference));command.Parameters.AddWithValue("$attempts",attempts);command.Parameters.AddWithValue("$hash",Db(contentHashSha256));command.Parameters.AddWithValue("$length",Math.Max(0,fileLength));command.Parameters.AddWithValue("$verified",Db(verifiedAtUtc));command.Parameters.AddWithValue("$created",created.ToString("O"));command.Parameters.AddWithValue("$updated",now.ToString("O"));command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        const string Select="SELECT Id,MediaAssetId,Provider,IsRequired,PolicyReason,State,RemoteReference,AttemptCount,NextAttemptAtUtc,LastErrorCategory,LastError,ContentHashSha256,FileLength,VerifiedAtUtc,CreatedAtUtc,UpdatedAtUtc FROM MediaAssetReplicas";
        static MediaAssetReplicaRecord ReadReplica(SqliteDataReader reader)=>new MediaAssetReplicaRecord{Id=reader.GetString(0),AssetId=reader.GetString(1),Provider=reader.GetString(2),IsRequired=reader.GetInt32(3)!=0,PolicyReason=reader.GetString(4),State=reader.GetString(5),RemoteReference=Text(reader,6),AttemptCount=reader.GetInt32(7),NextAttemptAtUtc=Date(reader,8),LastErrorCategory=Text(reader,9),LastError=Text(reader,10),ContentHashSha256=Text(reader,11),FileLength=reader.GetInt64(12),VerifiedAtUtc=Date(reader,13),CreatedAtUtc=DateTime.Parse(reader.GetString(14)).ToUniversalTime(),UpdatedAtUtc=DateTime.Parse(reader.GetString(15)).ToUniversalTime()};
        static DateTime? Date(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?(DateTime?)null:DateTime.Parse(reader.GetString(index)).ToUniversalTime();
        static object Db(string value)=>string.IsNullOrWhiteSpace(value)?(object)DBNull.Value:value;
        static object Db(DateTime? value)=>value.HasValue?(object)value.Value.ToUniversalTime().ToString("O"):DBNull.Value;
        static string Text(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
    }

    public sealed class SqliteDurableOutputJobRepository : IDurableOutputJobRepository
    {
        readonly SqliteDatabase database;
        public SqliteDurableOutputJobRepository(SqliteDatabase value){database=value;}
        public Task<DurableOutputJobRecord> CreateIntentAsync(DurableOutputJobRecord value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(value==null||string.IsNullOrWhiteSpace(value.Id)||value.SessionId==Guid.Empty||string.IsNullOrWhiteSpace(value.JobType)||string.IsNullOrWhiteSpace(value.IdempotencyKey))throw new ArgumentException("Durable output intent identity is required.");
            var now=value.CreatedAtUtc==default(DateTime)?DateTime.UtcNow:value.CreatedAtUtc.ToUniversalTime();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                EnsureSessionBelongsToCurrentDevice(connection,value.SessionId);
                command.CommandText=@"INSERT OR IGNORE INTO OutputJobs(Id,BoothSessionId,DeliverableId,MediaAssetId,JobType,IdempotencyKey,State,AttemptCount,ProtectedAuthorization,AuthorizationScope,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$session,$deliverable,$asset,$type,$key,$state,0,$authorization,$scope,$created,$updated);
UPDATE OutputJobs SET ProtectedAuthorization=CASE WHEN $authorization IS NOT NULL THEN $authorization ELSE ProtectedAuthorization END, AuthorizationScope=CASE WHEN $authorization IS NOT NULL THEN $scope ELSE COALESCE(AuthorizationScope,$scope) END WHERE IdempotencyKey=$key AND BoothSessionId=$session;";
                command.Parameters.AddWithValue("$id",value.Id);command.Parameters.AddWithValue("$session",value.SessionId.ToString());command.Parameters.AddWithValue("$deliverable",Db(value.DeliverableId));command.Parameters.AddWithValue("$asset",Db(value.AssetId));command.Parameters.AddWithValue("$type",value.JobType);command.Parameters.AddWithValue("$key",value.IdempotencyKey);command.Parameters.AddWithValue("$state",DurableOutputJobStates.Pending);command.Parameters.AddWithValue("$authorization",Db(value.ProtectedAuthorization));command.Parameters.AddWithValue("$scope",Db(value.AuthorizationScope));command.Parameters.AddWithValue("$created",now.ToString("O"));command.Parameters.AddWithValue("$updated",now.ToString("O"));command.ExecuteNonQuery();
                using(var read=connection.CreateCommand())
                {
                    read.CommandText=JobSelect+" WHERE IdempotencyKey=$key";read.Parameters.AddWithValue("$key",value.IdempotencyKey);
                    using(var reader=read.ExecuteReader())
                    {
                        if(!reader.Read())throw new InvalidOperationException("Durable output intent was not stored.");
                        var stored=ReadJob(reader);
                        if(stored.SessionId!=value.SessionId||!string.Equals(stored.DeliverableId,value.DeliverableId,StringComparison.Ordinal)||!string.Equals(stored.AssetId,value.AssetId,StringComparison.Ordinal)||!string.Equals(stored.JobType,value.JobType,StringComparison.Ordinal))throw new InvalidOperationException("The idempotency key is already bound to a different output intent.");
                        return Task.FromResult(stored);
                    }
                }
            }
        }
        public Task<DurableOutputJobRecord> LeaseNextDueAsync(string leaseId,DateTime leaseExpiresAtUtc,CancellationToken token,string authorizationScope=null)
        {
            token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(leaseId))throw new ArgumentException("Lease ID is required.",nameof(leaseId));
            var now=DateTime.UtcNow;
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                DurableOutputJobRecord job=null;
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;
                    command.Parameters.AddWithValue("$scope",Db(authorizationScope));
                    command.CommandText=JobSelect+@" WHERE State IN ('Pending','RetryWaiting','UnknownOutcome')
AND EXISTS(SELECT 1 FROM BoothSessions current_session WHERE current_session.Id=OutputJobs.BoothSessionId AND current_session.OriginDeviceId=$device)
AND ($scope IS NULL OR AuthorizationScope=$scope OR (AuthorizationScope LIKE 'delivery:%' AND ProtectedAuthorization IS NOT NULL))
AND (NextRetryAtUtc IS NULL OR NextRetryAtUtc<=$now)
AND (JobType='UploadAsset'
 OR (JobType='CommitDeliverable' AND NOT EXISTS(SELECT 1 FROM OutputJobs dependency WHERE dependency.DeliverableId=OutputJobs.DeliverableId AND dependency.JobType='UploadAsset' AND dependency.State<>'Completed'))
 OR (JobType='CreateGallery' AND EXISTS(SELECT 1 FROM OutputJobs dependency WHERE dependency.DeliverableId=OutputJobs.DeliverableId AND dependency.JobType='CommitDeliverable' AND dependency.State='Completed')))
ORDER BY CASE JobType WHEN 'UploadAsset' THEN 0 WHEN 'CommitDeliverable' THEN 1 ELSE 2 END,CreatedAtUtc,Id LIMIT 1";
                    command.Parameters.AddWithValue("$now",now.ToString("O"));
                    command.Parameters.AddWithValue("$device",CurrentDeviceId(connection,transaction));
                    using(var reader=command.ExecuteReader())if(reader.Read())job=ReadJob(reader);
                }
                if(job==null){transaction.Commit();return Task.FromResult<DurableOutputJobRecord>(null);}
                using(var update=connection.CreateCommand())
                {
                    update.Transaction=transaction;update.CommandText=@"UPDATE OutputJobs SET State='Leased',LeaseId=$lease,LeaseExpiresAtUtc=$expires,UpdatedAtUtc=$now WHERE Id=$id AND State=$state";
                    update.Parameters.AddWithValue("$lease",leaseId);update.Parameters.AddWithValue("$expires",leaseExpiresAtUtc.ToUniversalTime().ToString("O"));update.Parameters.AddWithValue("$now",now.ToString("O"));update.Parameters.AddWithValue("$id",job.Id);update.Parameters.AddWithValue("$state",job.State);
                    if(update.ExecuteNonQuery()!=1){transaction.Rollback();return Task.FromResult<DurableOutputJobRecord>(null);}
                }
                transaction.Commit();job.State=DurableOutputJobStates.Leased;return Task.FromResult(job);
            }
        }
        public Task CompleteAsync(string jobId,string externalReference,CancellationToken token)
        {
            return UpdateTerminalAsync(jobId,DurableOutputJobStates.Completed,null,externalReference,token);
        }
        public Task ScheduleRetryAsync(string jobId,DateTime nextRetryAtUtc,string error,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"UPDATE OutputJobs SET State='RetryWaiting',NextRetryAtUtc=$retry,LeaseId=NULL,LeaseExpiresAtUtc=NULL,LastError=$error,UpdatedAtUtc=$now WHERE Id=$id AND State NOT IN ('Completed','PermanentFailure','Cancelled')";
                command.Parameters.AddWithValue("$retry",nextRetryAtUtc.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$error",Db(error));command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",jobId);
                if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Durable output job was not found or is already terminal.");
            }return Task.CompletedTask;
        }
        public Task FailAsync(string jobId,string error,CancellationToken token)
        {
            return UpdateTerminalAsync(jobId,DurableOutputJobStates.PermanentFailure,error,null,token);
        }
        public Task FailPendingDependentsAsync(string deliverableId,string error,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(deliverableId))return Task.CompletedTask;using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"UPDATE OutputJobs SET State='PermanentFailure',LeaseId=NULL,LeaseExpiresAtUtc=NULL,LastError=$error,UpdatedAtUtc=$now WHERE DeliverableId=$deliverable AND JobType IN ('CommitDeliverable','CreateGallery') AND State NOT IN ('Completed','PermanentFailure','Cancelled')";
                command.Parameters.AddWithValue("$deliverable",deliverableId);command.Parameters.AddWithValue("$error",Db(error));command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
            }return Task.CompletedTask;
        }
        public Task SetStateAsync(string jobId,string state,string error,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText=@"UPDATE OutputJobs SET State=$state,AttemptCount=CASE WHEN $state='Submitting' THEN AttemptCount+1 ELSE AttemptCount END,LastError=$error,UpdatedAtUtc=$updated
WHERE Id=$id AND (State=$state
 OR ($state='Submitting' AND State IN ('Pending','Leased','RetryWaiting'))
 OR ($state='Submitted' AND State='Submitting')
 OR ($state='Completed' AND State IN ('Pending','Submitting','Submitted'))
 OR ($state='RetryWaiting' AND State IN ('Pending','Leased'))
 OR ($state='UnknownOutcome' AND State IN ('Submitting','Submitted'))
 OR ($state IN ('PermanentFailure','Cancelled') AND State NOT IN ('Completed','PermanentFailure','Cancelled','UnknownOutcome')))";command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$error",string.IsNullOrWhiteSpace(error)?(object)DBNull.Value:error);command.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",jobId);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Durable output job was not found or the requested state transition is invalid.");}return Task.CompletedTask;
        }
        public Task ReconcileInterruptedAsync(CancellationToken token){token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText=@"UPDATE OutputJobs SET State='UnknownOutcome',LastError='Application stopped while an external output operation was being submitted.',LeaseId=NULL,LeaseExpiresAtUtc=NULL,UpdatedAtUtc=$now WHERE State IN ('Submitting','Submitted') AND EXISTS(SELECT 1 FROM BoothSessions b WHERE b.Id=OutputJobs.BoothSessionId AND b.OriginDeviceId=$device);
UPDATE OutputJobs SET State='RetryWaiting',LeaseId=NULL,LeaseExpiresAtUtc=NULL,NextRetryAtUtc=$now,UpdatedAtUtc=$now WHERE State='Leased' AND EXISTS(SELECT 1 FROM BoothSessions b WHERE b.Id=OutputJobs.BoothSessionId AND b.OriginDeviceId=$device);";command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$device",CurrentDeviceId(connection,null));command.ExecuteNonQuery();}return Task.CompletedTask;}
        public Task<int> RetryPendingAsync(string deliverableId,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                var now=DateTime.UtcNow.ToString("O");var device=CurrentDeviceId(connection,transaction);var requested=string.IsNullOrWhiteSpace(deliverableId)?(object)DBNull.Value:deliverableId;int count;
                using(var countCommand=connection.CreateCommand())
                {
                    countCommand.Transaction=transaction;
                    countCommand.CommandText=@"SELECT COUNT(DISTINCT j.DeliverableId) FROM OutputJobs j
INNER JOIN BoothSessions b ON b.Id=j.BoothSessionId
WHERE b.OriginDeviceId=$device AND j.State IN ('RetryWaiting','UnknownOutcome','PermanentFailure')
AND ($deliverable IS NULL OR j.DeliverableId=$deliverable)";
                    countCommand.Parameters.AddWithValue("$device",device);countCommand.Parameters.AddWithValue("$deliverable",requested);count=Convert.ToInt32(countCommand.ExecuteScalar());
                }
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;
                    command.CommandText=@"UPDATE OutputJobs SET State='RetryWaiting',AttemptCount=0,NextRetryAtUtc=$now,LeaseId=NULL,LeaseExpiresAtUtc=NULL,LastError=NULL,UpdatedAtUtc=$now
WHERE State IN ('RetryWaiting','UnknownOutcome','PermanentFailure') AND ($deliverable IS NULL OR DeliverableId=$deliverable)
AND EXISTS(SELECT 1 FROM BoothSessions b WHERE b.Id=OutputJobs.BoothSessionId AND b.OriginDeviceId=$device);
UPDATE Deliverables SET State='RetryWaiting',LastError=NULL
WHERE Id IN(SELECT DISTINCT j.DeliverableId FROM OutputJobs j INNER JOIN BoothSessions b ON b.Id=j.BoothSessionId
 WHERE b.OriginDeviceId=$device AND j.State='RetryWaiting' AND ($deliverable IS NULL OR j.DeliverableId=$deliverable))
AND State NOT IN ('GalleryReady','Expired')";
                    command.Parameters.AddWithValue("$now",now);command.Parameters.AddWithValue("$deliverable",requested);command.Parameters.AddWithValue("$device",device);command.ExecuteNonQuery();
                }
                transaction.Commit();return Task.FromResult(count);
            }
        }
        Task UpdateTerminalAsync(string jobId,string state,string error,string externalReference,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"UPDATE OutputJobs SET State=$state,NextRetryAtUtc=NULL,LeaseId=NULL,LeaseExpiresAtUtc=NULL,ExternalReference=COALESCE($reference,ExternalReference),LastError=$error,UpdatedAtUtc=$now WHERE Id=$id AND State NOT IN ('Completed','PermanentFailure','Cancelled')";
                command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$reference",Db(externalReference));command.Parameters.AddWithValue("$error",Db(error));command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",jobId);
                if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Durable output job was not found or is already terminal.");
            }return Task.CompletedTask;
        }
        const string JobSelect="SELECT Id,BoothSessionId,DeliverableId,MediaAssetId,JobType,IdempotencyKey,State,AttemptCount,NextRetryAtUtc,ExternalReference,LastError,ProtectedAuthorization,CreatedAtUtc,UpdatedAtUtc,AuthorizationScope FROM OutputJobs";
        static void EnsureSessionBelongsToCurrentDevice(SqliteConnection connection,Guid sessionId)
        {
            using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT COUNT(*) FROM BoothSessions WHERE Id=$session AND OriginDeviceId=$device";
                command.Parameters.AddWithValue("$session",sessionId.ToString());
                command.Parameters.AddWithValue("$device",CurrentDeviceId(connection,null));
                if(Convert.ToInt32(command.ExecuteScalar())!=1)throw new InvalidOperationException("Lượt chụp không thuộc thiết bị đang hoạt động.");
            }
        }
        static string CurrentDeviceId(SqliteConnection connection,SqliteTransaction transaction)
        {
            using(var command=connection.CreateCommand())
            {
                command.Transaction=transaction;
                command.CommandText=@"SELECT p.DeviceId FROM AppState a
INNER JOIN DeviceProfiles p ON p.Id=a.ActiveProfileId
WHERE a.Id=1 AND p.EnrollmentState='Enrolled' LIMIT 1";
                var current=command.ExecuteScalar() as string;
                if(!string.IsNullOrWhiteSpace(current))return current;
                command.CommandText="SELECT COUNT(*) FROM DeviceProfiles WHERE EnrollmentState='Enrolled'";
                return Convert.ToInt32(command.ExecuteScalar())==0?"local-unenrolled":string.Empty;
            }
        }
        static DurableOutputJobRecord ReadJob(SqliteDataReader reader)=>new DurableOutputJobRecord{Id=reader.GetString(0),SessionId=Guid.Parse(reader.GetString(1)),DeliverableId=Text(reader,2),AssetId=Text(reader,3),JobType=reader.GetString(4),IdempotencyKey=reader.GetString(5),State=reader.GetString(6),AttemptCount=reader.GetInt32(7),NextRetryAtUtc=reader.IsDBNull(8)?(DateTime?)null:DateTime.Parse(reader.GetString(8)).ToUniversalTime(),ExternalReference=Text(reader,9),LastError=Text(reader,10),ProtectedAuthorization=Text(reader,11),AuthorizationScope=Text(reader,14),CreatedAtUtc=DateTime.Parse(reader.GetString(12)).ToUniversalTime(),UpdatedAtUtc=DateTime.Parse(reader.GetString(13)).ToUniversalTime()};
        static object Db(string value)=>string.IsNullOrWhiteSpace(value)?(object)DBNull.Value:value;
        static string Text(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
    }

    public sealed class SqliteEventExportJobRepository : IEventExportJobRepository
    {
        readonly SqliteDatabase database;
        public SqliteEventExportJobRepository(SqliteDatabase value){database=value;}

        public Task<EventExportJobRecord> CreateIntentAsync(EventExportJobRecord value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(value==null||string.IsNullOrWhiteSpace(value.Id)||value.EventId==Guid.Empty||value.SessionId==Guid.Empty||string.IsNullOrWhiteSpace(value.DeliverableId)||string.IsNullOrWhiteSpace(value.DestinationRoot)||string.IsNullOrWhiteSpace(value.TurnFolderName)||string.IsNullOrWhiteSpace(value.IdempotencyKey))throw new ArgumentException("Event export identity and destination are required.");
            var created=value.CreatedAtUtc==default(DateTime)?DateTime.UtcNow:value.CreatedAtUtc.ToUniversalTime();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                EnsureSessionBelongsToCurrentDevice(connection,value.SessionId);
                command.CommandText=@"INSERT OR IGNORE INTO EventExportJobs(Id,EventId,BoothSessionId,DeliverableId,DestinationRoot,TurnFolderName,IdempotencyKey,State,AttemptCount,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$event,$session,$deliverable,$destination,$folder,$key,'Pending',0,$created,$created);";
                command.Parameters.AddWithValue("$id",value.Id);command.Parameters.AddWithValue("$event",value.EventId.ToString());command.Parameters.AddWithValue("$session",value.SessionId.ToString());command.Parameters.AddWithValue("$deliverable",value.DeliverableId);command.Parameters.AddWithValue("$destination",value.DestinationRoot);command.Parameters.AddWithValue("$folder",value.TurnFolderName);command.Parameters.AddWithValue("$key",value.IdempotencyKey);command.Parameters.AddWithValue("$created",created.ToString("O"));command.ExecuteNonQuery();
                using(var read=connection.CreateCommand())
                {
                    read.CommandText=Select+" WHERE IdempotencyKey=$key";read.Parameters.AddWithValue("$key",value.IdempotencyKey);
                    using(var reader=read.ExecuteReader())
                    {
                        if(!reader.Read())throw new InvalidOperationException("Event export intent was not stored.");
                        var stored=Read(reader);
                        if(stored.EventId!=value.EventId||stored.SessionId!=value.SessionId||!string.Equals(stored.DeliverableId,value.DeliverableId,StringComparison.Ordinal)||!string.Equals(stored.DestinationRoot,value.DestinationRoot,StringComparison.OrdinalIgnoreCase)||!string.Equals(stored.TurnFolderName,value.TurnFolderName,StringComparison.Ordinal))throw new InvalidOperationException("The idempotency key is already bound to a different Event export.");
                        return Task.FromResult(stored);
                    }
                }
            }
        }

        public Task<EventExportJobRecord> LeaseNextDueAsync(string leaseId,DateTime leaseExpiresAtUtc,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(leaseId))throw new ArgumentException("Lease ID is required.",nameof(leaseId));var now=DateTime.UtcNow;
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                EventExportJobRecord job=null;
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;command.CommandText=Select+@" WHERE State IN ('Pending','RetryWaiting')
AND (NextRetryAtUtc IS NULL OR NextRetryAtUtc<=$now)
AND EXISTS(SELECT 1 FROM BoothSessions b WHERE b.Id=EventExportJobs.BoothSessionId AND b.OriginDeviceId=$device)
ORDER BY CreatedAtUtc,Id LIMIT 1";
                    command.Parameters.AddWithValue("$now",now.ToString("O"));command.Parameters.AddWithValue("$device",CurrentDeviceId(connection,transaction));using(var reader=command.ExecuteReader())if(reader.Read())job=Read(reader);
                }
                if(job==null){transaction.Commit();return Task.FromResult<EventExportJobRecord>(null);}
                using(var update=connection.CreateCommand())
                {
                    update.Transaction=transaction;update.CommandText=@"UPDATE EventExportJobs SET State='Leased',AttemptCount=AttemptCount+1,LeaseId=$lease,LeaseExpiresAtUtc=$expires,UpdatedAtUtc=$now WHERE Id=$id AND State=$state";
                    update.Parameters.AddWithValue("$lease",leaseId);update.Parameters.AddWithValue("$expires",leaseExpiresAtUtc.ToUniversalTime().ToString("O"));update.Parameters.AddWithValue("$now",now.ToString("O"));update.Parameters.AddWithValue("$id",job.Id);update.Parameters.AddWithValue("$state",job.State);
                    if(update.ExecuteNonQuery()!=1){transaction.Rollback();return Task.FromResult<EventExportJobRecord>(null);}
                }
                transaction.Commit();job.State=DurableOutputJobStates.Leased;job.AttemptCount++;return Task.FromResult(job);
            }
        }

        public Task CompleteAsync(string jobId,string exportedPath,CancellationToken token)=>TerminalAsync(jobId,DurableOutputJobStates.Completed,null,exportedPath,token);
        public Task FailAsync(string jobId,string error,CancellationToken token)=>TerminalAsync(jobId,DurableOutputJobStates.PermanentFailure,error,null,token);
        public Task ScheduleRetryAsync(string jobId,DateTime nextRetryAtUtc,string error,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"UPDATE EventExportJobs SET State='RetryWaiting',NextRetryAtUtc=$retry,LeaseId=NULL,LeaseExpiresAtUtc=NULL,LastError=$error,UpdatedAtUtc=$now WHERE Id=$id AND State NOT IN ('Completed','PermanentFailure','Cancelled')";
                command.Parameters.AddWithValue("$retry",nextRetryAtUtc.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$error",Db(error));command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",jobId);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Event export job was not found or is already terminal.");
            }return Task.CompletedTask;
        }
        public Task ReconcileInterruptedAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"UPDATE EventExportJobs SET State='RetryWaiting',NextRetryAtUtc=$now,LeaseId=NULL,LeaseExpiresAtUtc=NULL,LastError='Ứng dụng đã dừng khi đang xuất ảnh; tác vụ sẽ được tiếp tục.',UpdatedAtUtc=$now WHERE State='Leased' AND EXISTS(SELECT 1 FROM BoothSessions b WHERE b.Id=EventExportJobs.BoothSessionId AND b.OriginDeviceId=$device);";
                command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$device",CurrentDeviceId(connection,null));command.ExecuteNonQuery();
            }return Task.CompletedTask;
        }
        Task TerminalAsync(string jobId,string state,string error,string exportedPath,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"UPDATE EventExportJobs SET State=$state,NextRetryAtUtc=NULL,LeaseId=NULL,LeaseExpiresAtUtc=NULL,ExportedPath=COALESCE($path,ExportedPath),LastError=$error,UpdatedAtUtc=$now WHERE Id=$id AND State NOT IN ('Completed','PermanentFailure','Cancelled')";
                command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$path",Db(exportedPath));command.Parameters.AddWithValue("$error",Db(error));command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",jobId);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Event export job was not found or is already terminal.");
            }return Task.CompletedTask;
        }
        static void EnsureSessionBelongsToCurrentDevice(SqliteConnection connection,Guid sessionId)
        {
            using(var command=connection.CreateCommand()){command.CommandText="SELECT COUNT(*) FROM BoothSessions WHERE Id=$session AND OriginDeviceId=$device";command.Parameters.AddWithValue("$session",sessionId.ToString());command.Parameters.AddWithValue("$device",CurrentDeviceId(connection,null));if(Convert.ToInt32(command.ExecuteScalar())!=1)throw new InvalidOperationException("Lượt chụp không thuộc thiết bị đang hoạt động.");}
        }
        static string CurrentDeviceId(SqliteConnection connection,SqliteTransaction transaction)
        {
            using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText=@"SELECT p.DeviceId FROM AppState a INNER JOIN DeviceProfiles p ON p.Id=a.ActiveProfileId WHERE a.Id=1 AND p.EnrollmentState='Enrolled' LIMIT 1";var current=command.ExecuteScalar() as string;if(!string.IsNullOrWhiteSpace(current))return current;command.CommandText="SELECT COUNT(*) FROM DeviceProfiles WHERE EnrollmentState='Enrolled'";return Convert.ToInt32(command.ExecuteScalar())==0?"local-unenrolled":string.Empty;}
        }
        const string Select="SELECT Id,EventId,BoothSessionId,DeliverableId,DestinationRoot,TurnFolderName,IdempotencyKey,State,AttemptCount,NextRetryAtUtc,ExportedPath,LastError,CreatedAtUtc,UpdatedAtUtc FROM EventExportJobs";
        static EventExportJobRecord Read(SqliteDataReader reader)=>new EventExportJobRecord{Id=reader.GetString(0),EventId=Guid.Parse(reader.GetString(1)),SessionId=Guid.Parse(reader.GetString(2)),DeliverableId=reader.GetString(3),DestinationRoot=reader.GetString(4),TurnFolderName=reader.GetString(5),IdempotencyKey=reader.GetString(6),State=reader.GetString(7),AttemptCount=reader.GetInt32(8),NextRetryAtUtc=Date(reader,9),ExportedPath=Text(reader,10),LastError=Text(reader,11),CreatedAtUtc=DateTime.Parse(reader.GetString(12)).ToUniversalTime(),UpdatedAtUtc=DateTime.Parse(reader.GetString(13)).ToUniversalTime()};
        static DateTime? Date(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?(DateTime?)null:DateTime.Parse(reader.GetString(index)).ToUniversalTime();
        static object Db(string value)=>string.IsNullOrWhiteSpace(value)?(object)DBNull.Value:value;
        static string Text(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
    }

    public sealed class SqliteGalleryLinkCacheRepository : IGalleryLinkCacheRepository
    {
        readonly SqliteDatabase database;
        public SqliteGalleryLinkCacheRepository(SqliteDatabase value){database=value;}
        public Task SaveAsync(GalleryLinkCacheRecord value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(value==null||string.IsNullOrWhiteSpace(value.DeliverableId)||value.PublicUrl==null||!value.PublicUrl.IsAbsoluteUri)throw new ArgumentException("A deliverable and absolute gallery URL are required.");using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"INSERT INTO GalleryLinkCache(DeliverableId,PublicUrl,ExpiresAtUtc,ReceivedAtUtc) VALUES($id,$url,$expires,$received) ON CONFLICT(DeliverableId) DO UPDATE SET PublicUrl=excluded.PublicUrl,ExpiresAtUtc=excluded.ExpiresAtUtc,ReceivedAtUtc=excluded.ReceivedAtUtc";
                command.Parameters.AddWithValue("$id",value.DeliverableId);command.Parameters.AddWithValue("$url",value.PublicUrl.AbsoluteUri);command.Parameters.AddWithValue("$expires",value.ExpiresAtUtc.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$received",(value.ReceivedAtUtc==default(DateTime)?DateTime.UtcNow:value.ReceivedAtUtc.ToUniversalTime()).ToString("O"));command.ExecuteNonQuery();
            }return Task.CompletedTask;
        }
        public Task<GalleryLinkCacheRecord> GetAsync(string deliverableId,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT DeliverableId,PublicUrl,ExpiresAtUtc,ReceivedAtUtc FROM GalleryLinkCache WHERE DeliverableId=$id";command.Parameters.AddWithValue("$id",deliverableId);using(var reader=command.ExecuteReader())return Task.FromResult(reader.Read()?new GalleryLinkCacheRecord{DeliverableId=reader.GetString(0),PublicUrl=new Uri(reader.GetString(1)),ExpiresAtUtc=DateTime.Parse(reader.GetString(2)).ToUniversalTime(),ReceivedAtUtc=DateTime.Parse(reader.GetString(3)).ToUniversalTime()}:null);
            }
        }
    }
}
