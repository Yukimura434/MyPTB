using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class TetrahedralColorLutTests
    {
        [Fact]
        public void Every_lut_starts_at_fifty_percent_strength()
        {
            Assert.Equal(0.5f, new ColorLutData().Strength);
        }

        [Theory]
        [InlineData(0.8f,0.5f,0.2f)]
        [InlineData(0.8f,0.2f,0.5f)]
        [InlineData(0.5f,0.2f,0.8f)]
        [InlineData(0.2f,0.5f,0.8f)]
        [InlineData(0.2f,0.8f,0.5f)]
        [InlineData(0.5f,0.8f,0.2f)]
        public void Identity_cube_preserves_rgb_in_all_tetrahedra(float r,float g,float b)
        {
            var values=new float[24];var index=0;
            for(var blue=0;blue<2;blue++)for(var green=0;green<2;green++)for(var red=0;red<2;red++)
            {values[index++]=red;values[index++]=green;values[index++]=blue;}
            var lut=new ColorLutData{Metadata=new ColorLutMetadata{CubeSize=2},Values=values};
            float actualR,actualG,actualB;ColorLutService.Sample(lut,r,g,b,out actualR,out actualG,out actualB);
            Assert.Equal(r,actualR,4);Assert.Equal(g,actualG,4);Assert.Equal(b,actualB,4);
        }

        [Fact]
        public async Task Apply_to_file_is_non_destructive_uses_requested_strength_and_rejects_video()
        {
            var root=Path.Combine(Path.GetTempPath(),"photobooth-customer-lut-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();
                var resolver=new ColorLutPathResolver(new ApplicationOptions{ApplicationName="Test",DataDirectory=root,DatabasePath=Path.Combine(root,"test.db")});
                var service=new ColorLutService(new SqliteColorLutAssetRepository(database),new SqlitePresetColorRepository(database),new CubeLutParser(),resolver,NullLogger<ColorLutService>.Instance);
                var cube=Path.Combine(root,"invert.cube");
                File.WriteAllText(cube,"TITLE \"Invert\"\nLUT_3D_SIZE 2\n1 1 1\n0 1 1\n1 0 1\n0 0 1\n1 1 0\n0 1 0\n1 0 0\n0 0 0\n");
                var imported=await service.ImportAsync(cube,"Invert",CancellationToken.None);
                var now=DateTime.UtcNow;var preset=new Preset{Id=Guid.NewGuid(),Name="Invert",CaptureCountdownSeconds=3,CreatedAtUtc=now,ModifiedAtUtc=now};
                await new SqlitePresetRepository(database).SaveAsync(preset,CancellationToken.None);
                await service.AttachAsync(preset.Id,imported.Asset.Id,CancellationToken.None);
                var source=Path.Combine(root,"source.png");var output=Path.Combine(root,"output.png");
                using(var bitmap=new Bitmap(24,16)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.Red);bitmap.Save(source,ImageFormat.Png);}

                await service.ApplyToFileAsync(preset.Id,source,output,0.5f,CancellationToken.None);

                using(var original=new Bitmap(source)){var pixel=original.GetPixel(12,8);Assert.InRange(pixel.R,250,255);Assert.InRange(pixel.G,0,5);Assert.InRange(pixel.B,0,5);}
                using(var rendered=new Bitmap(output)){Assert.Equal(24,rendered.Width);Assert.Equal(16,rendered.Height);var pixel=rendered.GetPixel(12,8);Assert.InRange(pixel.R,126,129);Assert.InRange(pixel.G,126,129);Assert.InRange(pixel.B,126,129);}
                await Assert.ThrowsAsync<InvalidDataException>(()=>service.ApplyToFileAsync(preset.Id,source,Path.Combine(root,"forbidden.mp4"),0.5f,CancellationToken.None));
            }
            finally{try{Directory.Delete(root,true);}catch{}}
        }

        [Fact]
        public async Task Cancelled_preview_quietly_drops_stale_render()
        {
            var root=Path.Combine(Path.GetTempPath(),"photobooth-cancelled-lut-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var database=new SqliteDatabase(Path.Combine(root,"test.db"));database.Initialize();
                var resolver=new ColorLutPathResolver(new ApplicationOptions{ApplicationName="Test",DataDirectory=root,DatabasePath=Path.Combine(root,"test.db")});
                var service=new ColorLutService(new SqliteColorLutAssetRepository(database),new SqlitePresetColorRepository(database),new CubeLutParser(),resolver,NullLogger<ColorLutService>.Instance);
                var source=Path.Combine(root,"source.png");using(var bitmap=new Bitmap(8,8))bitmap.Save(source,ImageFormat.Png);
                using(var cancellation=new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    Assert.Null(await service.RenderPreviewAsync(Guid.NewGuid(),source,0.5f,cancellation.Token));
                }
            }
            finally{try{Directory.Delete(root,true);}catch{}}
        }
    }
}
