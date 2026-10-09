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
    public sealed class InterfaceDefaultsTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Default_clears_selection_and_waiting_live_view_but_preserves_assets_and_settings(bool existingSettings)
        {
            using (var f = new Fixture())
            {
                var before = await f.Settings.GetAsync(CancellationToken.None);
                if (existingSettings)
                {
                    before.WaitingBackgroundZoom = 180;
                    before.WaitingLiveViewAreaPercent = 20;
                    before.PhotoCount = 7;
                    before.LiveViewPresetId = Guid.NewGuid();
                    await f.Settings.SaveAsync(before, CancellationToken.None);
                }
                await f.Assets.UseDefaultAsync(CancellationToken.None);
                f.Db.Initialize();
                Assert.Null(await f.Assets.GetSelectedAsync(CancellationToken.None));
                Assert.Single(await f.Assets.GetAllAsync(CancellationToken.None));
                var after = await f.Settings.GetAsync(CancellationToken.None);
                Assert.False(after.ShowWaitingLiveView);
                Assert.Equal(before.PhotoCount, after.PhotoCount);
                Assert.Equal(before.WaitingBackgroundZoom, after.WaitingBackgroundZoom);
                Assert.Equal(before.WaitingLiveViewAreaPercent, after.WaitingLiveViewAreaPercent);
                Assert.Equal(before.LiveViewPresetId, after.LiveViewPresetId);
                await f.Assets.UseDefaultAsync(CancellationToken.None);
                await f.Assets.SelectAsync(f.Id, CancellationToken.None);
                Assert.Equal(f.Id, (await f.Assets.GetSelectedAsync(CancellationToken.None)).Id);
                Assert.False((await f.Settings.GetAsync(CancellationToken.None)).ShowWaitingLiveView);
            }
        }

        [Fact]
        public async Task Default_failure_or_cancellation_keeps_previous_background_and_live_view()
        {
            using (var f = new Fixture())
            {
                await f.Settings.SaveAsync(new Settings { ShowWaitingLiveView = true }, CancellationToken.None);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Assets.UseDefaultAsync(new CancellationToken(true)));
                using (var c = f.Db.OpenConnection()) using (var q = c.CreateCommand())
                {
                    q.CommandText = "CREATE TRIGGER fail_default BEFORE UPDATE OF ShowWaitingLiveView ON WorkflowSettings BEGIN SELECT RAISE(ABORT, 'test failure'); END";
                    q.ExecuteNonQuery();
                }
                await Assert.ThrowsAsync<SqliteException>(() => f.Assets.UseDefaultAsync(CancellationToken.None));
                Assert.Equal(f.Id, (await f.Assets.GetSelectedAsync(CancellationToken.None)).Id);
                Assert.True((await f.Settings.GetAsync(CancellationToken.None)).ShowWaitingLiveView);
            }
        }

        sealed class Fixture : IDisposable
        {
            readonly string directory = Path.Combine(Path.GetTempPath(), "interface-default-" + Guid.NewGuid().ToString("N"));
            public readonly Guid Id = Guid.NewGuid();
            public readonly SqliteDatabase Db;
            public readonly SqliteInterfaceAssetRepository Assets;
            public readonly SqliteSettingsRepository Settings;
            public Fixture()
            {
                Db = new SqliteDatabase(Path.Combine(directory, "test.db")); Db.Initialize();
                Assets = new SqliteInterfaceAssetRepository(Db); Settings = new SqliteSettingsRepository(Db);
                Assets.AddAsync(new InterfaceAsset { Id = Id, Name = "Background", FilePath = "background.gif", IsAnimated = true, IsSelected = true, CreatedAtUtc = DateTime.UtcNow }, CancellationToken.None).GetAwaiter().GetResult();
            }
            public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
