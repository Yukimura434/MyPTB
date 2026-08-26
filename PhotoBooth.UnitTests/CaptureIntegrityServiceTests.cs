using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CaptureIntegrityServiceTests
    {
        [Fact]
        public async Task PictureAndVideo_requires_matching_ids_lineage_and_readable_video_payload()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var picture=Asset(root,"p.jpg",CaptureAssetTypes.Picture,"image-1",Encoding.ASCII.GetBytes("picture"));
                var video=Asset(root,"p.mp4",CaptureAssetTypes.Video,"image-1",Mp4());
                var composite=Asset(root,"final.png",CaptureAssetTypes.Composite,null,Encoding.ASCII.GetBytes("composite"));composite.SourceAssetIds=new[]{picture.Id};
                var compositeVideo=Asset(root,"final.mp4",CaptureAssetTypes.CompositeVideo,null,Mp4());compositeVideo.SourceAssetIds=new[]{video.Id};
                var capture=new PhotoCapture{Id="capture",MediaMode=CaptureMediaModes.PictureAndVideo,Photos=new[]{picture,video,composite,compositeVideo}};foreach(var asset in capture.Photos)asset.CaptureId=capture.Id;
                await new CaptureIntegrityService().ValidateAsync(capture,CancellationToken.None);
                video.CapturedImageId="image-2";await Assert.ThrowsAsync<InvalidDataException>(()=>new CaptureIntegrityService().ValidateAsync(capture,CancellationToken.None));
            }
            finally{Directory.Delete(root,true);}
        }
        [Fact]
        public async Task Video_composite_lineage_may_reference_only_the_sources_used_by_frame_slots()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var picture1=Asset(root,"p1.jpg",CaptureAssetTypes.Picture,"image-1",Encoding.ASCII.GetBytes("picture 1"));
                var picture2=Asset(root,"p2.jpg",CaptureAssetTypes.Picture,"image-2",Encoding.ASCII.GetBytes("picture 2"));
                var video1=Asset(root,"p1.mp4",CaptureAssetTypes.Video,"image-1",Mp4());
                var video2=Asset(root,"p2.mp4",CaptureAssetTypes.Video,"image-2",Mp4());
                var composite=Asset(root,"final.png",CaptureAssetTypes.Composite,null,Encoding.ASCII.GetBytes("composite"));composite.SourceAssetIds=new[]{picture1.Id,picture2.Id};
                var videoComposite=Asset(root,"final.mp4",CaptureAssetTypes.CompositeVideo,null,Mp4());videoComposite.SourceAssetIds=new[]{video1.Id};
                var capture=new PhotoCapture{Id="capture",MediaMode=CaptureMediaModes.PictureAndVideo,Photos=new[]{picture1,picture2,video1,video2,composite,videoComposite}};foreach(var asset in capture.Photos)asset.CaptureId=capture.Id;
                await new CaptureIntegrityService().ValidateAsync(capture,CancellationToken.None);
                videoComposite.SourceAssetIds=new[]{picture1.Id};
                await Assert.ThrowsAsync<InvalidDataException>(()=>new CaptureIntegrityService().ValidateAsync(capture,CancellationToken.None));
            }
            finally{Directory.Delete(root,true);}
        }
        static CapturePhoto Asset(string root,string name,string type,string imageId,byte[] bytes){var path=Path.Combine(root,name);File.WriteAllBytes(path,bytes);return new CapturePhoto{Id=Guid.NewGuid().ToString("N"),CapturedImageId=imageId,LocalPath=path,PhotoType=type,FileLength=bytes.Length,ContentHashSha256=Hash(bytes),SourceAssetIds=new string[0]};}
        static byte[] Mp4()=>new byte[]{0,0,0,24,(byte)'f',(byte)'t',(byte)'y',(byte)'p',(byte)'i',(byte)'s',(byte)'o',(byte)'m'};
        static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",string.Empty).ToLowerInvariant();}
    }
}
