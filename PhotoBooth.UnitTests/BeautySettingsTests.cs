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
        public async Task Geometry_is_capped_on_read_and_save_without_mutating_on_read()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();
                var repository=new SqliteBeautySettingsRepository(db);
                await repository.SaveAsync(new BeautySettings{Enabled=true,EyeSize=100,SlimFace=100,SmoothSkin=37},CancellationToken.None);
                var service=new BeautySettingsService(repository);
                var settings=await service.GetAsync(CancellationToken.None);
                Assert.Equal(20,settings.EyeSize);Assert.Equal(20,settings.SlimFace);Assert.Equal(37,settings.SmoothSkin);
                // Loading does not mutate the user's database.
                Assert.Equal(100,(await repository.GetAsync(CancellationToken.None)).EyeSize);
                BeautySettings changed=null;service.SettingsChanged+=(s,e)=>changed=e.Settings;
                await service.SaveAsync(settings,CancellationToken.None);
                var stored=await repository.GetAsync(CancellationToken.None);
                Assert.Equal(20,stored.EyeSize);Assert.Equal(20,stored.SlimFace);Assert.Equal(37,stored.SmoothSkin);
                Assert.NotNull(changed);Assert.Equal(20,changed.EyeSize);Assert.Equal(20,changed.SlimFace);
            }
            finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        }

        [Fact]
        public void Geometry_enables_still_processing_but_not_live_processing()
        {
            Assert.True(new BeautySettings{Enabled=true,EyeSize=50,SlimFace=50}.HasEffect);
            Assert.False(new BeautySettings{Enabled=true,EyeSize=50,SlimFace=50}.HasLiveEffect);
        }

        [Fact]
        public async Task Database_defaults_disabled_and_round_trips_clamped_values()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();db.Initialize();
                var service=new BeautySettingsService(new SqliteBeautySettingsRepository(db));
                BeautySettings changed=null;service.SettingsChanged+=(s,e)=>changed=e.Settings;
                Assert.False((await service.GetAsync(CancellationToken.None)).Enabled);
                await service.SaveAsync(new BeautySettings{Enabled=true,SmoothSkin=-3,BrightenSkin=32,SkinTone=101,Sharpen=77,EyeSize=45,SlimFace=120,BlemishRemoval=52,DarkCircleReduction=63},CancellationToken.None);
                var saved=await service.GetAsync(CancellationToken.None);
                Assert.True(saved.Enabled);Assert.Equal(0,saved.SmoothSkin);Assert.Equal(20,saved.BrightenSkin);Assert.Equal(20,saved.SkinTone);Assert.Equal(77,saved.Sharpen);Assert.Equal(20,saved.EyeSize);Assert.Equal(20,saved.SlimFace);
                Assert.Equal(52,saved.BlemishRemoval);Assert.Equal(0,saved.DarkCircleReduction);
                Assert.NotNull(changed);Assert.True(changed.Enabled);Assert.Equal(0,changed.SmoothSkin);Assert.Equal(20,changed.SkinTone);
            }
            finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        }
    }
}
