using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;
namespace PhotoBooth.UnitTests
{
    public sealed class BeautySettingsTests
    {
        [Fact]
        public async Task Database_defaults_disabled_and_round_trips_clamped_values()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();db.Initialize();
                var service=new BeautySettingsService(new SqliteBeautySettingsRepository(db));
                Assert.False((await service.GetAsync(CancellationToken.None)).Enabled);
                await service.SaveAsync(new BeautySettings{Enabled=true,SmoothSkin=-3,BrightenSkin=32,SkinTone=101,Sharpen=77,EyeSize=45,SlimFace=120},CancellationToken.None);
                var saved=await service.GetAsync(CancellationToken.None);
                Assert.True(saved.Enabled);Assert.Equal(0,saved.SmoothSkin);Assert.Equal(32,saved.BrightenSkin);Assert.Equal(100,saved.SkinTone);Assert.Equal(77,saved.Sharpen);Assert.Equal(45,saved.EyeSize);Assert.Equal(100,saved.SlimFace);
            }
            finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        }
    }
}
