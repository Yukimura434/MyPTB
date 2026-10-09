using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteRevenueReportRepository : IRevenueReportRepository
    {
        readonly SqliteDatabase database;
        public SqliteRevenueReportRepository(SqliteDatabase database) { this.database=database; }

        public Task<IReadOnlyList<RevenueReport>> GetPendingAsync(int limit,CancellationToken token)
        {
            var values=new List<RevenueReport>();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"SELECT r.BoothSessionId,r.EventId,e.RemoteEventId,b.DisplayCode,r.BasePriceMinor,r.IncludedPrints,
r.PrintedCopies,r.AdditionalPrintCopies,r.AdditionalPrintUnitPriceMinor,r.GrossRevenueMinor,r.Currency,
r.OpenedAtUtc,r.CompletedAtUtc,r.AttemptCount
FROM RevenueReports r JOIN Events e ON e.Id=r.EventId JOIN BoothSessions b ON b.Id=r.BoothSessionId
WHERE r.State IN ('Pending','RetryWaiting') AND (r.NextRetryAtUtc IS NULL OR r.NextRetryAtUtc<=strftime('%Y-%m-%dT%H:%M:%fZ','now'))
ORDER BY r.CompletedAtUtc LIMIT $limit";
                command.Parameters.AddWithValue("$limit",Math.Max(1,Math.Min(100,limit)));
                using(var reader=command.ExecuteReader())while(reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    values.Add(new RevenueReport{BoothSessionId=Guid.Parse(reader.GetString(0)),EventId=Guid.Parse(reader.GetString(1)),RemoteEventId=Text(reader,2),DisplayCode=Text(reader,3),BasePriceMinor=reader.GetInt64(4),IncludedPrints=reader.GetInt32(5),PrintedCopies=reader.GetInt32(6),AdditionalPrintCopies=reader.GetInt32(7),AdditionalPrintUnitPriceMinor=reader.GetInt64(8),GrossRevenueMinor=reader.GetInt64(9),Currency=reader.GetString(10),OpenedAtUtc=Parse(reader.GetString(11)),CompletedAtUtc=Parse(reader.GetString(12)),AttemptCount=reader.GetInt32(13)});
                }
            }
            return Task.FromResult<IReadOnlyList<RevenueReport>>(values);
        }

        public Task MarkSyncedAsync(Guid boothSessionId,CancellationToken token)=>Update(boothSessionId,"Synced",null,null,token);
        public Task MarkRetryAsync(Guid boothSessionId,string error,CancellationToken token)
        {
            var next=DateTime.UtcNow.AddMinutes(5);
            return Update(boothSessionId,"RetryWaiting",error,next,token);
        }

        Task Update(Guid id,string state,string error,DateTime? next,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText="UPDATE RevenueReports SET State=$state,AttemptCount=AttemptCount+1,NextRetryAtUtc=$next,LastError=$error,UpdatedAtUtc=$now WHERE BoothSessionId=$id";
                command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$next",next.HasValue?(object)next.Value.ToString("O"):DBNull.Value);command.Parameters.AddWithValue("$error",string.IsNullOrWhiteSpace(error)?(object)DBNull.Value:error);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",id.ToString());command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }
        static string Text(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
        static DateTime Parse(string value)=>DateTime.Parse(value).ToUniversalTime();
    }
}
