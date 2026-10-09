using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class LibraryReplacementTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Replacement_is_exact_durable_and_keeps_classification_and_other_settings(bool preset)
        {
            using (var fixture = new LibraryFixture(preset))
            {
                await fixture.Seed();
                await fixture.Replace(new[] { fixture.Member });
                fixture.Database.Initialize();
                Assert.Equal(fixture.Member, Assert.Single(await fixture.Pinned()));
                var settings = await fixture.Settings.GetAsync(CancellationToken.None);
                Assert.Empty(preset ? settings.SelectedPresetCollectionIds : settings.SelectedFrameCollectionIds);
                Assert.Single(preset ? settings.SelectedFrameCollectionIds : settings.SelectedPresetCollectionIds);
                Assert.Equal(fixture.LivePreset, settings.LiveViewPresetId);
                Assert.Equal(65, settings.LiveViewPresetStrengthPercent);
                Assert.Equal(7, settings.PhotoCount);
                if (preset)
                {
                    var item = await fixture.Presets.GetAsync(fixture.Member, CancellationToken.None);
                    Assert.Equal(75, item.StrengthPercent);
                    Assert.Equal(fixture.Collection, item.EventId);
                    Assert.Equal(fixture.Lut, item.LutAssetId);
                }
                else
                {
                    var item = await fixture.Frames.GetAsync(fixture.Member, CancellationToken.None);
                    Assert.Equal(fixture.Collection, item.EventId);
                    Assert.Equal(fixture.Slot, Assert.Single(item.Slots).Id);
                }
                await fixture.Replace(new[] { fixture.Outside, fixture.Member, fixture.Member });
                Assert.Equal(2, (await fixture.Pinned()).Count);
                await fixture.Replace(new[] { fixture.Member });
                Assert.Equal(fixture.Member, Assert.Single(await fixture.Pinned()));
                await fixture.Replace(new Guid[0]);
                Assert.Empty(await fixture.Pinned());
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Missing_item_rolls_back_all_pins_and_legacy_selection(bool preset)
        {
            using (var fixture = new LibraryFixture(preset))
            {
                await fixture.Seed();
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Replace(new[] { fixture.Member, Guid.NewGuid() }));
                Assert.Equal(fixture.Outside, Assert.Single(await fixture.Pinned()));
                var settings = await fixture.Settings.GetAsync(CancellationToken.None);
                Assert.Equal(fixture.Collection, Assert.Single(preset ? settings.SelectedPresetCollectionIds : settings.SelectedFrameCollectionIds));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Replace(new[] { fixture.Member }, new CancellationToken(true)));
                Assert.Equal(fixture.Outside, Assert.Single(await fixture.Pinned()));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Reclassification_and_collection_deletion_do_not_change_the_used_snapshot(bool preset)
        {
            using (var fixture = new LibraryFixture(preset))
            {
                await fixture.Seed();
                await fixture.Replace(new[] { fixture.Member });
                if (preset)
                {
                    var events = new PresetEventService(new SqlitePresetEventRepository(fixture.Database), fixture.Presets);
                    var second = await events.CreateAsync("Second", CancellationToken.None);
                    await events.AssignPresetAsync(fixture.Member, second.Id, CancellationToken.None);
                    Assert.Equal(second.Id, (await fixture.Presets.GetAsync(fixture.Member, CancellationToken.None)).EventId);
                    await events.AssignPresetAsync(fixture.Member, null, CancellationToken.None);
                    Assert.Null((await fixture.Presets.GetAsync(fixture.Member, CancellationToken.None)).EventId);
                    await events.AssignPresetAsync(fixture.Member, second.Id, CancellationToken.None);
                    await events.DeleteAsync(second.Id, CancellationToken.None);
                    Assert.Null((await fixture.Presets.GetAsync(fixture.Member, CancellationToken.None)).EventId);
                }
                else
                {
                    var events = new FrameEventService(new SqliteFrameEventRepository(fixture.Database), fixture.Frames);
                    var second = await events.CreateAsync("Second", CancellationToken.None);
                    await events.AssignFrameAsync(fixture.Member, second.Id, CancellationToken.None);
                    Assert.Equal(second.Id, (await fixture.Frames.GetAsync(fixture.Member, CancellationToken.None)).EventId);
                    await events.AssignFrameAsync(fixture.Member, null, CancellationToken.None);
                    Assert.Null((await fixture.Frames.GetAsync(fixture.Member, CancellationToken.None)).EventId);
                    await events.AssignFrameAsync(fixture.Member, second.Id, CancellationToken.None);
                    await events.DeleteAsync(second.Id, CancellationToken.None);
                    Assert.Null((await fixture.Frames.GetAsync(fixture.Member, CancellationToken.None)).EventId);
                }
                Assert.Equal(fixture.Member, Assert.Single(await fixture.Pinned()));
            }
        }

        sealed class LibraryFixture : IDisposable
        {
            readonly bool preset;
            readonly string root = Path.Combine(Path.GetTempPath(), "library-replacement-" + Guid.NewGuid().ToString("N"));
            public readonly Guid Member = Guid.NewGuid(), Outside = Guid.NewGuid(), Collection = Guid.NewGuid(), LivePreset = Guid.NewGuid(), Slot = Guid.NewGuid(), Lut = Guid.NewGuid();
            public SqliteDatabase Database { get; }
            public SqliteFrameRepository Frames { get; }
            public SqlitePresetRepository Presets { get; }
            public SqliteSettingsRepository Settings { get; }
            public LibraryFixture(bool preset)
            {
                this.preset = preset;
                Database = new SqliteDatabase(Path.Combine(root, "test.db")); Database.Initialize();
                Frames = new SqliteFrameRepository(Database); Presets = new SqlitePresetRepository(Database); Settings = new SqliteSettingsRepository(Database);
            }
            public async Task Seed()
            {
                var now = DateTime.UtcNow; var token = CancellationToken.None;
                await new SqliteFrameEventRepository(Database).SaveAsync(new FrameEvent { Id = Collection, Name = "Collection", CreatedAtUtc = now }, token);
                await new SqlitePresetEventRepository(Database).SaveAsync(new PresetEvent { Id = Collection, Name = "Collection", CreatedAtUtc = now }, token);
                await new SqliteColorLutAssetRepository(Database).InsertAsync(new ColorLutAsset { Id = Lut, DisplayName = "Test", RelativePath = "Assets/Presets/Cubes/test.cube", ContentHashSha256 = new string('a', 64), FileLength = 128, CubeSize = 2, DomainMaxR = 1, DomainMaxG = 1, DomainMaxB = 1, Status = ColorLutAssetStatus.Ready, CreatedAtUtc = now, ModifiedAtUtc = now }, token);
                foreach (var id in new[] { Member, Outside })
                {
                    await Frames.SaveAsync(new Frame { Id = id, Name = id.ToString(), CreatedAtUtc = now, EventId = id == Member ? (Guid?)Collection : null, IsPinned = id == Outside, Slots = new[] { new FrameSlot { Id = id == Member ? Slot : Guid.NewGuid(), Index = 0, Width = 80, Height = 80 } } }, token);
                    await Presets.SaveAsync(new Preset { Id = id, Name = id.ToString(), CreatedAtUtc = now, ModifiedAtUtc = now, StrengthPercent = 75, EventId = id == Member ? (Guid?)Collection : null, IsPinned = id == Outside }, token);
                    await new SqlitePresetColorRepository(Database).SaveAsync(new PresetColorSettings { PresetId = id, LutAssetId = Lut, ModifiedAtUtc = now }, null, token);
                }
                var settings = await Settings.GetAsync(token);
                settings.SelectedFrameCollectionIds = new[] { Collection }; settings.SelectedPresetCollectionIds = new[] { Collection };
                settings.LiveViewPresetId = LivePreset; settings.LiveViewPresetStrengthPercent = 65; settings.PhotoCount = 7;
                await Settings.SaveAsync(settings, token);
            }
            public Task Replace(IReadOnlyCollection<Guid> ids, CancellationToken token = default) => preset ? Presets.ReplacePinnedAsync(ids, token) : Frames.ReplacePinnedAsync(ids, token);
            public async Task<IReadOnlyList<Guid>> Pinned() => preset ? (await Presets.GetAllAsync(CancellationToken.None)).Where(x => x.IsPinned).Select(x => x.Id).ToArray() : (await Frames.GetAllAsync(CancellationToken.None)).Where(x => x.IsPinned).Select(x => x.Id).ToArray();
            public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        }
    }
}
