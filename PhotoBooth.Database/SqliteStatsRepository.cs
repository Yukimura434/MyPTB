using System;
using System.Collections.Generic;
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
            return Task.FromResult(Count("SELECT COUNT(*) FROM CustomerSessions WHERE Kind='Booth'"));
        }

        public Task<long> CountCapturedImagesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Count("SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType IN ('Picture','Video','CompositeVideo','Original')"));
        }

        public Task<long> CountSuccessfulPrintsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Count("SELECT COUNT(*) FROM PrintJobs WHERE Status='Success'"));
        }

        public Task<DataStatisticsSnapshot> GetDataStatisticsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            {
                var today = DateTime.Today.ToUniversalTime().ToString("O");
                return Task.FromResult(new DataStatisticsSnapshot
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    BoothSessionCount = Scalar(connection, "SELECT COUNT(*) FROM BoothSessions"),
                    DeliverableCount = Scalar(connection, "SELECT COUNT(*) FROM Deliverables"),
                    PictureCount = Scalar(connection, "SELECT COUNT(*) FROM DeliverableAssets WHERE Role IN ('Picture','Original')"),
                    VideoCount = Scalar(connection, "SELECT COUNT(*) FROM DeliverableAssets WHERE Role IN ('Video','CompositeVideo')"),
                    CompositeCount = Scalar(connection, "SELECT COUNT(*) FROM DeliverableAssets WHERE Role='Composite'"),
                    GifCount = Scalar(connection, "SELECT COUNT(*) FROM DeliverableAssets WHERE Role='Gif'"),
                    ShareArchiveCount = Scalar(connection, "SELECT COUNT(*) FROM DeliverableAssets WHERE Role='ShareArchive'"),
                    ReadyAssetCount = Scalar(connection, "SELECT COUNT(*) FROM MediaAssets WHERE Status='Ready'"),
                    MissingAssetCount = Scalar(connection, "SELECT COUNT(*) FROM MediaAssets WHERE Status='Missing'"),
                    SuccessfulPrintCount = Scalar(connection, "SELECT COUNT(*) FROM PrintJobs WHERE Status='Success'"),
                    FailedPrintCount = Scalar(connection, "SELECT COUNT(*) FROM PrintJobs WHERE Status='Failed'"),
                    PendingUploadCount = Scalar(connection, "SELECT COUNT(*) FROM UploadQueue WHERE Status IN ('Pending','Uploading','RetryWaiting')"),
                    UploadedCount = Scalar(connection, "SELECT COUNT(*) FROM UploadQueue WHERE Status='Uploaded'"),
                    FailedUploadCount = Scalar(connection, "SELECT COUNT(*) FROM UploadQueue WHERE Status='PermanentFailure'"),
                    TodayBoothSessionCount = Scalar(connection, "SELECT COUNT(*) FROM BoothSessions WHERE StartedAtUtc>=$today", today),
                    TodayDeliverableCount = Scalar(connection, "SELECT COUNT(*) FROM Deliverables WHERE CreatedAtUtc>=$today", today),
                    TodayPictureCount = Scalar(connection, "SELECT COUNT(*) FROM DeliverableAssets WHERE Role IN ('Picture','Video','CompositeVideo','Original') AND CreatedAtUtc>=$today", today),
                    TodayPrintCount = Scalar(connection, "SELECT COUNT(*) FROM PrintJobs WHERE Status='Success' AND PrintedAtUtc>=$today", today),
                    TotalAssetBytes = Scalar(connection, "SELECT COALESCE(SUM(FileLength),0) FROM MediaAssets WHERE Status<>'Deleted'"),
                    RecentDeliverables = LoadRecent(connection, token)
                });
            }
        }

        public Task<CaptureLibrarySnapshot> SearchCaptureLibraryAsync(CaptureLibraryFilter filter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            filter = filter ?? new CaptureLibraryFilter();
            using (var connection = database.OpenConnection())
            {
                var where = BuildFilter(filter);
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
                    command.CommandText = @"SELECT COALESCE(SUM(p.Copies),0),
CASE WHEN COUNT(p.Id)>0 THEN COALESCE(SUM(p.Copies),0)-COUNT(DISTINCT p.CaptureId) ELSE 0 END
FROM PrintJobs p
INNER JOIN Deliverables d ON d.Id=p.CaptureId
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
LEFT JOIN Events e ON e.Id=b.EventId" + where.Sql + " AND p.Status='Success'";
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
WHERE length(trim(e.Name))>0
ORDER BY e.Name COLLATE NOCASE";
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
                command.CommandText = @"SELECT a.Id,a.DeliverableId,a.Role,a.Position,a.LocalPath,m.RelativePath,a.MimeType,a.AssetStatus
FROM DeliverableAssets a
LEFT JOIN MediaAssets m ON m.Id=a.Id AND m.Status<>'Deleted'
WHERE a.DeliverableId=$capture AND a.Role<>'ShareArchive'
ORDER BY CASE a.Role WHEN 'Picture' THEN 0 WHEN 'Composite' THEN 1 WHEN 'Video' THEN 2 WHEN 'CompositeVideo' THEN 3 WHEN 'Gif' THEN 4 ELSE 5 END,a.Position,a.Id";
                command.Parameters.AddWithValue("$capture", captureId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        values.Add(new CaptureLibraryMedia
                        {
                            AssetId = reader.GetString(0), CaptureId = reader.GetString(1), Role = reader.GetString(2), Position = reader.GetInt32(3),
                            LocalPath = Text(reader, 4), ManagedRelativePath = Text(reader, 5), MimeType = Text(reader, 6), AssetStatus = Text(reader, 7)
                        });
                    }
            }
            return Task.FromResult<IReadOnlyList<CaptureLibraryMedia>>(values);
        }

        static IReadOnlyList<CaptureLibraryItem> LoadCaptureLibrary(SqliteConnection connection, CaptureLibraryFilter filter, SqlFilter where, CancellationToken token)
        {
            var values = new List<CaptureLibraryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT d.Id,d.BoothSessionId,COALESCE(NULLIF(b.DisplayCode,''),substr(replace(d.BoothSessionId,'-',''),1,8)),
COALESCE(NULLIF(e.Name,''),'Không có sự kiện'),d.CreatedAtUtc,d.Status,COUNT(a.Id),
COALESCE(SUM(CASE WHEN a.Role IN ('Picture','Original') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN a.Role IN ('Video','CompositeVideo') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN a.Role='Gif' THEN 1 ELSE 0 END),0),
COALESCE((SELECT SUM(p.Copies) FROM PrintJobs p WHERE p.CaptureId=d.Id AND p.Status='Success'),0),
COALESCE((SELECT CASE WHEN COUNT(p.Id)>0 THEN SUM(p.Copies)-1 ELSE 0 END FROM PrintJobs p WHERE p.CaptureId=d.Id AND p.Status='Success'),0),
(SELECT da.LocalPath FROM DeliverableAssets da WHERE da.DeliverableId=d.Id AND da.Role IN ('Picture','Original','Composite','Gif') ORDER BY CASE da.Role WHEN 'Picture' THEN 0 WHEN 'Original' THEN 0 WHEN 'Composite' THEN 1 ELSE 2 END,da.Position LIMIT 1),
(SELECT ma.RelativePath FROM DeliverableAssets da LEFT JOIN MediaAssets ma ON ma.Id=da.Id AND ma.Status<>'Deleted' WHERE da.DeliverableId=d.Id AND da.Role IN ('Picture','Original','Composite','Gif') ORDER BY CASE da.Role WHEN 'Picture' THEN 0 WHEN 'Original' THEN 0 WHEN 'Composite' THEN 1 ELSE 2 END,da.Position LIMIT 1)
FROM Deliverables d
INNER JOIN BoothSessions b ON b.Id=d.BoothSessionId
LEFT JOIN Events e ON e.Id=b.EventId
LEFT JOIN DeliverableAssets a ON a.DeliverableId=d.Id" + where.Sql + @"
GROUP BY d.Id,d.BoothSessionId,b.DisplayCode,e.Name,d.CreatedAtUtc,d.Status
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
                            PrintedPhotoCount = reader.GetInt32(10), ExtraPrintCount = reader.GetInt32(11), ThumbnailPath = Text(reader, 12),
                            ThumbnailManagedRelativePath = Text(reader, 13)
                        });
                    }
            }
            return values;
        }

        static SqlFilter BuildFilter(CaptureLibraryFilter filter)
        {
            if (string.Equals(filter.Mode, CaptureLibraryFilterModes.Date, StringComparison.Ordinal))
            {
                if (!filter.FromUtc.HasValue || !filter.ToUtc.HasValue || filter.ToUtc <= filter.FromUtc) return new SqlFilter(" WHERE 1=0");
                return new SqlFilter(" WHERE d.CreatedAtUtc>=$from AND d.CreatedAtUtc<$to")
                {
                    FromUtc = filter.FromUtc.Value.ToUniversalTime().ToString("O"), ToUtc = filter.ToUtc.Value.ToUniversalTime().ToString("O")
                };
            }
            if (string.Equals(filter.Mode, CaptureLibraryFilterModes.Event, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(filter.Query)) return new SqlFilter(" WHERE 1=1");
                return new SqlFilter(" WHERE e.Name LIKE $query ESCAPE '\\' COLLATE NOCASE") { Query = "%" + EscapeLike(filter.Query.Trim()) + "%" };
            }
            if (string.Equals(filter.Mode, CaptureLibraryFilterModes.Session, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(filter.Query)) return new SqlFilter(" WHERE 1=1");
                return new SqlFilter(" WHERE (replace(d.BoothSessionId,'-','') LIKE $session ESCAPE '\\' OR b.DisplayCode LIKE $display ESCAPE '\\' COLLATE NOCASE)")
                {
                    SessionQuery = EscapeLike(filter.Query.Trim().Replace("-", string.Empty)) + "%", DisplayQuery = "%" + EscapeLike(filter.Query.Trim()) + "%"
                };
            }
            return new SqlFilter(" WHERE 1=1");
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
            if (filter.FromUtc != null) command.Parameters.AddWithValue("$from", filter.FromUtc);
            if (filter.ToUtc != null) command.Parameters.AddWithValue("$to", filter.ToUtc);
            if (filter.Query != null) command.Parameters.AddWithValue("$query", filter.Query);
            if (filter.SessionQuery != null) command.Parameters.AddWithValue("$session", filter.SessionQuery);
            if (filter.DisplayQuery != null) command.Parameters.AddWithValue("$display", filter.DisplayQuery);
        }

        static IReadOnlyList<RecentDeliverableStatistics> LoadRecent(SqliteConnection connection, CancellationToken token)
        {
            var values = new List<RecentDeliverableStatistics>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT d.Id,d.BoothSessionId,COALESCE(e.Name,'—'),d.CreatedAtUtc,d.Status,COUNT(a.Id),SUM(CASE WHEN a.Role='Video' THEN 1 ELSE 0 END),SUM(CASE WHEN a.Role='Gif' THEN 1 ELSE 0 END),(SELECT COUNT(*) FROM PrintJobs p WHERE p.CaptureId=d.Id),SUM(CASE WHEN a.AssetStatus='Missing' THEN 1 ELSE 0 END),MAX(CASE WHEN a.Role='ShareArchive' THEN 1 ELSE 0 END) FROM Deliverables d LEFT JOIN BoothSessions b ON b.Id=d.BoothSessionId LEFT JOIN Events e ON e.Id=b.EventId LEFT JOIN DeliverableAssets a ON a.DeliverableId=d.Id GROUP BY d.Id,d.BoothSessionId,e.Name,d.CreatedAtUtc,d.Status ORDER BY d.CreatedAtUtc DESC LIMIT 50";
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

        long Count(string sql) { using (var connection = database.OpenConnection()) return Scalar(connection, sql); }

        static long Scalar(SqliteConnection connection, string sql, string today = null)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                if (today != null) command.Parameters.AddWithValue("$today", today);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        static DateTime Parse(string value) => DateTime.Parse(value).ToUniversalTime();

        sealed class SqlFilter
        {
            public SqlFilter(string sql) { Sql = sql; }
            public string Sql { get; }
            public string FromUtc { get; set; }
            public string ToUtc { get; set; }
            public string Query { get; set; }
            public string SessionQuery { get; set; }
            public string DisplayQuery { get; set; }
        }
    }
}
