using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteColorLutAssetRepository : IColorLutAssetRepository
    {
        readonly SqliteDatabase database;
        public SqliteColorLutAssetRepository(SqliteDatabase database) { this.database = database; }

        public Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var values = new List<ColorLutAsset>();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = SelectColumns + " ORDER BY DisplayName,Id";
                using (var reader = command.ExecuteReader()) while (reader.Read()) values.Add(Read(reader));
            }
            return Task.FromResult<IReadOnlyList<ColorLutAsset>>(values);
        }

        public Task<ColorLutAsset> GetAsync(Guid id, CancellationToken token) => GetOne("Id=$value", id.ToString(), token);
        public Task<ColorLutAsset> GetByHashAsync(string sha256, CancellationToken token) => GetOne("ContentHashSha256=$value", sha256, token);

        Task<ColorLutAsset> GetOne(string predicate, string value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = SelectColumns + " WHERE " + predicate + " LIMIT 1";
                command.Parameters.AddWithValue("$value", value);
                using (var reader = command.ExecuteReader()) return Task.FromResult(reader.Read() ? Read(reader) : null);
            }
        }

        public Task InsertAsync(ColorLutAsset asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO ColorLutAssets
(Id,DisplayName,RelativePath,ContentHashSha256,FileLength,CubeSize,DomainMinR,DomainMinG,DomainMinB,DomainMaxR,DomainMaxG,DomainMaxB,Status,ValidationVersion,LastValidatedAtUtc,CreatedAtUtc,ModifiedAtUtc,RowVersion)
VALUES($id,$name,$path,$hash,$length,$size,$minr,$ming,$minb,$maxr,$maxg,$maxb,$status,$validation,$validated,$created,$modified,$version)";
                Bind(command, asset, false);
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(ColorLutAsset asset, long expectedRowVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE ColorLutAssets SET
DisplayName=$name,RelativePath=$path,ContentHashSha256=$hash,FileLength=$length,CubeSize=$size,
DomainMinR=$minr,DomainMinG=$ming,DomainMinB=$minb,DomainMaxR=$maxr,DomainMaxG=$maxg,DomainMaxB=$maxb,
Status=$status,ValidationVersion=$validation,LastValidatedAtUtc=$validated,CreatedAtUtc=$created,ModifiedAtUtc=$modified,
RowVersion=RowVersion+1 WHERE Id=$id AND RowVersion=$expected";
                Bind(command, asset, true);
                command.Parameters.AddWithValue("$expected", expectedRowVersion);
                var changed = command.ExecuteNonQuery() == 1;
                if (changed) asset.RowVersion = expectedRowVersion + 1;
                return Task.FromResult(changed);
            }
        }

        public Task<int> GetUsageCountAsync(Guid id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM PresetColorSettings WHERE LutAssetId=$id";
                command.Parameters.AddWithValue("$id", id.ToString());
                return Task.FromResult(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
            }
        }

        public Task<bool> DeleteAsync(Guid id, long expectedRowVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM ColorLutAssets WHERE Id=$id AND RowVersion=$version";
                command.Parameters.AddWithValue("$id", id.ToString());
                command.Parameters.AddWithValue("$version", expectedRowVersion);
                return Task.FromResult(command.ExecuteNonQuery() == 1);
            }
        }

        static void Bind(SqliteCommand command, ColorLutAsset value, bool update)
        {
            command.Parameters.AddWithValue("$id", value.Id.ToString());
            command.Parameters.AddWithValue("$name", value.DisplayName);
            command.Parameters.AddWithValue("$path", value.RelativePath);
            command.Parameters.AddWithValue("$hash", value.ContentHashSha256);
            command.Parameters.AddWithValue("$length", value.FileLength);
            command.Parameters.AddWithValue("$size", value.CubeSize);
            command.Parameters.AddWithValue("$minr", value.DomainMinR);
            command.Parameters.AddWithValue("$ming", value.DomainMinG);
            command.Parameters.AddWithValue("$minb", value.DomainMinB);
            command.Parameters.AddWithValue("$maxr", value.DomainMaxR);
            command.Parameters.AddWithValue("$maxg", value.DomainMaxG);
            command.Parameters.AddWithValue("$maxb", value.DomainMaxB);
            command.Parameters.AddWithValue("$status", value.Status.ToString());
            command.Parameters.AddWithValue("$validation", value.ValidationVersion);
            command.Parameters.AddWithValue("$validated", value.LastValidatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$created", value.CreatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$modified", value.ModifiedAtUtc.ToString("O"));
            if (!update) command.Parameters.AddWithValue("$version", value.RowVersion);
        }

        const string SelectColumns = @"SELECT Id,DisplayName,RelativePath,ContentHashSha256,FileLength,CubeSize,
DomainMinR,DomainMinG,DomainMinB,DomainMaxR,DomainMaxG,DomainMaxB,Status,ValidationVersion,
LastValidatedAtUtc,CreatedAtUtc,ModifiedAtUtc,RowVersion FROM ColorLutAssets";

        static ColorLutAsset Read(SqliteDataReader reader) => new ColorLutAsset
        {
            Id = Guid.Parse(reader.GetString(0)), DisplayName = reader.GetString(1), RelativePath = reader.GetString(2),
            ContentHashSha256 = reader.GetString(3), FileLength = reader.GetInt64(4), CubeSize = reader.GetInt32(5),
            DomainMinR = (float)reader.GetDouble(6), DomainMinG = (float)reader.GetDouble(7), DomainMinB = (float)reader.GetDouble(8),
            DomainMaxR = (float)reader.GetDouble(9), DomainMaxG = (float)reader.GetDouble(10), DomainMaxB = (float)reader.GetDouble(11),
            Status = (ColorLutAssetStatus)Enum.Parse(typeof(ColorLutAssetStatus), reader.GetString(12), true),
            ValidationVersion = reader.GetInt32(13), LastValidatedAtUtc = ParseUtc(reader.GetString(14)),
            CreatedAtUtc = ParseUtc(reader.GetString(15)), ModifiedAtUtc = ParseUtc(reader.GetString(16)), RowVersion = reader.GetInt64(17)
        };

        static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    public sealed class SqlitePresetColorRepository : IPresetColorRepository
    {
        readonly SqliteDatabase database;
        public SqlitePresetColorRepository(SqliteDatabase database) { this.database = database; }

        public Task<PresetColorSettings> GetAsync(Guid presetId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT PresetId,LutAssetId,Strength,Enabled,ModifiedAtUtc,RowVersion FROM PresetColorSettings WHERE PresetId=$id";
                command.Parameters.AddWithValue("$id", presetId.ToString());
                using (var reader = command.ExecuteReader())
                    if (reader.Read()) return Task.FromResult(new PresetColorSettings { PresetId=Guid.Parse(reader.GetString(0)), LutAssetId=reader.IsDBNull(1)?(Guid?)null:Guid.Parse(reader.GetString(1)), Strength=(float)reader.GetDouble(2), Enabled=reader.GetInt32(3)!=0, ModifiedAtUtc=DateTime.Parse(reader.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(), RowVersion=reader.GetInt64(5) });
            }
            return Task.FromResult<PresetColorSettings>(null);
        }

        public Task SaveAsync(PresetColorSettings value, long? expectedRowVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand())
            {
                if (expectedRowVersion.HasValue)
                {
                    command.CommandText = @"UPDATE PresetColorSettings SET LutAssetId=$asset,Strength=$strength,Enabled=$enabled,ModifiedAtUtc=$modified,RowVersion=RowVersion+1 WHERE PresetId=$preset AND RowVersion=$expected";
                    command.Parameters.AddWithValue("$expected",expectedRowVersion.Value);
                }
                else command.CommandText = @"INSERT INTO PresetColorSettings(PresetId,LutAssetId,Strength,Enabled,ModifiedAtUtc,RowVersion) VALUES($preset,$asset,$strength,$enabled,$modified,1)";
                command.Parameters.AddWithValue("$preset",value.PresetId.ToString());
                command.Parameters.AddWithValue("$asset",value.LutAssetId.HasValue?(object)value.LutAssetId.Value.ToString():DBNull.Value);
                command.Parameters.AddWithValue("$strength",value.Strength);
                command.Parameters.AddWithValue("$enabled",value.Enabled?1:0);
                command.Parameters.AddWithValue("$modified",value.ModifiedAtUtc.ToString("O"));
                if (command.ExecuteNonQuery()!=1) throw new InvalidOperationException("Preset color settings were modified by another operation.");
                value.RowVersion=expectedRowVersion.GetValueOrDefault(0)+1;
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid presetId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="DELETE FROM PresetColorSettings WHERE PresetId=$id";command.Parameters.AddWithValue("$id",presetId.ToString());command.ExecuteNonQuery();}
            return Task.CompletedTask;
        }
    }
}
