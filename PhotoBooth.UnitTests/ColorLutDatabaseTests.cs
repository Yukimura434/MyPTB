using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class ColorLutDatabaseTests
    {
        [Fact]
        public async System.Threading.Tasks.Task Color_asset_constraints_relations_and_row_version_are_enforced()
        {
            var directory=Path.Combine(Path.GetTempPath(),"photobooth-color-db-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var database=new SqliteDatabase(Path.Combine(directory,"test.db"));database.Initialize();
                using(var schema=database.OpenConnection())
                {
                    using(var migration=schema.CreateCommand()){migration.CommandText="SELECT COUNT(*) FROM SchemaMigrations WHERE Version=14 AND Name='single_lut_preset_library_and_events'";Assert.Equal(1,Convert.ToInt32(migration.ExecuteScalar()));}
                    using(var columns=schema.CreateCommand()){columns.CommandText="SELECT COUNT(*) FROM pragma_table_info('PresetColorSettings') WHERE name IN ('Strength','Enabled')";Assert.Equal(0,Convert.ToInt32(columns.ExecuteScalar()));}
                }
                var assets=new SqliteColorLutAssetRepository(database);
                var presetColors=new SqlitePresetColorRepository(database);
                var presetId=Guid.NewGuid();
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText="INSERT INTO AdminPresets(Id,Name,Countdown,CreatedAtUtc,ModifiedAtUtc,IsDefault) VALUES($id,'Test',3,$now,$now,0)";
                    command.Parameters.AddWithValue("$id",presetId.ToString());command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
                }
                var asset=NewAsset();await assets.InsertAsync(asset,CancellationToken.None);
                await presetColors.SaveAsync(new PresetColorSettings{PresetId=presetId,LutAssetId=asset.Id,ModifiedAtUtc=DateTime.UtcNow},null,CancellationToken.None);
                Assert.Equal(1,await assets.GetUsageCountAsync(asset.Id,CancellationToken.None));
                await Assert.ThrowsAsync<SqliteException>(()=>assets.DeleteAsync(asset.Id,asset.RowVersion,CancellationToken.None));
                asset.DisplayName="Changed";
                Assert.True(await assets.UpdateAsync(asset,1,CancellationToken.None));
                Assert.Equal(2,asset.RowVersion);
                Assert.False(await assets.UpdateAsync(asset,1,CancellationToken.None));
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="DELETE FROM AdminPresets WHERE Id=$id";command.Parameters.AddWithValue("$id",presetId.ToString());command.ExecuteNonQuery();}
                Assert.Null(await presetColors.GetAsync(presetId,CancellationToken.None));
                Assert.True(await assets.DeleteAsync(asset.Id,2,CancellationToken.None));
            }
            finally{try{Directory.Delete(directory,true);}catch{}}
        }

        [Fact]
        public async System.Threading.Tasks.Task Duplicate_hash_and_cube_above_hard_limit_are_rejected()
        {
            var directory=Path.Combine(Path.GetTempPath(),"photobooth-color-db-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                var database=new SqliteDatabase(Path.Combine(directory,"test.db"));database.Initialize();var repo=new SqliteColorLutAssetRepository(database);
                var first=NewAsset();await repo.InsertAsync(first,CancellationToken.None);
                var duplicate=NewAsset();duplicate.RelativePath="Assets/Presets/Cubes/other.cube";
                await Assert.ThrowsAsync<SqliteException>(()=>repo.InsertAsync(duplicate,CancellationToken.None));
                var tooLarge=NewAsset();tooLarge.ContentHashSha256=new string('b',64);tooLarge.RelativePath="Assets/Presets/Cubes/large.cube";tooLarge.CubeSize=129;
                await Assert.ThrowsAsync<SqliteException>(()=>repo.InsertAsync(tooLarge,CancellationToken.None));
            }
            finally{try{Directory.Delete(directory,true);}catch{}}
        }

        [Fact]
        public async System.Threading.Tasks.Task Legacy_preset_color_data_is_migrated_without_persisted_strength()
        {
            var directory=Path.Combine(Path.GetTempPath(),"photobooth-color-migration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                var database=new SqliteDatabase(Path.Combine(directory,"test.db"));database.Initialize();
                var asset=NewAsset();var unassigned=NewAsset();unassigned.Id=Guid.NewGuid();unassigned.DisplayName="Unassigned LUT";unassigned.RelativePath="Assets/Presets/Cubes/unassigned.cube";unassigned.ContentHashSha256=new string('c',64);
                var assetRepository=new SqliteColorLutAssetRepository(database);await assetRepository.InsertAsync(asset,CancellationToken.None);await assetRepository.InsertAsync(unassigned,CancellationToken.None);
                var presetId=Guid.NewGuid();var orphanId=Guid.NewGuid();var now=DateTime.UtcNow.ToString("O");
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText=@"DELETE FROM SchemaMigrations WHERE Version=14;
DROP TABLE PresetColorSettings;
CREATE TABLE PresetColorSettings(PresetId TEXT PRIMARY KEY NOT NULL,LutAssetId TEXT,Strength REAL NOT NULL DEFAULT 1.0,Enabled INTEGER NOT NULL DEFAULT 1,ModifiedAtUtc TEXT NOT NULL,RowVersion INTEGER NOT NULL DEFAULT 1,FOREIGN KEY(PresetId) REFERENCES AdminPresets(Id) ON DELETE CASCADE,FOREIGN KEY(LutAssetId) REFERENCES ColorLutAssets(Id) ON DELETE RESTRICT);
INSERT INTO AdminPresets(Id,Name,SettingsJson,Countdown,CreatedAtUtc,ModifiedAtUtc,IsDefault,IsPinned) VALUES($preset,'Legacy','{""Brightness"":0.25}',3,$now,$now,1,0);
INSERT INTO AdminPresets(Id,Name,SettingsJson,Countdown,CreatedAtUtc,ModifiedAtUtc,IsDefault,IsPinned) VALUES($orphan,'Orphan','{}',3,$now,$now,1,0);
INSERT INTO PresetColorSettings(PresetId,LutAssetId,Strength,Enabled,ModifiedAtUtc,RowVersion) VALUES($preset,$asset,0.83,1,$now,4);";
                    command.Parameters.AddWithValue("$preset",presetId.ToString());command.Parameters.AddWithValue("$orphan",orphanId.ToString());command.Parameters.AddWithValue("$asset",asset.Id.ToString());command.Parameters.AddWithValue("$now",now);command.ExecuteNonQuery();
                }

                database.Initialize();
                var values=await new SqlitePresetRepository(database).GetAllAsync(CancellationToken.None);
                var migrated=Assert.Single(values,x=>x.Id==presetId);Assert.Equal(asset.Id,migrated.LutAssetId);
                var backfilled=Assert.Single(values,x=>x.LutAssetId==unassigned.Id);Assert.Equal("Unassigned LUT",backfilled.Name);
                Assert.DoesNotContain(values,x=>x.Id==orphanId);
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText="SELECT (SELECT COUNT(*) FROM pragma_table_info('PresetColorSettings') WHERE name IN ('Strength','Enabled')),(SELECT SettingsJson IS NULL FROM AdminPresets WHERE Id=$preset),(SELECT IsDefault FROM AdminPresets WHERE Id=$orphan)";
                    command.Parameters.AddWithValue("$preset",presetId.ToString());command.Parameters.AddWithValue("$orphan",orphanId.ToString());
                    using(var reader=command.ExecuteReader()){Assert.True(reader.Read());Assert.Equal(0,reader.GetInt32(0));Assert.Equal(1,reader.GetInt32(1));Assert.Equal(0,reader.GetInt32(2));}
                }
            }
            finally{try{Directory.Delete(directory,true);}catch{}}
        }

        [Fact]
        public async System.Threading.Tasks.Task Preset_events_release_presets_when_deleted()
        {
            var directory=Path.Combine(Path.GetTempPath(),"photobooth-preset-events-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                var database=new SqliteDatabase(Path.Combine(directory,"test.db"));database.Initialize();
                var asset=NewAsset();await new SqliteColorLutAssetRepository(database).InsertAsync(asset,CancellationToken.None);
                var now=DateTime.UtcNow;var preset=new Preset{Id=Guid.NewGuid(),Name="Preset mới",CreatedAtUtc=now,ModifiedAtUtc=now,CaptureCountdownSeconds=3};
                var presets=new SqlitePresetRepository(database);await presets.SaveAsync(preset,CancellationToken.None);
                await new SqlitePresetColorRepository(database).SaveAsync(new PresetColorSettings{PresetId=preset.Id,LutAssetId=asset.Id,ModifiedAtUtc=now},null,CancellationToken.None);
                var events=new SqlitePresetEventRepository(database);var collection=new PresetEvent{Id=Guid.NewGuid(),Name="Wedding",CreatedAtUtc=now};await events.SaveAsync(collection,CancellationToken.None);
                preset=await presets.GetAsync(preset.Id,CancellationToken.None);preset.EventId=collection.Id;await presets.SaveAsync(preset,CancellationToken.None);
                Assert.Equal(collection.Id,(await presets.GetAsync(preset.Id,CancellationToken.None)).EventId);
                await events.DeleteAsync(collection.Id,CancellationToken.None);
                Assert.Null((await presets.GetAsync(preset.Id,CancellationToken.None)).EventId);
            }
            finally{try{Directory.Delete(directory,true);}catch{}}
        }

        static ColorLutAsset NewAsset(){var now=DateTime.UtcNow;return new ColorLutAsset{Id=Guid.NewGuid(),DisplayName="Test LUT",RelativePath="Assets/Presets/Cubes/test.cube",ContentHashSha256=new string('a',64),FileLength=128,CubeSize=33,DomainMaxR=1,DomainMaxG=1,DomainMaxB=1,Status=ColorLutAssetStatus.Ready,LastValidatedAtUtc=now,CreatedAtUtc=now,ModifiedAtUtc=now,RowVersion=1};}
    }
}
