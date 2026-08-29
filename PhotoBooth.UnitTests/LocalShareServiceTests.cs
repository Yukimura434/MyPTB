using System;using System.Drawing;using System.Drawing.Imaging;using System.IO;using System.Linq;using System.Threading;using PhotoBooth.Infrastructure.Services;using Xunit;
namespace PhotoBooth.UnitTests
{
 public sealed class LocalShareServiceTests
 {
  [Fact]public void CreateAssets_CreatesThumbnailWithoutChangingOriginal()
  {
   var root=NewDirectory();try{var original=Path.Combine(root,"photo.jpg");using(var image=new Bitmap(800,600)){using(var graphics=Graphics.FromImage(image))graphics.Clear(Color.CornflowerBlue);image.Save(original,ImageFormat.Jpeg);}var expected=File.ReadAllBytes(original);var thumbnails=Path.Combine(root,"thumbs");Directory.CreateDirectory(thumbnails);var asset=Assert.Single(LocalShareService.CreateAssets(new[]{original},thumbnails,CancellationToken.None));Assert.Equal("Ảnh 1",asset.Label);Assert.Equal("image/jpeg",asset.MimeType);Assert.True(File.Exists(asset.ThumbnailPath));Assert.Equal(expected,File.ReadAllBytes(original));using(var thumbnail=new Bitmap(asset.ThumbnailPath))Assert.True(thumbnail.Width<=480&&thumbnail.Height<=480);}finally{Directory.Delete(root,true);}
  }
  [Fact]public void Gallery_UsesOnlyThumbnailUrlsAndProvidesPerFileAndFixedDownloadAllControls()
  {
   var assets=new[]{new LocalShareService.ShareAsset{Id="photo-id",Label="Ảnh 1"},new LocalShareService.ShareAsset{Id="video-id",Label="Video 1"}};var html=LocalShareService.BuildGalleryHtml("token-1",assets);Assert.Contains("Ảnh của bạn",html);Assert.Contains("/share/token-1/thumbnail/photo-id",html);Assert.DoesNotContain("<video",html,StringComparison.OrdinalIgnoreCase);Assert.Equal(2,Count(html,"class=download"));Assert.Contains("position:fixed",html);Assert.Contains("ids.filter(id=>!done.has(id))",html);Assert.Contains("localStorage",html);Assert.DoesNotContain("session",html,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("capture",html,StringComparison.OrdinalIgnoreCase);
  }
  [Fact]public void CreateAssets_LabelsGifAndVideoSeparatelyAndNeverCreatesZip()
  {
   var root=NewDirectory();try{var gif=Path.Combine(root,"animation.gif");using(var image=new Bitmap(20,20))image.Save(gif,ImageFormat.Gif);var video=Path.Combine(root,"clip.mp4");File.WriteAllBytes(video,new byte[]{1,2,3});var thumbnails=Path.Combine(root,"thumbs");Directory.CreateDirectory(thumbnails);var assets=LocalShareService.CreateAssets(new[]{gif,video},thumbnails,CancellationToken.None);Assert.Equal(new[]{"Ảnh động 1","Video 1"},assets.Select(x=>x.Label).ToArray());Assert.Empty(Directory.EnumerateFiles(root,"*.zip",SearchOption.AllDirectories));Assert.All(assets,asset=>Assert.True(File.Exists(asset.ThumbnailPath)));}finally{Directory.Delete(root,true);}
  }
  static int Count(string value,string fragment){var count=0;for(var index=0;(index=value.IndexOf(fragment,index,StringComparison.Ordinal))>=0;index+=fragment.Length)count++;return count;}
  static string NewDirectory(){var path=Path.Combine(Path.GetTempPath(),"PhotoBooth-LocalShare-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
 }
}
