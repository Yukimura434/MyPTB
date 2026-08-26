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
        public async Task PictureAndMotion_requires_matching_ids_lineage_and_readable_motion_payload()
        {
            var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var picture=Asset(root,"p.jpg",CaptureAssetTypes.Picture,"image-1",Encoding.ASCII.GetBytes("picture"));
                var motion=Asset(root,"p_MP.jpg",CaptureAssetTypes.MotionPhoto,"image-1",Encoding.ASCII.GetBytes("jpeg GCamera:MicroVideo data ftyp mp4"));
                var composite=Asset(root,"final.png",CaptureAssetTypes.Composite,null,Encoding.ASCII.GetBytes("composite"));composite.SourceAssetIds=new[]{picture.Id};
                var motionComposite=Asset(root,"final_MP.jpg",CaptureAssetTypes.MotionPhotoComposite,null,Encoding.ASCII.GetBytes("jpeg GCamera:MicroVideo data ftyp mp4"));motionComposite.SourceAssetIds=new[]{motion.Id};
                var capture=new PhotoCapture{Id="capture",MediaMode=CaptureMediaModes.PictureAndMotion,Photos=new[]{picture,motion,composite,motionComposite}};foreach(var asset in capture.Photos)asset.CaptureId=capture.Id;
                await new CaptureIntegrityService().ValidateAsync(capture,CancellationToken.None);
                motion.CapturedImageId="image-2";await Assert.ThrowsAsync<InvalidDataException>(()=>new CaptureIntegrityService().ValidateAsync(capture,CancellationToken.None));
            }
            finally{Directory.Delete(root,true);}
        }
        static CapturePhoto Asset(string root,string name,string type,string imageId,byte[] bytes){var path=Path.Combine(root,name);File.WriteAllBytes(path,bytes);return new CapturePhoto{Id=Guid.NewGuid().ToString("N"),CapturedImageId=imageId,LocalPath=path,PhotoType=type,FileLength=bytes.Length,ContentHashSha256=Hash(bytes),SourceAssetIds=new string[0]};}
        static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",string.Empty).ToLowerInvariant();}
    }
}
