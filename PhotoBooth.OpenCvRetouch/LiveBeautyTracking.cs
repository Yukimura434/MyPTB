using System;
using System.Linq;
using OpenCvSharp;
namespace PhotoBooth.OpenCvRetouch { internal static class LiveBeautyTracking {
 internal static bool Track(Mat before,Mat now,ref Point2f[][] faces, out double confidence)
 {
  confidence=1; using(var a=new Mat())using(var b=new Mat())
  {
   Cv2.Resize(before,a,new Size(0,0),.5,.5,InterpolationFlags.Area);Cv2.Resize(now,b,a.Size(),0,0,InterpolationFlags.Area);
   var p=faces.SelectMany(f=>f).Select(v=>new Point2f(v.X*.5f,v.Y*.5f)).ToArray();var moved=new Point2f[p.Length];var back=new Point2f[p.Length];byte[] ok,reverse;float[] errors,reverseErrors;
   Cv2.CalcOpticalFlowPyrLK(a,b,p,ref moved,out ok,out errors,new Size(21,21),2,null,OpticalFlowFlags.None,.0001);Cv2.CalcOpticalFlowPyrLK(b,a,moved,ref back,out reverse,out reverseErrors,new Size(21,21),2,null,OpticalFlowFlags.None,.0001);
   for(int i=0;i<p.Length;i++){double dx=p[i].X-back[i].X,dy=p[i].Y-back[i].Y;confidence=Math.Min(confidence,Math.Max(0,1-Math.Max(errors[i]/25d,Math.Sqrt(dx*dx+dy*dy)/.75)));if(ok[i]==0||reverse[i]==0||errors[i]>25||dx*dx+dy*dy>.75*.75||moved[i].X<0||moved[i].Y<0||moved[i].X>=b.Width||moved[i].Y>=b.Height)return false;}
   faces=Enumerable.Range(0,faces.Length).Select(i=>moved.Skip(i*68).Take(68).Select(v=>new Point2f(v.X*2,v.Y*2)).ToArray()).ToArray();return true;
  }
 }
 internal static unsafe void Light(Mat image,Mat mask,int smoothAmount,int brightAmount)
 {
  var bounds=Cv2.BoundingRect(mask);if(bounds.Width==0)return;int x=Math.Max(0,bounds.X-3),y=Math.Max(0,bounds.Y-3);var rect=new Rect(x,y,Math.Min(image.Width,bounds.Right+3)-x,Math.Min(image.Height,bounds.Bottom+3)-y);
  using(var part=new Mat(image,rect))using(var m=new Mat(mask,rect))using(var smooth=new Mat())
  {
   if(smoothAmount>0)Cv2.BilateralFilter(part,smooth,5,40,26);else part.CopyTo(smooth);int w=part.Width,h=part.Height;
   for(int yy=0;yy<h;yy++){byte* dst=(byte*)part.Ptr(yy),src=(byte*)smooth.Ptr(yy),row=(byte*)m.Ptr(yy);for(int xx=0;xx<w;xx++)if(row[xx]!=0)for(int c=0;c<3;c++){int i=xx*3+c;dst[i]=(byte)Math.Max(0,Math.Min(255,Math.Round(dst[i]+row[xx]/255d*((src[i]-dst[i])*Math.Min(100,Math.Max(0,smoothAmount))*.0063+Math.Min(100,Math.Max(0,brightAmount))*.06))));}}
  }
 }
}}
