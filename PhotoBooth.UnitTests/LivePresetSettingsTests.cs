using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public class LivePresetSettingsTests
    {
        [Fact]
        public async Task Preview_selection_roundtrips_independently_and_clear_preserves_capture_settings()
        {
            var directory = Path.Combine(Path.GetTempPath(), "x57-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var db = new SqliteDatabase(Path.Combine(directory, "test.db")); db.Initialize();
                var repository = new SqliteSettingsRepository(db);
                var settings = await repository.GetAsync(CancellationToken.None);
                Assert.Null(settings.LiveViewPresetId);
                settings.DefaultPresetId = Guid.NewGuid(); settings.LiveViewPresetId = Guid.NewGuid();
                settings.LiveViewPresetStrengthPercent = 145; settings.PhotoCount = 4;
                var capture = settings.DefaultPresetId; var preview = settings.LiveViewPresetId;
                await repository.SaveAsync(settings, CancellationToken.None);
                var loaded = await repository.GetAsync(CancellationToken.None);
                Assert.Equal(capture, loaded.DefaultPresetId); Assert.Equal(preview, loaded.LiveViewPresetId);
                Assert.Equal(100, loaded.LiveViewPresetStrengthPercent);
                loaded.LiveViewPresetId = null; loaded.LiveViewPresetStrengthPercent = -10;
                await repository.SaveAsync(loaded, CancellationToken.None);
                loaded = await repository.GetAsync(CancellationToken.None);
                Assert.Null(loaded.LiveViewPresetId); Assert.Equal(0, loaded.LiveViewPresetStrengthPercent);
                Assert.Equal(capture, loaded.DefaultPresetId); Assert.Equal(4, loaded.PhotoCount);
                db.Initialize();
                Assert.Equal(capture, (await repository.GetAsync(CancellationToken.None)).DefaultPresetId);
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        }
    }
}
