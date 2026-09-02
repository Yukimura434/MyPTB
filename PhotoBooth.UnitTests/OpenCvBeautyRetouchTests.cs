using System;
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
        [Fact]
        public async Task Process_detects_faces_and_preserves_dimensions()
        {
            if(IntPtr.Size!=4)return;
            var source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","TestImages","harry-potter.jpg");
            var assets=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","Beauty");
            var output=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".jpg");
            try
            {
                using(var service=new OpenCvBeautyRetouchService(assets))
                {
                    var result=await service.ProcessAsync(source,output,new BeautySettings{Enabled=true,SmoothSkin=30,BrightenSkin=20,SkinTone=20,Sharpen=25,EyeSize=20,SlimFace=20},CancellationToken.None);
                    Assert.True(result.Applied);Assert.True(result.FacesDetected>0);
                }
                using(var before=Cv2.ImRead(source))using(var after=Cv2.ImRead(output)){Assert.Equal(before.Size(),after.Size());Assert.False(after.Empty());}
            }
            finally { try{if(File.Exists(output))File.Delete(output);}catch{} }
        }

        [Fact]
        public async Task Live_preview_processes_jpeg_in_memory_and_preserves_dimensions()
        {
            if(IntPtr.Size!=4)return;
            var source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","TestImages","harry-potter.jpg");
            var assets=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","Beauty");var input=File.ReadAllBytes(source);
            using(var service=new OpenCvLiveBeautyPreviewService(assets))
            {
                var output=await service.ProcessAsync(input,new BeautySettings{Enabled=true,SmoothSkin=25,BrightenSkin=15,EyeSize=15,SlimFace=15},CancellationToken.None);
                Assert.NotNull(output);Assert.NotEmpty(output);Assert.NotEqual(input,output);
                var repeated=await service.ProcessAsync(input,new BeautySettings{Enabled=true,SmoothSkin=25,BrightenSkin=15,EyeSize=15,SlimFace=15},CancellationToken.None);
                Assert.Same(output,repeated);
                using(var before=Cv2.ImDecode(input,ImreadModes.Color))using(var after=Cv2.ImDecode(output,ImreadModes.Color))Assert.Equal(before.Size(),after.Size());
                service.Reset();
            }
        }

        [Fact]
        public async Task Live_preview_cancellation_quietly_drops_stale_frame()
        {
            var input=new byte[]{1,2,3};
            using(var cancellation=new CancellationTokenSource())
            using(var service=new OpenCvLiveBeautyPreviewService("missing-assets"))
            {
                cancellation.Cancel();
                var output=await service.ProcessAsync(input,new BeautySettings{Enabled=true,SmoothSkin=25},cancellation.Token);
                Assert.Same(input,output);
            }
        }
    }
}
