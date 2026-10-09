using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class BoothLibrarySelectionTests
    {
        [Fact]
        public void Removing_a_collection_keeps_independent_pins_and_deduplicates_members()
        {
            var collection = Guid.NewGuid();
            var pinned = new Frame { Id=Guid.NewGuid(), EventId=collection, IsPinned=true };
            var member = new Frame { Id=Guid.NewGuid(), EventId=collection };
            var outside = new Frame { Id=Guid.NewGuid() };
            var frames = new[] { pinned, member, pinned, outside };
            var settings = new Settings { SelectedFrameCollectionIds=new[] { collection, collection } };
            Assert.Equal(2,BoothLibrarySelection.Frames(frames,settings).Count);
            settings.SelectedFrameCollectionIds=new Guid[0];
            Assert.Equal(pinned.Id,Assert.Single(BoothLibrarySelection.Frames(frames,settings)).Id);
            Assert.False(member.IsPinned);
        }

        [Fact]
        public void New_members_join_selected_collections_without_changing_their_manual_pin()
        {
            var collection=Guid.NewGuid();
            var preset=new Preset { Id=Guid.NewGuid(),EventId=collection,StrengthPercent=75 };
            var settings=new Settings { SelectedPresetCollectionIds=new[] { collection } };
            Assert.Equal(75,Assert.Single(BoothLibrarySelection.Presets(new[] {preset},settings)).StrengthPercent);
            preset.EventId=null;
            Assert.Empty(BoothLibrarySelection.Presets(new[] {preset},settings));
            preset.IsPinned=true;
            Assert.Single(BoothLibrarySelection.Presets(new[] {preset},settings));
        }

        [Fact]
        public async Task Collection_choices_and_booth_settings_survive_restart_and_unrelated_settings_save()
        {
            var root=Path.Combine(Path.GetTempPath(),"booth-library-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path=Path.Combine(root,"test.db");var db=new SqliteDatabase(path);db.Initialize();
                var repo=new SqliteSettingsRepository(db);
                var frames=Guid.NewGuid();var presets=Guid.NewGuid();
                var settings=await repo.GetAsync(CancellationToken.None);
                settings.SelectedFrameCollectionIds=new[] {frames,frames};settings.SelectedPresetCollectionIds=new[] {presets};
                settings.BoothSettingsConfigured=true;settings.PhotoCount=4;settings.GifFrameDurationMilliseconds=400;
                await repo.SaveAsync(settings,CancellationToken.None);
                db=new SqliteDatabase(path);db.Initialize();repo=new SqliteSettingsRepository(db);
                var loaded=await repo.GetAsync(CancellationToken.None);
                Assert.Equal(frames,Assert.Single(loaded.SelectedFrameCollectionIds));Assert.Equal(presets,Assert.Single(loaded.SelectedPresetCollectionIds));
                Assert.True(loaded.BoothSettingsConfigured);Assert.Equal(4,loaded.PhotoCount);Assert.Equal(400,loaded.GifFrameDurationMilliseconds);
                loaded.PreferredCameraId="camera-test";await repo.SaveAsync(loaded,CancellationToken.None);
                Assert.Equal(presets,Assert.Single((await repo.GetAsync(CancellationToken.None)).SelectedPresetCollectionIds));
            }
            finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        }

        [Theory]
        [InlineData(0)] [InlineData(75)] [InlineData(100)]
        public async Task Preset_strength_round_trips_and_pin_updates_preserve_it(int strength)
        {
            var root=Path.Combine(Path.GetTempPath(),"booth-strength-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();
                var repo=new SqlitePresetRepository(db);var now=DateTime.UtcNow;
                var preset=new Preset { Id=Guid.NewGuid(),Name="Test",StrengthPercent=strength,CreatedAtUtc=now,ModifiedAtUtc=now };
                await repo.SaveAsync(preset,CancellationToken.None);
                var asset=new ColorLutAsset { Id=Guid.NewGuid(),DisplayName="Test",RelativePath="Assets/Presets/Cubes/test.cube",ContentHashSha256=new string('a',64),FileLength=128,CubeSize=2,DomainMaxR=1,DomainMaxG=1,DomainMaxB=1,Status=ColorLutAssetStatus.Ready,CreatedAtUtc=now,ModifiedAtUtc=now,RowVersion=1 };
                await new SqliteColorLutAssetRepository(db).InsertAsync(asset,CancellationToken.None);
                await new SqlitePresetColorRepository(db).SaveAsync(new PresetColorSettings { PresetId=preset.Id,LutAssetId=asset.Id,ModifiedAtUtc=now },null,CancellationToken.None);
                var loaded=await repo.GetAsync(preset.Id,CancellationToken.None);Assert.Equal(strength,loaded.StrengthPercent);
                loaded.IsPinned=true;await repo.SaveAsync(loaded,CancellationToken.None);
                db.Initialize();Assert.Equal(strength,(await repo.GetAsync(preset.Id,CancellationToken.None)).StrengthPercent);
                Assert.Equal(asset.Id,(await repo.GetAsync(preset.Id,CancellationToken.None)).LutAssetId);
            }
            finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        }
    }
}
