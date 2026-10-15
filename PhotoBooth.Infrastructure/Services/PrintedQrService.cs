using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
namespace PhotoBooth.Infrastructure.Services
{
 public sealed class PrintedQrService : IPrintedQrService
 {
  public Task StampAsync(string imagePath,FrameQrSettings settings,string galleryUrl,CancellationToken token) => Task.Run(()=>{
   if(settings?.Enabled!=true)return;
   if(!Uri.TryCreate(galleryUrl,UriKind.Absolute,out var uri)||(uri.Scheme!="https"&&uri.Scheme!="http"))throw new InvalidOperationException("Chưa có mã nhận ảnh hợp lệ để in.");
   token.ThrowIfCancellationRequested();var temporary=imagePath+".qr.png";
   try { using(var original=new Bitmap(imagePath))using(var image=new Bitmap(original)){
    image.SetResolution(300,300);var q=settings.Normalize(image.Width,image.Height);
    var size=Math.Max(1,(int)Math.Round(q.Size*Math.Max(image.Width,image.Height)));
    var matrix=new ZXing.QrCode.QRCodeWriter().encode(uri.AbsoluteUri,ZXing.BarcodeFormat.QR_CODE,0,0,new System.Collections.Generic.Dictionary<ZXing.EncodeHintType,object>{{ZXing.EncodeHintType.MARGIN,4},{ZXing.EncodeHintType.ERROR_CORRECTION,ZXing.QrCode.Internal.ErrorCorrectionLevel.M}});
    var scale=size/matrix.Width;if(scale<2)throw new InvalidOperationException("QR quá nhỏ cho đường dẫn này. Hãy tăng kích thước QR.");
    var edge=scale*matrix.Width;var x=Math.Min(image.Width-edge,(int)Math.Round(q.X*image.Width));var y=Math.Min(image.Height-edge,(int)Math.Round(q.Y*image.Height));
    using(var g=Graphics.FromImage(image)){g.FillRectangle(Brushes.White,x,y,edge,edge);for(var row=0;row<matrix.Height;row++)for(var col=0;col<matrix.Width;col++)if(matrix[col,row])g.FillRectangle(Brushes.Black,x+col*scale,y+row*scale,scale,scale);}
    token.ThrowIfCancellationRequested();image.Save(temporary,ImageFormat.Png);
   }token.ThrowIfCancellationRequested();File.Copy(temporary,imagePath,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
  },token);
 }
}
