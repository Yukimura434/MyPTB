using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PhotoBooth.Core.Models;
using PhotoBooth.OpenCvRetouch;
using Xunit;
namespace PhotoBooth.UnitTests
{
    public sealed class OpenCvBeautyRetouchTests
    {
        static string Source => Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","TestImages","beauty-model.png");
        static string Assets => Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","Beauty");

        [Fact]
        public async Task Geometry_changes_still_image_and_is_capped_at_half_old_strength()
        {
            Assert.Equal(4, IntPtr.Size);
            var a=Path.GetTempFileName();var b=Path.GetTempFileName();var plain=Path.GetTempFileName();
            try
            {
                using(var service=new OpenCvBeautyRetouchService(Assets))
                {
                    Assert.True((await service.ProcessAsync(Source,a,new BeautySettings{Enabled=true,EyeSize=50,SlimFace=50},CancellationToken.None)).Applied);
                    await service.ProcessAsync(Source,b,new BeautySettings{Enabled=true,EyeSize=100,SlimFace=100},CancellationToken.None);
                }
                Assert.Equal(File.ReadAllBytes(a),File.ReadAllBytes(b));
                using(var original=Cv2.ImRead(Source))using(var processed=Cv2.ImRead(a))
                using(var baseline=Cv2.ImDecode(original.ImEncode(".jpg"),ImreadModes.Color))
                {Assert.Equal(original.Size(),processed.Size());Assert.True(Cv2.Norm(baseline,processed,NormTypes.L1)>0);}
            }
            finally{File.Delete(a);File.Delete(b);File.Delete(plain);}
        }

        [Fact]
        public async Task Geometry_and_heavy_effects_alone_bypass_live_models()
        {
            using(var service=new OpenCvLiveBeautyPreviewService("missing-assets"))
                Assert.Null(await service.ProcessAsync(new byte[]{1,2,3},new BeautySettings{Enabled=true,EyeSize=50,SlimFace=50,BlemishRemoval=100,DarkCircleReduction=100,SkinTone=100,Sharpen=100},CancellationToken.None));
        }

        [Theory]
        [InlineData(100,0)]
        [InlineData(0,100)]
        public async Task Live_light_changes_pixels_and_reset_does_not_reuse_masks(int smooth,int bright)
        {
            var bytes=File.ReadAllBytes(Source);
            using(var service=new OpenCvLiveBeautyPreviewService(Assets))
            {
                var settings=new BeautySettings{Enabled=true,SmoothSkin=smooth,BrightenSkin=bright};
                var output=await WaitForBeauty(service,bytes,settings);
                using(var original=Cv2.ImDecode(bytes,ImreadModes.Color))using(var resized=new Mat())
                using(var after=new Mat(output.Height,output.Width,MatType.CV_8UC3))
                {
                    Cv2.Resize(original,resized,new Size(output.Width,output.Height),0,0,InterpolationFlags.Area);
                    System.Runtime.InteropServices.Marshal.Copy(output.Pixels,0,after.Data,output.Pixels.Length);
                    Assert.True(Cv2.Norm(resized,after,NormTypes.L1)>0);
                    Assert.True(Math.Max(output.Width,output.Height)<=640);
                }
                service.Reset();
                using(var blank=new Mat(output.Height,output.Width,MatType.CV_8UC3,Scalar.All(45)))
                {
                    var empty=await service.ProcessAsync(blank.ImEncode(".jpg"),settings,CancellationToken.None);
                    Assert.NotNull(empty);Assert.False(empty.BeautyApplied);
                }
            }
        }

        internal static async Task<LiveBeautyPixels> WaitForBeauty(OpenCvLiveBeautyPreviewService service,byte[] bytes,BeautySettings settings)
        {
            var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<15000)
            {
                var output=await service.ProcessAsync(bytes,settings,CancellationToken.None);
                if(output?.BeautyApplied==true)return output;
                await Task.Delay(33);
            }
            throw new Exception("Background detector never produced a usable current-frame mask.");
        }

        [Fact]
        public async Task Live_cancellation_and_disposal_drop_frames_without_loading_models()
        {
            var service=new OpenCvLiveBeautyPreviewService("missing-assets");
            var settings=new BeautySettings{Enabled=true,SmoothSkin=100};
            Assert.Null(await service.ProcessAsync(new byte[]{1},settings,new CancellationToken(true)));
            service.Dispose();
            Assert.Null(await service.ProcessAsync(new byte[]{1},settings,CancellationToken.None));
        }

        [Fact]
        public async Task Full_still_effects_preserve_dimensions()
        {
            var output=Path.GetTempFileName();
            try
            {
                var settings=new BeautySettings{Enabled=true};settings.SetLevel(10);
                using(var service=new OpenCvBeautyRetouchService(Assets))Assert.True((await service.ProcessAsync(Source,output,settings,CancellationToken.None)).Applied);
                using(var before=Cv2.ImRead(Source))using(var after=Cv2.ImRead(output))Assert.Equal(before.Size(),after.Size());
            }
            finally{File.Delete(output);}
        }
    }
}
