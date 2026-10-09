using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class WaitingScreenProfileTests : IDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), "waiting-profiles-" + Guid.NewGuid().ToString("N"));
        readonly SqliteDatabase db;
        readonly SqliteSettingsRepository repository;
        public WaitingScreenProfileTests() { db = new SqliteDatabase(Path.Combine(directory, "test.db")); db.Initialize(); repository = new SqliteSettingsRepository(db); }
        [Fact] public async Task Legacy_upgrade_preserves_background_and_geometry_until_orientation_is_configured()
        {
            var id = Guid.NewGuid();
            await repository.SaveAsync(new Settings { WaitingBackgroundZoom = 170, WaitingLiveViewX = 91, ShowWaitingLiveView = true, CustomerLayoutMode = CustomerLayoutMode.Portrait }, CancellationToken.None);
            using (var c = db.OpenConnection()) using (var q = c.CreateCommand()) { q.CommandText = "DROP TABLE WaitingScreenProfiles"; q.ExecuteNonQuery(); }
            db.Initialize(); db.Initialize();
            var saved = await repository.GetAsync(CancellationToken.None);
            Assert.Null(saved.PortraitWaitingScreen); Assert.Null(saved.LandscapeWaitingScreen);
            foreach (var portrait in new[] { false, true }) { var p = saved.GetWaitingScreen(portrait, id); Assert.Equal(id, p.BackgroundAssetId); Assert.Equal(170, p.BackgroundZoom); Assert.Equal(91, p.LiveViewX); Assert.True(p.ShowLiveView); }
        }
        [Fact] public async Task Profiles_round_trip_independently_and_default_never_falls_back_to_legacy_asset()
        {
            var landscape = Guid.NewGuid(); var portrait = Guid.NewGuid();
            var saved = new Settings { PhotoCount = 7, LandscapeWaitingScreen = new WaitingScreenProfile { BackgroundAssetId = landscape, LiveViewX = 83, BackgroundZoom = 145 }, PortraitWaitingScreen = new WaitingScreenProfile { BackgroundAssetId = portrait, ShowLiveView = true, LiveViewY = 87, BackgroundZoom = 220 } };
            await repository.SaveAsync(saved, CancellationToken.None); db.Initialize();
            var first = await repository.GetAsync(CancellationToken.None);
            Assert.Equal(landscape, first.LandscapeWaitingScreen.BackgroundAssetId); Assert.Equal(portrait, first.PortraitWaitingScreen.BackgroundAssetId);
            first.PortraitWaitingScreen.BackgroundAssetId = null; first.PortraitWaitingScreen.ShowLiveView = false;
            await repository.SaveAsync(first, CancellationToken.None);
            var second = await repository.GetAsync(CancellationToken.None);
            Assert.Null(second.GetWaitingScreen(true, landscape).BackgroundAssetId); Assert.False(second.PortraitWaitingScreen.ShowLiveView);
            Assert.Equal(landscape, second.LandscapeWaitingScreen.BackgroundAssetId); Assert.Equal(83, second.LandscapeWaitingScreen.LiveViewX); Assert.Equal(145, second.LandscapeWaitingScreen.BackgroundZoom);
            Assert.Equal(220, second.PortraitWaitingScreen.BackgroundZoom); Assert.Equal(7, second.PhotoCount);
            // A legacy writer must not delete profiles it does not know about.
            await repository.SaveAsync(new Settings(), CancellationToken.None);
            Assert.Equal(landscape, (await repository.GetAsync(CancellationToken.None)).LandscapeWaitingScreen.BackgroundAssetId);
        }
        [Fact] public async Task Failed_profile_write_rolls_back_both_orientations_and_workflow()
        {
            await repository.SaveAsync(new Settings { PhotoCount = 3, LandscapeWaitingScreen = new WaitingScreenProfile { BackgroundZoom = 130 }, PortraitWaitingScreen = new WaitingScreenProfile { BackgroundZoom = 150 } }, CancellationToken.None);
            using (var c = db.OpenConnection()) using (var q = c.CreateCommand()) { q.CommandText = "CREATE TRIGGER fail_profile BEFORE INSERT ON WaitingScreenProfiles WHEN NEW.Orientation=1 BEGIN SELECT RAISE(ABORT,'fixture failure'); END"; q.ExecuteNonQuery(); }
            var saved = await repository.GetAsync(CancellationToken.None); saved.PhotoCount = 8; saved.LandscapeWaitingScreen.BackgroundZoom = 250; saved.PortraitWaitingScreen.BackgroundZoom = 280;
            await Assert.ThrowsAsync<SqliteException>(() => repository.SaveAsync(saved, CancellationToken.None));
            var after = await repository.GetAsync(CancellationToken.None); Assert.Equal(3, after.PhotoCount); Assert.Equal(130, after.LandscapeWaitingScreen.BackgroundZoom); Assert.Equal(150, after.PortraitWaitingScreen.BackgroundZoom);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync(saved, new CancellationToken(true)));
        }
        [Fact] public void Editing_profile_copy_cannot_mutate_saved_settings()
        {
            var s = new Settings { PortraitWaitingScreen = new WaitingScreenProfile { BackgroundZoom = 180 } };
            s.GetWaitingScreen(true, null).BackgroundZoom = 250;
            Assert.Equal(180, s.PortraitWaitingScreen.BackgroundZoom);
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
