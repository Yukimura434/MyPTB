using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqlitePresetRepository : IPresetRepository
    {
        public Task ReplacePinnedAsync(IReadOnlyCollection<Guid> ids, CancellationToken token)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // Clear legacy collection unions in the same commit as all pin changes.
                command.CommandText = "UPDATE AdminPresets SET IsPinned=0; UPDATE WorkflowSettings SET SelectedPresetCollections='' WHERE Id=1";
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE AdminPresets SET IsPinned=1 WHERE Id=$id";
                var parameter = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var id in ids.Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    parameter.Value = id.ToString();
                    if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Mục đã bị xóa. Vui lòng tải lại thư viện.");
                }
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        readonly SqliteDatabase database;
        public SqlitePresetRepository(SqliteDatabase database) { this.database = database; }

        public Task<IReadOnlyList<Preset>> GetAllAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<Preset>();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT p.Id,p.Name,p.FrameId,p.PrinterProfileId,p.Countdown,p.CreatedAtUtc,p.ModifiedAtUtc,
p.IsDefault,p.IsPinned,p.EventId,l.Id,l.DisplayName,l.CubeSize,l.Status,l.RowVersion,p.StrengthPercent
FROM AdminPresets p
INNER JOIN PresetColorSettings color ON color.PresetId=p.Id
INNER JOIN ColorLutAssets l ON l.Id=color.LutAssetId
ORDER BY p.Name COLLATE NOCASE,p.Id";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.Add(new Preset
                        {
                            Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), SettingsJson = null,
                            FrameId = reader.IsDBNull(2) ? (Guid?)null : Guid.Parse(reader.GetString(2)),
                            PrinterProfileId = reader.IsDBNull(3) ? (Guid?)null : Guid.Parse(reader.GetString(3)),
                            CaptureCountdownSeconds = reader.IsDBNull(4) ? 3 : reader.GetInt32(4),
                            CreatedAtUtc = ParseUtc(reader.GetString(5)), ModifiedAtUtc = ParseUtc(reader.GetString(6)),
                            IsDefault = !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                            IsPinned = !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
                            EventId = reader.IsDBNull(9) ? (Guid?)null : Guid.Parse(reader.GetString(9)),
                            LutAssetId = Guid.Parse(reader.GetString(10)), LutDisplayName = reader.GetString(11),
                            LutCubeSize = reader.GetInt32(12), LutStatus = reader.GetString(13), LutRowVersion = reader.GetInt64(14), StrengthPercent = reader.GetInt32(15)
                        });
                    }
                }
            }
            return Task.FromResult<IReadOnlyList<Preset>>(result);
        }

        public async Task<Preset> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            var values = await GetAllAsync(cancellationToken).ConfigureAwait(false);
            return values.FirstOrDefault(x => x.Id == id);
        }

        public Task SaveAsync(Preset preset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO AdminPresets
(Id,Name,SettingsJson,FrameId,PrinterProfileId,Countdown,CreatedAtUtc,ModifiedAtUtc,IsDefault,IsPinned,EventId,StrengthPercent)
VALUES($id,$name,NULL,$frame,$printer,$countdown,$created,$modified,$default,$pinned,$event,$strength)
ON CONFLICT(Id) DO UPDATE SET
Name=excluded.Name,SettingsJson=NULL,FrameId=excluded.FrameId,PrinterProfileId=excluded.PrinterProfileId,
Countdown=excluded.Countdown,CreatedAtUtc=excluded.CreatedAtUtc,ModifiedAtUtc=excluded.ModifiedAtUtc,
IsDefault=excluded.IsDefault,IsPinned=excluded.IsPinned,EventId=excluded.EventId,StrengthPercent=excluded.StrengthPercent";
                command.Parameters.AddWithValue("$strength", Math.Max(0, Math.Min(100, preset.StrengthPercent)));
                command.Parameters.AddWithValue("$id", preset.Id.ToString());
                command.Parameters.AddWithValue("$name", preset.Name);
                command.Parameters.AddWithValue("$frame", preset.FrameId.HasValue ? (object)preset.FrameId.Value.ToString() : DBNull.Value);
                command.Parameters.AddWithValue("$printer", preset.PrinterProfileId.HasValue ? (object)preset.PrinterProfileId.Value.ToString() : DBNull.Value);
                command.Parameters.AddWithValue("$countdown", preset.CaptureCountdownSeconds);
                command.Parameters.AddWithValue("$created", preset.CreatedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$modified", preset.ModifiedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$default", preset.IsDefault ? 1 : 0);
                command.Parameters.AddWithValue("$pinned", preset.IsPinned ? 1 : 0);
                command.Parameters.AddWithValue("$event", preset.EventId.HasValue ? (object)preset.EventId.Value.ToString() : DBNull.Value);
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM AdminPresets WHERE Id=$id";
                command.Parameters.AddWithValue("$id", id.ToString());
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }
}
