using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteStatsRepository : IStatsRepository
    {
        readonly SqliteDatabase database;

        public SqliteStatsRepository(SqliteDatabase database) { this.database = database; }

        public Task<long> CountSessionsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(CountForCurrentDevice("SELECT COUNT(*) FROM BoothSessions b WHERE b.OriginDeviceId=$device"));
        }

        public Task<long> CountCapturedImagesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(CountForCurrentDevice(@"SELECT COUNT(*) FROM DeliverableAssets a
INNER JOIN Deliverables d ON d.Id=a.DeliverableId
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
WHERE b.OriginDeviceId=$device AND a.Role IN ('Picture','Video','CompositeVideo')"));
        }

        public Task<long> CountSuccessfulPrintsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(CountForCurrentDevice(@"SELECT COUNT(*) FROM PrintJobs p
INNER JOIN BoothSessions b ON b.Id=p.BoothSessionId
WHERE b.OriginDeviceId=$device AND p.Outcome='Success'"));
        }

        public Task<DataStatisticsSnapshot> GetDataStatisticsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            {
                var today = DateTime.Today.ToUniversalTime().ToString("O");
                var device = CurrentDeviceId(connection);
                return Task.FromResult(new DataStatisticsSnapshot
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    BoothSessionCount = Scalar(connection, "SELECT COUNT(*) FROM BoothSessions b WHERE b.OriginDeviceId=$device", device),
                    DeliverableCount = Scalar(connection, "SELECT COUNT(*) FROM Deliverables d INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId WHERE b.OriginDeviceId=$device", device),
                    PictureCount = AssetCount(connection, device, "('Picture','Original')"),
                    VideoCount = AssetCount(connection, device, "('Video','CompositeVideo')"),
                    CompositeCount = AssetCount(connection, device, "('Composite')"),
                    GifCount = AssetCount(connection, device, "('Gif')"),
                    ShareArchiveCount = AssetCount(connection, device, "('ShareArchive')"),
                    ReadyAssetCount = Scalar(connection, "SELECT COUNT(*) FROM MediaAssets m INNER JOIN BoothSessions b ON b.Id=m.BoothSessionId WHERE b.OriginDeviceId=$device AND m.State='Ready'", device),
                    MissingAssetCount = Scalar(connection, "SELECT COUNT(*) FROM MediaAssets m INNER JOIN BoothSessions b ON b.Id=m.BoothSessionId WHERE b.OriginDeviceId=$device AND m.State='Missing'", device),
                    SuccessfulPrintCount = PrintCount(connection, device, "Success"),
                    FailedPrintCount = PrintCount(connection, device, "Failed"),
                    PendingUploadCount = Scalar(connection, "SELECT COUNT(*) FROM OutputJobs j INNER JOIN BoothSessions b ON b.Id=j.BoothSessionId WHERE b.OriginDeviceId=$device AND j.JobType IN ('UploadAsset','CommitDeliverable','CreateGallery') AND j.State IN ('Pending','Leased','Submitting','Submitted','RetryWaiting')", device),
                    UploadedCount = Scalar(connection, "SELECT COUNT(*) FROM OutputJobs j INNER JOIN BoothSessions b ON b.Id=j.BoothSessionId WHERE b.OriginDeviceId=$device AND j.JobType='UploadAsset' AND j.State='Completed'", device),
                    FailedUploadCount = Scalar(connection, "SELECT COUNT(*) FROM OutputJobs j INNER JOIN BoothSessions b ON b.Id=j.BoothSessionId WHERE b.OriginDeviceId=$device AND j.JobType IN ('UploadAsset','CommitDeliverable','CreateGallery') AND j.State='PermanentFailure'", device),
                    TodayBoothSessionCount = Scalar(connection, "SELECT COUNT(*) FROM BoothSessions b WHERE b.OriginDeviceId=$device AND b.StartedAtUtc>=$today", device, today),
                    TodayDeliverableCount = Scalar(connection, "SELECT COUNT(*) FROM Deliverables d INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId WHERE b.OriginDeviceId=$device AND d.CreatedAtUtc>=$today", device, today),
                    TodayPictureCount = Scalar(connection, "SELECT COUNT(*) FROM DeliverableAssets a INNER JOIN MediaAssets m ON m.Id=a.MediaAssetId INNER JOIN BoothSessions b ON b.Id=m.BoothSessionId WHERE b.OriginDeviceId=$device AND a.Role IN ('Picture','Video','CompositeVideo') AND m.CreatedAtUtc>=$today", device, today),
                    TodayPrintCount = Scalar(connection, "SELECT COUNT(*) FROM PrintJobs p INNER JOIN BoothSessions b ON b.Id=p.BoothSessionId WHERE b.OriginDeviceId=$device AND p.Outcome='Success' AND p.RequestedAtUtc>=$today", device, today),
                    TotalAssetBytes = Scalar(connection, "SELECT COALESCE(SUM(m.FileLength),0) FROM MediaAssets m INNER JOIN BoothSessions b ON b.Id=m.BoothSessionId WHERE b.OriginDeviceId=$device AND m.State<>'Deleted'", device),
                    RecentDeliverables = LoadRecent(connection, device, token)
                });
            }
        }

        public Task<CaptureLibrarySnapshot> SearchCaptureLibraryAsync(CaptureLibraryFilter filter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            filter = filter ?? new CaptureLibraryFilter();
            using (var connection = database.OpenConnection())
            {
                var where = BuildFilter(filter, CurrentDeviceId(connection));
                var result = new CaptureLibrarySnapshot
                {
                    CaptureCount = FilteredScalar(connection,
                        "SELECT COUNT(*) FROM Deliverables d INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId LEFT JOIN Events e ON e.Id=b.EventId" + where.Sql,
                        where),
                    Captures = LoadCaptureLibrary(connection, filter, where, token),
                    HasRevenueData = false,
                    RevenueAmount = 0m
                };

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT
COALESCE(SUM(CASE WHEN d.State='GalleryReady' THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN d.State NOT IN ('GalleryReady','Failed') AND NOT EXISTS(SELECT 1 FROM OutputJobs failed WHERE failed.DeliverableId=d.Id AND failed.State='PermanentFailure') AND EXISTS(SELECT 1 FROM OutputJobs j WHERE j.DeliverableId=d.Id AND j.State IN ('Pending','Leased','Submitting','Submitted','RetryWaiting','UnknownOutcome')) THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN d.State='Failed' OR EXISTS(SELECT 1 FROM OutputJobs j WHERE j.DeliverableId=d.Id AND j.State='PermanentFailure') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN d.State<>'GalleryReady' AND NOT EXISTS(SELECT 1 FROM OutputJobs j WHERE j.DeliverableId=d.Id AND j.State NOT IN ('Completed','Cancelled')) THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN EXISTS(SELECT 1 FROM OutputJobs j WHERE j.DeliverableId=d.Id AND j.State IN ('RetryWaiting','UnknownOutcome','PermanentFailure')) THEN 1 ELSE 0 END),0)
FROM Deliverables d
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
LEFT JOIN Events e ON e.Id=b.EventId" + where.Sql;
                    BindFilter(command, where);
                    using(var reader=command.ExecuteReader())if(reader.Read())
                    {
                        result.ReadyShareCount=reader.GetInt64(0);
                        result.PendingShareCount=reader.GetInt64(1);
                        result.FailedShareCount=reader.GetInt64(2);
                        result.LocalOnlyShareCount=reader.GetInt64(3);
                        result.RecoverableShareCount=reader.GetInt64(4);
                    }
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COALESCE(SUM(p.Copies),0),
CASE WHEN COUNT(p.Id)>0 THEN COALESCE(SUM(p.Copies),0)-COUNT(DISTINCT p.DeliverableId) ELSE 0 END
FROM PrintJobs p
INNER JOIN Deliverables d ON d.Id=p.DeliverableId
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
LEFT JOIN Events e ON e.Id=b.EventId" + where.Sql + " AND p.Outcome='Success'";
                    BindFilter(command, where);
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            result.PrintedPhotoCount = reader.GetInt64(0);
                            result.ExtraPrintCount = reader.GetInt64(1);
                        }
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(r.BoothSessionId),COALESCE(SUM(r.GrossRevenueMinor),0)
FROM RevenueReports r WHERE EXISTS(
 SELECT 1 FROM BoothSessions b INNER JOIN Deliverables d ON d.BoothSessionId=b.Id
 LEFT JOIN Events e ON e.Id=b.EventId" + where.Sql + " AND b.Id=r.BoothSessionId)";
                    BindFilter(command, where);
                    using(var reader=command.ExecuteReader())if(reader.Read())
                    {
                        result.HasRevenueData=reader.GetInt64(0)>0;
                        result.RevenueAmount=reader.GetDecimal(1);
                    }
                }
                return Task.FromResult(result);
            }
        }

        public Task<IReadOnlyList<string>> GetEventSuggestionsAsync(CancellationToken token)
        {
            var values = new List<string>();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT DISTINCT e.Name
FROM Events e
INNER JOIN BoothSessions b ON b.EventId=e.Id
INNER JOIN Deliverables d ON d.BoothSessionId=b.Id
WHERE b.OriginDeviceId=$device AND length(trim(e.Name))>0
ORDER BY e.Name COLLATE NOCASE";
                command.Parameters.AddWithValue("$device", CurrentDeviceId(connection));
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        values.Add(reader.GetString(0));
                    }
            }
            return Task.FromResult<IReadOnlyList<string>>(values);
        }

        public Task<IReadOnlyList<CaptureLibraryMedia>> GetCaptureMediaAsync(string captureId, CancellationToken token)
        {
            var values = new List<CaptureLibraryMedia>();
            if (string.IsNullOrWhiteSpace(captureId)) return Task.FromResult<IReadOnlyList<CaptureLibraryMedia>>(values);
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT m.Id,a.DeliverableId,a.Role,a.Position,m.RelativePath,m.MimeType,m.State
FROM DeliverableAssets a
INNER JOIN Deliverables d ON d.Id=a.DeliverableId
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
INNER JOIN MediaAssets m ON m.Id=a.MediaAssetId AND m.State<>'Deleted'
WHERE b.OriginDeviceId=$device AND a.DeliverableId=$capture AND a.Role<>'ShareArchive'
ORDER BY CASE a.Role WHEN 'Picture' THEN 0 WHEN 'Composite' THEN 1 WHEN 'Video' THEN 2 WHEN 'CompositeVideo' THEN 3 WHEN 'Gif' THEN 4 ELSE 5 END,a.Position,m.Id";
                command.Parameters.AddWithValue("$capture", captureId);
                command.Parameters.AddWithValue("$device", CurrentDeviceId(connection));
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        values.Add(new CaptureLibraryMedia
                        {
                            AssetId = reader.GetString(0), CaptureId = reader.GetString(1), Role = reader.GetString(2), Position = reader.GetInt32(3),
                            LocalPath = Full(reader.GetString(4)), ManagedRelativePath = reader.GetString(4), MimeType = reader.GetString(5), AssetStatus = reader.GetString(6)
                        });
                    }
            }
            return Task.FromResult<IReadOnlyList<CaptureLibraryMedia>>(values);
        }

        IReadOnlyList<CaptureLibraryItem> LoadCaptureLibrary(SqliteConnection connection, CaptureLibraryFilter filter, SqlFilter where, CancellationToken token)
        {
            var values = new List<CaptureLibraryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT d.Id,d.BoothSessionId,COALESCE(NULLIF(b.DisplayCode,''),substr(replace(d.BoothSessionId,'-',''),1,8)),
 COALESCE(NULLIF(e.Name,''),'Không có sự kiện'),d.CreatedAtUtc,d.State,COUNT(a.MediaAssetId),
COALESCE(SUM(CASE WHEN a.Role IN ('Picture','Original') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN a.Role IN ('Video','CompositeVideo') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN a.Role='Gif' THEN 1 ELSE 0 END),0),
 COALESCE((SELECT SUM(p.Copies) FROM PrintJobs p WHERE p.DeliverableId=d.Id AND p.Outcome='Success'),0),
 COALESCE((SELECT CASE WHEN COUNT(p.Id)>0 THEN SUM(p.Copies)-1 ELSE 0 END FROM PrintJobs p WHERE p.DeliverableId=d.Id AND p.Outcome='Success'),0),
 COALESCE((SELECT r.GrossRevenueMinor FROM RevenueReports r WHERE r.BoothSessionId=d.BoothSessionId),0),
 (SELECT ma.RelativePath FROM DeliverableAssets da INNER JOIN MediaAssets ma ON ma.Id=da.MediaAssetId AND ma.State<>'Deleted' WHERE da.DeliverableId=d.Id AND da.Role IN ('Picture','Composite','Gif') ORDER BY CASE da.Role WHEN 'Picture' THEN 0 WHEN 'Composite' THEN 1 ELSE 2 END,da.Position LIMIT 1),
 (SELECT COUNT(*) FROM OutputJobs j WHERE j.DeliverableId=d.Id AND j.State IN ('Pending','Leased','Submitting','Submitted','RetryWaiting','UnknownOutcome')),
 (SELECT COUNT(*) FROM OutputJobs j WHERE j.DeliverableId=d.Id AND j.State='PermanentFailure'),
 (SELECT j.LastError FROM OutputJobs j WHERE j.DeliverableId=d.Id AND j.LastError IS NOT NULL ORDER BY j.UpdatedAtUtc DESC LIMIT 1),
 (SELECT g.PublicUrl FROM GalleryLinkCache g WHERE g.DeliverableId=d.Id)
FROM Deliverables d
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
LEFT JOIN Events e ON e.Id=b.EventId
LEFT JOIN DeliverableAssets a ON a.DeliverableId=d.Id" + where.Sql + @"
GROUP BY d.Id,d.BoothSessionId,b.DisplayCode,e.Name,d.CreatedAtUtc,d.State
ORDER BY d.CreatedAtUtc DESC LIMIT $limit";
                BindFilter(command, where);
                command.Parameters.AddWithValue("$limit", Math.Max(1, Math.Min(500, filter.MaximumItems)));
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        values.Add(new CaptureLibraryItem
                        {
                            CaptureId = reader.GetString(0), SessionId = Guid.Parse(reader.GetString(1)), SessionDisplayCode = reader.GetString(2),
                            EventName = reader.GetString(3), CreatedAtUtc = Parse(reader.GetString(4)), Status = reader.GetString(5), AssetCount = reader.GetInt32(6),
                            PictureCount = reader.GetInt32(7), VideoCount = reader.GetInt32(8), GifCount = reader.GetInt32(9),
                             PrintedPhotoCount = reader.GetInt32(10), ExtraPrintCount = reader.GetInt32(11), RevenueAmount=reader.GetDecimal(12),ThumbnailPath = reader.IsDBNull(13)?null:Full(reader.GetString(13)),
                             ThumbnailManagedRelativePath = Text(reader, 13), PendingSyncCount=reader.GetInt32(14), FailedSyncCount=reader.GetInt32(15),
                             LastSyncError=Text(reader,16), GalleryUrl=Text(reader,17)
                        });
                    }
            }
            return values;
        }

        static SqlFilter BuildFilter(CaptureLibraryFilter filter, string deviceId)
        {
            if (string.Equals(filter.Mode, CaptureLibraryFilterModes.Date, StringComparison.Ordinal))
            {
                if (!filter.FromUtc.HasValue || !filter.ToUtc.HasValue || filter.ToUtc <= filter.FromUtc) return new SqlFilter(" WHERE 1=0", deviceId);
                return new SqlFilter(" WHERE b.OriginDeviceId=$device AND d.CreatedAtUtc>=$from AND d.CreatedAtUtc<$to", deviceId)
                {
                    FromUtc = filter.FromUtc.Value.ToUniversalTime().ToString("O"), ToUtc = filter.ToUtc.Value.ToUniversalTime().ToString("O")
                };
            }
            if (string.Equals(filter.Mode, CaptureLibraryFilterModes.Event, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(filter.Query)) return new SqlFilter(" WHERE b.OriginDeviceId=$device", deviceId);
                return new SqlFilter(" WHERE b.OriginDeviceId=$device AND e.Name LIKE $query ESCAPE '\\' COLLATE NOCASE", deviceId) { Query = "%" + EscapeLike(filter.Query.Trim()) + "%" };
            }
            if (string.Equals(filter.Mode, CaptureLibraryFilterModes.Session, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(filter.Query)) return new SqlFilter(" WHERE b.OriginDeviceId=$device", deviceId);
                return new SqlFilter(" WHERE b.OriginDeviceId=$device AND (replace(d.BoothSessionId,'-','') LIKE $session ESCAPE '\\' OR b.DisplayCode LIKE $display ESCAPE '\\' COLLATE NOCASE)", deviceId)
                {
                    SessionQuery = EscapeLike(filter.Query.Trim().Replace("-", string.Empty)) + "%", DisplayQuery = "%" + EscapeLike(filter.Query.Trim()) + "%"
                };
            }
            return new SqlFilter(" WHERE b.OriginDeviceId=$device", deviceId);
        }

        static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        static long FilteredScalar(SqliteConnection connection, string sql, SqlFilter filter)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                BindFilter(command, filter);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        static void BindFilter(SqliteCommand command, SqlFilter filter)
        {
            command.Parameters.AddWithValue("$device", filter.DeviceId ?? string.Empty);
            if (filter.FromUtc != null) command.Parameters.AddWithValue("$from", filter.FromUtc);
            if (filter.ToUtc != null) command.Parameters.AddWithValue("$to", filter.ToUtc);
            if (filter.Query != null) command.Parameters.AddWithValue("$query", filter.Query);
            if (filter.SessionQuery != null) command.Parameters.AddWithValue("$session", filter.SessionQuery);
            if (filter.DisplayQuery != null) command.Parameters.AddWithValue("$display", filter.DisplayQuery);
        }

        static IReadOnlyList<RecentDeliverableStatistics> LoadRecent(SqliteConnection connection, string deviceId, CancellationToken token)
        {
            var values = new List<RecentDeliverableStatistics>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT d.Id,d.BoothSessionId,COALESCE(e.Name,'—'),d.CreatedAtUtc,d.State,COUNT(a.MediaAssetId),SUM(CASE WHEN a.Role='Video' THEN 1 ELSE 0 END),SUM(CASE WHEN a.Role='Gif' THEN 1 ELSE 0 END),(SELECT COUNT(*) FROM PrintJobs p WHERE p.DeliverableId=d.Id),SUM(CASE WHEN m.State='Missing' THEN 1 ELSE 0 END),MAX(CASE WHEN a.Role='ShareArchive' THEN 1 ELSE 0 END) FROM Deliverables d INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId LEFT JOIN Events e ON e.Id=b.EventId LEFT JOIN DeliverableAssets a ON a.DeliverableId=d.Id LEFT JOIN MediaAssets m ON m.Id=a.MediaAssetId WHERE b.OriginDeviceId=$device GROUP BY d.Id,d.BoothSessionId,e.Name,d.CreatedAtUtc,d.State ORDER BY d.CreatedAtUtc DESC LIMIT 50";
                command.Parameters.AddWithValue("$device", deviceId ?? string.Empty);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        values.Add(new RecentDeliverableStatistics
                        {
                            DeliverableId = reader.GetString(0), BoothSessionId = Guid.Parse(reader.GetString(1)), EventName = reader.GetString(2),
                            CreatedAtUtc = Parse(reader.GetString(3)), Status = reader.GetString(4), AssetCount = reader.GetInt32(5),
                            VideoCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6), GifCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                            PrintCount = reader.GetInt32(8), MissingAssetCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                            HasShareArchive = !reader.IsDBNull(10) && reader.GetInt32(10) > 0
                        });
                    }
            }
            return values;
        }

        long CountForCurrentDevice(string sql)
        {
            using (var connection = database.OpenConnection())
                return Scalar(connection, sql, CurrentDeviceId(connection));
        }

        string Full(string relative)
        {
            if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))return relative;
            var root=database.DataRoot.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var full=Path.GetFullPath(Path.Combine(database.DataRoot,relative.Replace('/',Path.DirectorySeparatorChar)));
            return full.StartsWith(root,StringComparison.OrdinalIgnoreCase)?full:null;
        }

        static long AssetCount(SqliteConnection connection, string deviceId, string roles)
        {
            return Scalar(connection, @"SELECT COUNT(*) FROM DeliverableAssets a
INNER JOIN Deliverables d ON d.Id=a.DeliverableId
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
WHERE b.OriginDeviceId=$device AND a.Role IN " + roles, deviceId);
        }

        static long PrintCount(SqliteConnection connection, string deviceId, string outcome)
        {
            using(var command=connection.CreateCommand())
            {
                command.CommandText=@"SELECT COUNT(*) FROM PrintJobs p
INNER JOIN BoothSessions b ON b.Id=p.BoothSessionId
WHERE b.OriginDeviceId=$device AND p.Outcome=$outcome";
                command.Parameters.AddWithValue("$device", deviceId ?? string.Empty);
                command.Parameters.AddWithValue("$outcome", outcome);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        static string CurrentDeviceId(SqliteConnection connection)
        {
            using(var command=connection.CreateCommand())
            {
                command.CommandText=@"SELECT p.DeviceId FROM AppState a
INNER JOIN DeviceProfiles p ON p.Id=a.ActiveProfileId
WHERE a.Id=1 AND p.EnrollmentState='Enrolled' LIMIT 1";
                var current=command.ExecuteScalar() as string;
                if(!string.IsNullOrWhiteSpace(current))return current;
                command.CommandText="SELECT COUNT(*) FROM DeviceProfiles WHERE EnrollmentState='Enrolled'";
                return Convert.ToInt32(command.ExecuteScalar())==0?"local-unenrolled":string.Empty;
            }
        }

        static long Scalar(SqliteConnection connection, string sql, string deviceId, string today = null)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.AddWithValue("$device", deviceId ?? string.Empty);
                if (today != null) command.Parameters.AddWithValue("$today", today);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        static DateTime Parse(string value) => DateTime.Parse(value).ToUniversalTime();

        sealed class SqlFilter
        {
            public SqlFilter(string sql, string deviceId) { Sql = sql; DeviceId = deviceId; }
            public string Sql { get; }
            public string DeviceId { get; }
            public string FromUtc { get; set; }
            public string ToUtc { get; set; }
            public string Query { get; set; }
            public string SessionQuery { get; set; }
            public string DisplayQuery { get; set; }
        }
    }
}
