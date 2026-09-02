using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqlitePresetEventRepository : IPresetEventRepository
    {
        readonly SqliteDatabase database;
        public SqlitePresetEventRepository(SqliteDatabase database) { this.database = database; }

        public Task<IReadOnlyList<PresetEvent>> GetAllAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new List<PresetEvent>();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id,Name,CreatedAtUtc FROM PresetEvents ORDER BY Name COLLATE NOCASE";
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        result.Add(new PresetEvent { Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), CreatedAtUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime() });
            }
            return Task.FromResult<IReadOnlyList<PresetEvent>>(result);
        }

        public Task SaveAsync(PresetEvent value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO PresetEvents(Id,Name,CreatedAtUtc) VALUES($id,$name,$created) ON CONFLICT(Id) DO UPDATE SET Name=$name";
                command.Parameters.AddWithValue("$id", value.Id.ToString());
                command.Parameters.AddWithValue("$name", value.Name);
                command.Parameters.AddWithValue("$created", value.CreatedAtUtc.ToString("O"));
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var presets = connection.CreateCommand())
                {
                    presets.Transaction = transaction;
                    presets.CommandText = "UPDATE AdminPresets SET EventId=NULL WHERE EventId=$id";
                    presets.Parameters.AddWithValue("$id", id.ToString());
                    presets.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM PresetEvents WHERE Id=$id";
                    command.Parameters.AddWithValue("$id", id.ToString());
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }
    }
}
