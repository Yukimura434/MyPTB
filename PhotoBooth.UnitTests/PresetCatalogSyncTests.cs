using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class PresetCatalogSyncTests
    {
        [Fact]
        public async Task Cached_versions_join_library_unselected_and_retries_preserve_local_choices()
        {
            var root = Path.Combine(Path.GetTempPath(), "preset-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var db = new SqliteDatabase(Path.Combine(root,"test.db")); db.Initialize();
                var options = new ApplicationOptions { DataDirectory = root, PhotoApiBaseUrl = "https://example.invalid" };
                var paths = new ColorLutPathResolver(options);
                var color = new ColorLutService(new SqliteColorLutAssetRepository(db), new SqlitePresetColorRepository(db),
                    new CubeLutParser(), paths, NullLogger<ColorLutService>.Instance);
                var presets = new SqlitePresetRepository(db);
                var sync = new DeviceConfigurationSyncService(null, null, null, null, options, null,
                    presets, color, new CubeLutParser(), paths);
                var file = Path.Combine(root,"sample.cube");
                File.WriteAllText(file,"LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n");
                var asset = (await color.ImportAsync(file,"Sample",CancellationToken.None)).Asset;
                var type = typeof(DeviceConfigurationSyncService).GetNestedType("RemotePreset",BindingFlags.NonPublic);
                object remote(string version) {
                    var value = Activator.CreateInstance(type,true);
                    foreach(var pair in new System.Collections.Generic.Dictionary<string,object> {
                        {"PresetVersionId",version},{"Name","From web"},{"AssetBytes",asset.FileLength},
                        {"ContentSha256",asset.ContentHashSha256},{"CubeSize",2},{"AssetUrl","https://example.invalid/asset"}})
                        type.GetProperty(pair.Key).SetValue(value,pair.Value);
                    return value;
                }
                Task<bool> ensure(object value) => (Task<bool>)typeof(DeviceConfigurationSyncService)
                    .GetMethod("EnsurePresetAsync",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(sync,new object[]{value,"",CancellationToken.None});
                Assert.True(await ensure(remote("v1")));
                var first = (await presets.GetAllAsync(CancellationToken.None)).Single();
                Assert.False(first.IsPinned); Assert.False(first.IsDefault); Assert.Null(first.EventId);
                first.IsPinned=true;first.StrengthPercent=77;first.Name="Local name";
                await presets.SaveAsync(first,CancellationToken.None);
                Assert.False(await ensure(remote("v1")));
                Assert.True(await ensure(remote("v2")));
                var all=await presets.GetAllAsync(CancellationToken.None);
                Assert.Equal(2,all.Count);
                var retained=all.Single(p=>p.Id==first.Id);
                Assert.True(retained.IsPinned);Assert.Equal(77,retained.StrengthPercent);Assert.Equal("Local name",retained.Name);
                Assert.False(all.Single(p=>p.Id!=first.Id).IsPinned);
                var invalid=remote("invalid");type.GetProperty("AssetBytes").SetValue(invalid,2097153L);
                await Assert.ThrowsAsync<InvalidDataException>(()=>ensure(invalid));
                Assert.Equal(2,(await presets.GetAllAsync(CancellationToken.None)).Count);
            }
            finally { try { Directory.Delete(root,true); } catch {} }
        }
    }
}
