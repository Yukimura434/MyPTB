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
                var assets=new SqliteColorLutAssetRepository(database);
                var presetColors=new SqlitePresetColorRepository(database);
                var presetId=Guid.NewGuid();
                using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
                {
                    command.CommandText="INSERT INTO AdminPresets(Id,Name,Countdown,CreatedAtUtc,ModifiedAtUtc,IsDefault) VALUES($id,'Test',3,$now,$now,0)";
                    command.Parameters.AddWithValue("$id",presetId.ToString());command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
                }
                var asset=NewAsset();await assets.InsertAsync(asset,CancellationToken.None);
                await presetColors.SaveAsync(new PresetColorSettings{PresetId=presetId,LutAssetId=asset.Id,Strength=1,Enabled=true,ModifiedAtUtc=DateTime.UtcNow},null,CancellationToken.None);
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

        static ColorLutAsset NewAsset(){var now=DateTime.UtcNow;return new ColorLutAsset{Id=Guid.NewGuid(),DisplayName="Test LUT",RelativePath="Assets/Presets/Cubes/test.cube",ContentHashSha256=new string('a',64),FileLength=128,CubeSize=33,DomainMaxR=1,DomainMaxG=1,DomainMaxB=1,Status=ColorLutAssetStatus.Ready,LastValidatedAtUtc=now,CreatedAtUtc=now,ModifiedAtUtc=now,RowVersion=1};}
    }
}
