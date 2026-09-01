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
(Id,SessionId,Sequence,AttemptNumber,CameraId,PictureAssetId,VideoAssetId,Status,IntentAtUtc,CompletedAtUtc,LastError)
VALUES($id,$session,$sequence,$attempt,$camera,$picture,$video,$status,$intent,NULL,NULL)";
                command.Parameters.AddWithValue("$id", value.Id);
                command.Parameters.AddWithValue("$session", value.SessionId.ToString());
                command.Parameters.AddWithValue("$sequence", value.Sequence);
                command.Parameters.AddWithValue("$attempt", Math.Max(1, value.AttemptNumber));
                command.Parameters.AddWithValue("$camera", Db(value.CameraId));
                command.Parameters.AddWithValue("$picture", value.PictureAssetId);
                command.Parameters.AddWithValue("$video", Db(value.VideoAssetId));
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
                command.CommandText = "UPDATE CaptureAttempts SET Status=$status,CompletedAtUtc=$completed,LastError=$error WHERE Id=$id";
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
                command.CommandText = "SELECT Id,SessionId,Sequence,AttemptNumber,CameraId,PictureAssetId,VideoAssetId,Status,IntentAtUtc,CompletedAtUtc,LastError FROM CaptureAttempts WHERE Status IN ('IntentRecorded','Unknown') ORDER BY IntentAtUtc";
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
            CameraId = Text(reader, 4), PictureAssetId = reader.GetString(5), VideoAssetId = Text(reader, 6), Status = reader.GetString(7),
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
(Id,SessionId,CaptureAttemptId,Kind,RelativePath,MimeType,FileLength,ContentHashSha256,Status,RetentionClass,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$session,$attempt,$kind,$path,$mime,$length,$hash,$status,$retention,$created,$updated)
ON CONFLICT(Id) DO UPDATE SET RelativePath=excluded.RelativePath,MimeType=excluded.MimeType,FileLength=excluded.FileLength,
ContentHashSha256=COALESCE(excluded.ContentHashSha256,MediaAssets.ContentHashSha256),Status=excluded.Status,RetentionClass=excluded.RetentionClass,UpdatedAtUtc=excluded.UpdatedAtUtc";
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
                command.CommandText = Select + " WHERE SessionId=$session ORDER BY CreatedAtUtc,Id";
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
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM OutputJobs WHERE SessionId=$session AND State NOT IN ('Completed','PermanentFailure','Cancelled'))";
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
                command.CommandText = "UPDATE MediaAssets SET Status='Deleted',UpdatedAtUtc=$updated WHERE SessionId=$session AND Status<>'Deleted'";
                command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$session", sessionId.ToString());
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task MarkDeletedAsync(string assetId,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(assetId))return Task.CompletedTask;using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="UPDATE MediaAssets SET Status='Deleted',UpdatedAtUtc=$updated WHERE Id=$id";command.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",assetId);command.ExecuteNonQuery();}return Task.CompletedTask;
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

        const string Select = "SELECT Id,SessionId,CaptureAttemptId,Kind,RelativePath,MimeType,FileLength,ContentHashSha256,Status,RetentionClass,CreatedAtUtc,UpdatedAtUtc FROM MediaAssets";
        static MediaAssetRecord Read(SqliteDataReader reader) => new MediaAssetRecord
        {
            Id=reader.GetString(0),SessionId=Guid.Parse(reader.GetString(1)),CaptureAttemptId=Text(reader,2),Kind=reader.GetString(3),RelativePath=reader.GetString(4),MimeType=reader.GetString(5),FileLength=reader.GetInt64(6),ContentHashSha256=Text(reader,7),Status=reader.GetString(8),RetentionClass=reader.GetString(9),CreatedAtUtc=DateTime.Parse(reader.GetString(10)).ToUniversalTime(),UpdatedAtUtc=DateTime.Parse(reader.GetString(11)).ToUniversalTime()
        };
        static object Db(string value) => string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    public sealed class SqliteDurableOutputJobRepository : IDurableOutputJobRepository
    {
        readonly SqliteDatabase database;
        public SqliteDurableOutputJobRepository(SqliteDatabase value){database=value;}
        public Task<DurableOutputJobRecord> CreateIntentAsync(DurableOutputJobRecord value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var now=value.CreatedAtUtc==default(DateTime)?DateTime.UtcNow:value.CreatedAtUtc.ToUniversalTime();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"INSERT OR IGNORE INTO OutputJobs(Id,SessionId,AssetId,JobType,IdempotencyKey,State,AttemptCount,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$session,$asset,$type,$key,$state,0,$created,$updated)";
                command.Parameters.AddWithValue("$id",value.Id);command.Parameters.AddWithValue("$session",value.SessionId.ToString());command.Parameters.AddWithValue("$asset",value.AssetId);command.Parameters.AddWithValue("$type",value.JobType);command.Parameters.AddWithValue("$key",value.IdempotencyKey);command.Parameters.AddWithValue("$state",DurableOutputJobStates.Pending);command.Parameters.AddWithValue("$created",now.ToString("O"));command.Parameters.AddWithValue("$updated",now.ToString("O"));command.ExecuteNonQuery();
                using(var read=connection.CreateCommand()){read.CommandText="SELECT Id,SessionId,AssetId,JobType,IdempotencyKey,State,AttemptCount,LastError,CreatedAtUtc,UpdatedAtUtc FROM OutputJobs WHERE IdempotencyKey=$key";read.Parameters.AddWithValue("$key",value.IdempotencyKey);using(var reader=read.ExecuteReader()){if(!reader.Read())throw new InvalidOperationException("Durable output intent was not stored.");return Task.FromResult(new DurableOutputJobRecord{Id=reader.GetString(0),SessionId=Guid.Parse(reader.GetString(1)),AssetId=reader.GetString(2),JobType=reader.GetString(3),IdempotencyKey=reader.GetString(4),State=reader.GetString(5),AttemptCount=reader.GetInt32(6),LastError=reader.IsDBNull(7)?null:reader.GetString(7),CreatedAtUtc=DateTime.Parse(reader.GetString(8)).ToUniversalTime(),UpdatedAtUtc=DateTime.Parse(reader.GetString(9)).ToUniversalTime()});}}
            }
        }
        public Task SetStateAsync(string jobId,string state,string error,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="UPDATE OutputJobs SET State=$state,AttemptCount=CASE WHEN $state='Submitting' THEN AttemptCount+1 ELSE AttemptCount END,LastError=$error,UpdatedAtUtc=$updated WHERE Id=$id";command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$error",string.IsNullOrWhiteSpace(error)?(object)DBNull.Value:error);command.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",jobId);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Durable output job was not found.");}return Task.CompletedTask;
        }
        public Task ReconcileInterruptedAsync(CancellationToken token){token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText=@"UPDATE OutputJobs SET State='UnknownOutcome',LastError='Application stopped while an external output operation was being submitted.',LeaseId=NULL,LeaseExpiresAtUtc=NULL,UpdatedAtUtc=$now WHERE State IN ('Submitting','Submitted');
UPDATE OutputJobs SET State='RetryWaiting',LeaseId=NULL,LeaseExpiresAtUtc=NULL,NextRetryAtUtc=$now,UpdatedAtUtc=$now WHERE State='Leased';";command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();}return Task.CompletedTask;}
    }
}
