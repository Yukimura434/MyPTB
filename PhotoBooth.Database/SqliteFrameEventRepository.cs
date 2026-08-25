using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteFrameEventRepository : IFrameEventRepository
    {
        readonly SqliteDatabase db;
        public SqliteFrameEventRepository(SqliteDatabase db) { this.db = db; }
        public Task<IReadOnlyList<FrameEvent>> GetAllAsync(CancellationToken token)
        {
            var result = new List<FrameEvent>(); using (var c = db.OpenConnection()) using (var q = c.CreateCommand())
            { q.CommandText = "SELECT Id,Name,CreatedAtUtc FROM FrameEvents ORDER BY Name COLLATE NOCASE"; using (var r = q.ExecuteReader()) while (r.Read()) result.Add(new FrameEvent { Id = Guid.Parse(r.GetString(0)), Name = r.GetString(1), CreatedAtUtc = DateTime.Parse(r.GetString(2)).ToUniversalTime() }); }
            return Task.FromResult<IReadOnlyList<FrameEvent>>(result);
        }
        public Task SaveAsync(FrameEvent value, CancellationToken token)
        {
            using (var c = db.OpenConnection()) using (var q = c.CreateCommand()) { q.CommandText = "INSERT INTO FrameEvents(Id,Name,CreatedAtUtc) VALUES($id,$name,$created) ON CONFLICT(Id) DO UPDATE SET Name=$name"; q.Parameters.AddWithValue("$id", value.Id.ToString()); q.Parameters.AddWithValue("$name", value.Name); q.Parameters.AddWithValue("$created", value.CreatedAtUtc.ToString("O")); q.ExecuteNonQuery(); } return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken token)
        {
            using (var c = db.OpenConnection()) using (var tx = c.BeginTransaction()) { using (var f = c.CreateCommand()) { f.Transaction = tx; f.CommandText = "UPDATE Frames SET EventId=NULL WHERE EventId=$id"; f.Parameters.AddWithValue("$id", id.ToString()); f.ExecuteNonQuery(); } using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "DELETE FROM FrameEvents WHERE Id=$id"; q.Parameters.AddWithValue("$id", id.ToString()); q.ExecuteNonQuery(); } tx.Commit(); } return Task.CompletedTask;
        }
    }
}
