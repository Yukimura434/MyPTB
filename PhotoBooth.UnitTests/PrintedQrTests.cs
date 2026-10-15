using System;using System.Drawing;using System.IO;using System.Threading;using System.Threading.Tasks;using PhotoBooth.Core.Models;using PhotoBooth.Database;using PhotoBooth.Infrastructure.Services;using Xunit;
namespace PhotoBooth.UnitTests {
 public class PrintedQrTests {
  [Fact] public async Task Shared_settings_survive_reopen_and_new_frame_imports() {
   var dir=Path.Combine(Path.GetTempPath(),"qr-global-"+Guid.NewGuid());Directory.CreateDirectory(dir);
   try {
    var path=Path.Combine(dir,"test.db");var db=new SqliteDatabase(path);db.Initialize();
    var settings=new SqliteSettingsRepository(db);var before=await settings.GetAsync(CancellationToken.None);Assert.False(before.PrintedQr.Enabled);
    before.PrintedQr=new FrameQrSettings{Enabled=true,X=.4,Y=.5,Size=.12};before.QrPreviewImagePath="preview.png";before.PhotoCount=7;
    await settings.SaveAsync(before,CancellationToken.None);
    var frames=new SqliteFrameRepository(db);
    for(var i=0;i<20;i++)await frames.SaveAsync(new Frame{Id=Guid.NewGuid(),Name="Frame "+i,PixelWidth=1200,PixelHeight=1800,CreatedAtUtc=DateTime.UtcNow},CancellationToken.None);
    var reopened=new SqliteDatabase(path);reopened.Initialize();settings=new SqliteSettingsRepository(reopened);
    var stored=await settings.GetAsync(CancellationToken.None);Assert.True(stored.PrintedQr.Enabled);Assert.Equal(.4,stored.PrintedQr.X);Assert.Equal(.5,stored.PrintedQr.Y);Assert.Equal(.12,stored.PrintedQr.Size);Assert.Equal("preview.png",stored.QrPreviewImagePath);Assert.Equal(7,stored.PhotoCount);
    stored.CountdownSeconds=4;await settings.SaveAsync(stored,CancellationToken.None);
    Assert.True((await settings.GetAsync(CancellationToken.None)).PrintedQr.Enabled);
   } finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
  }
  [Theory][InlineData(1200,1800)][InlineData(1800,1200)]
  public async Task Final_image_contains_decodable_server_url_with_quiet_zone(int width,int height){var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".png");const string url="https://example.test/q/abcdefghijklmnopqrstuv";try{using(var image=new Bitmap(width,height)){using(var g=Graphics.FromImage(image))g.Clear(System.Drawing.Color.Coral);image.Save(path,System.Drawing.Imaging.ImageFormat.Png);}await new PrintedQrService().StampAsync(path,new FrameQrSettings{Enabled=true,X=0.5,Y=0.5,Size=0.16},url,CancellationToken.None);using(var bitmap=new Bitmap(path)){Assert.Equal(System.Drawing.Color.Coral.ToArgb(),bitmap.GetPixel(0,0).ToArgb());var result=new ZXing.BarcodeReader().Decode(bitmap);Assert.NotNull(result);Assert.Equal(url,result.Text);}}finally{File.Delete(path);}}
  [Fact]public async Task Disabled_does_not_change_bytes(){var path=Path.GetTempFileName();try{File.WriteAllText(path,"unchanged");await new PrintedQrService().StampAsync(path,new FrameQrSettings(),null,CancellationToken.None);Assert.Equal("unchanged",File.ReadAllText(path));}finally{File.Delete(path);}}
  [Fact]public async Task Settings_survive_reopen_and_other_frame_updates(){var dir=Path.Combine(Path.GetTempPath(),"qr-"+Guid.NewGuid());Directory.CreateDirectory(dir);try{var path=Path.Combine(dir,"test.db");var db=new SqliteDatabase(path);db.Initialize();var repo=new SqliteFrameRepository(db);var frame=new Frame{Id=Guid.NewGuid(),Name="frame",PixelWidth=1200,PixelHeight=1800,CreatedAtUtc=DateTime.UtcNow,PrintedQr=new FrameQrSettings{Enabled=true,X=.3,Y=.6,Size=.12}};await repo.SaveAsync(frame,CancellationToken.None);var other=new Frame{Id=Guid.NewGuid(),Name="old",PixelWidth=1200,PixelHeight=1800,CreatedAtUtc=DateTime.UtcNow};await repo.SaveAsync(other,CancellationToken.None);db=new SqliteDatabase(path);db.Initialize();repo=new SqliteFrameRepository(db);var stored=await repo.GetAsync(frame.Id,CancellationToken.None);Assert.True(stored.PrintedQr.Enabled);Assert.Equal(.3,stored.PrintedQr.X,6);Assert.Equal(.12,stored.PrintedQr.Size,6);stored.IsPinned=true;await repo.SaveAsync(stored,CancellationToken.None);Assert.True((await repo.GetAsync(frame.Id,CancellationToken.None)).PrintedQr.Enabled);Assert.False((await repo.GetAsync(other.Id,CancellationToken.None)).PrintedQr.Enabled);}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}}
  [Fact]public void Geometry_clamps_nan_and_extreme_aspect_without_leaving_image(){foreach(var dimensions in new[]{Tuple.Create(1,10000),Tuple.Create(1200,1800),Tuple.Create(1800,1200)}){var q=new FrameQrSettings{Enabled=true,X=double.NaN,Y=2,Size=1}.Normalize(dimensions.Item1,dimensions.Item2);Assert.True(q.X>=0&&q.Y>=0);Assert.True(q.X+q.Size*Math.Max(dimensions.Item1,dimensions.Item2)/dimensions.Item1<=1.00001);Assert.True(q.Y+q.Size*Math.Max(dimensions.Item1,dimensions.Item2)/dimensions.Item2<=1.00001);}}
 }
}
