using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Face;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.OpenCvRetouch
{
    public sealed class OpenCvBeautyRetouchService : IBeautyRetouchService, IDisposable
    {
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly string cascadePath;
        readonly string modelPath;
        CascadeClassifier detector;
        FacemarkLBF facemark;
        bool disposed;

        public OpenCvBeautyRetouchService() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Beauty")) { }
        public OpenCvBeautyRetouchService(string assetsDirectory)
        {
            cascadePath = Path.Combine(assetsDirectory, "frontface.xml");
            modelPath = Path.Combine(assetsDirectory, "lbfmodel.yaml");
        }

        public async Task<BeautyRetouchResult> ProcessAsync(string inputPath, string outputPath, BeautySettings settings, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("Input image path is required.", nameof(inputPath));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output image path is required.", nameof(outputPath));
            settings = settings?.Clone() ?? new BeautySettings();
            if (!settings.HasEffect) { if (!Same(inputPath, outputPath)) File.Copy(inputPath, outputPath, true); return new BeautyRetouchResult(); }
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { return await Task.Run(() => ProcessCore(inputPath, outputPath, settings, token), token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        BeautyRetouchResult ProcessCore(string inputPath, string outputPath, BeautySettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); EnsureLoaded(); var watch=Stopwatch.StartNew();
            using(var original=Cv2.ImRead(inputPath,ImreadModes.Color))
            {
                if(original.Empty()) throw new InvalidDataException("Unable to decode Beauty input image.");
                using(var analysis=ResizeForAnalysis(original,1280,out var scale))
                using(var gray=new Mat())
                {
                    Cv2.CvtColor(analysis,gray,ColorConversionCodes.BGR2GRAY);Cv2.EqualizeHist(gray,gray);
                    var faces=detector.DetectMultiScale(gray,1.1,3,HaarDetectionTypes.ScaleImage,new Size(40,40));
                    if(faces.Length==0){WriteValidated(inputPath,outputPath,null,token);return new BeautyRetouchResult{ElapsedMilliseconds=watch.ElapsedMilliseconds};}
                    Point2f[][] points; using(var faceMat=Mat.FromArray(faces)) if(!facemark.Fit(gray,faceMat,out points)) throw new InvalidOperationException("FacemarkLBF failed to fit detected faces.");
                    token.ThrowIfCancellationRequested();
                    using(var skin=Mat.Zeros(original.Size(),MatType.CV_8UC1))
                    using(var feature=Mat.Zeros(original.Size(),MatType.CV_8UC1))
                    {
                        for(var i=0;i<points.Length;i++) BuildMasks(points[i],scale,skin,feature);
                        using(var featheredSkin=new Mat()) using(var featheredFeature=new Mat())
                        {
                            Cv2.GaussianBlur(skin,featheredSkin,new Size(0,0),Math.Max(2,original.Width/500d));
                            Cv2.GaussianBlur(feature,featheredFeature,new Size(0,0),Math.Max(1,original.Width/900d));
                            using(var result=original.Clone())
                            {
                                ApplyEffects(original,result,featheredSkin,featheredFeature,settings,token);
                                if(settings.EyeSize>0||settings.SlimFace>0) foreach(var facePoints in points){token.ThrowIfCancellationRequested();ApplyGeometry(result,facePoints,scale,settings);}
                                WriteValidated(inputPath,outputPath,result,token);
                            }
                        }
                    }
                    return new BeautyRetouchResult{Applied=true,FacesDetected=points.Length,ElapsedMilliseconds=watch.ElapsedMilliseconds};
                }
            }
        }

        internal static void ApplyEffects(Mat original,Mat result,Mat skinMask,Mat featureMask,BeautySettings s,CancellationToken token)
        {
            if(s.SmoothSkin>0&&skinMask!=null){using(var filtered=new Mat()) {var sigma=15+s.SmoothSkin*.55;Cv2.BilateralFilter(original,filtered,7,sigma,sigma*.6);BlendMasked(result,filtered,skinMask,s.SmoothSkin*.0075);}}
            token.ThrowIfCancellationRequested();
            if(s.BrightenSkin>0&&skinMask!=null){using(var bright=new Mat()){result.ConvertTo(bright,-1,1,2+s.BrightenSkin*.18);BlendMasked(result,bright,skinMask,s.BrightenSkin/100d);}}
            if(s.SkinTone>0&&skinMask!=null){using(var toned=result.Clone()){var warm=s.SkinTone/100d;Cv2.Add(toned,new Scalar(-4*warm,1*warm,7*warm),toned);BlendMasked(result,toned,skinMask,.75);}}
            token.ThrowIfCancellationRequested();
            if(s.Sharpen>0&&featureMask!=null){using(var blur=new Mat())using(var sharp=new Mat()){Cv2.GaussianBlur(result,blur,new Size(0,0),1.1);Cv2.AddWeighted(result,1+s.Sharpen*.008,blur,-s.Sharpen*.008,0,sharp);BlendMasked(result,sharp,featureMask,Math.Min(.85,s.Sharpen/100d));}}
        }

        static void BlendMasked(Mat destination,Mat effect,Mat mask,double strength)
        {
            if(destination==null||effect==null||mask==null||destination.Empty()||effect.Empty()||mask.Empty()||strength<=0)return;
            if(destination.Size()!=effect.Size()||destination.Size()!=mask.Size())throw new ArgumentException("Blend inputs must have identical dimensions.");

            // Full-resolution CV_32FC3 buffers are prohibitively large for camera images
            // (4160x2768 needs about 138 MB per buffer). Process small tiles so peak native
            // memory stays bounded while preserving the same soft-mask blend equation.
            var boundedStrength=Math.Min(1d,strength);
            const int tileSize=512;
            for(var y=0;y<destination.Height;y+=tileSize)
            for(var x=0;x<destination.Width;x+=tileSize)
            {
                var width=Math.Min(tileSize,destination.Width-x);var height=Math.Min(tileSize,destination.Height-y);
                var roi=new Rect(x,y,width,height);
                using(var maskTile=new Mat(mask,roi))
                {
                    if(Cv2.CountNonZero(maskTile)==0)continue;
                    using(var alpha=new Mat())using(var alpha3=new Mat())using(var dst32=new Mat())using(var effect32=new Mat())
                    using(var delta=new Mat())using(var blended=new Mat())
                    {
                        maskTile.ConvertTo(alpha,MatType.CV_32FC1,boundedStrength/255d);
                        Cv2.CvtColor(alpha,alpha3,ColorConversionCodes.GRAY2BGR);
                        using(var destinationTile=new Mat(destination,roi))using(var effectTile=new Mat(effect,roi))
                        {
                            destinationTile.ConvertTo(dst32,MatType.CV_32FC3);
                            effectTile.ConvertTo(effect32,MatType.CV_32FC3);
                            Cv2.Subtract(effect32,dst32,delta);
                            Cv2.Multiply(delta,alpha3,delta);
                            Cv2.Add(dst32,delta,blended);
                            blended.ConvertTo(destinationTile,MatType.CV_8UC3);
                        }
                    }
                }
            }
        }

        internal static void BuildMasks(Point2f[] source,double scale,Mat skin,Mat feature)
        {
            if(source==null||source.Length!=68)return;Func<int,Point> p=i=>new Point(Math.Round(source[i].X/scale),Math.Round(source[i].Y/scale));
            var face=new List<Point>();for(var i=0;i<=16;i++)face.Add(p(i));for(var i=26;i>=17;i--)face.Add(p(i));
            if(skin!=null){Cv2.FillConvexPoly(skin,face.ToArray(),Scalar.White,LineTypes.AntiAlias);Exclude(skin,Enumerable.Range(36,6).Select(p).ToArray());Exclude(skin,Enumerable.Range(42,6).Select(p).ToArray());Exclude(skin,Enumerable.Range(48,12).Select(p).ToArray());}
            if(feature!=null){Cv2.FillConvexPoly(feature,Enumerable.Range(36,6).Select(p).ToArray(),Scalar.White,LineTypes.AntiAlias);Cv2.FillConvexPoly(feature,Enumerable.Range(42,6).Select(p).ToArray(),Scalar.White,LineTypes.AntiAlias);Cv2.Polylines(feature,new[]{face.ToArray()},true,Scalar.White,Math.Max(3,feature.Width/300),LineTypes.AntiAlias);}
        }
        static void Exclude(Mat mask,Point[] polygon)=>Cv2.FillConvexPoly(mask,polygon,Scalar.Black,LineTypes.AntiAlias);
        internal static unsafe void ApplyGeometry(Mat image,Point2f[] source,double scale,BeautySettings settings)
        {
            if(source==null||source.Length!=68)return;
            var points=source.Select(v=>new Point2f((float)(v.X/scale),(float)(v.Y/scale))).ToArray();
            var minX=points.Take(27).Min(v=>v.X);var maxX=points.Take(27).Max(v=>v.X);var minY=points.Take(27).Min(v=>v.Y);var maxY=points.Take(27).Max(v=>v.Y);
            var padX=(maxX-minX)*.18f;var padY=(maxY-minY)*.18f;
            var left=Math.Max(0,(int)Math.Floor(minX-padX));var top=Math.Max(0,(int)Math.Floor(minY-padY));var right=Math.Min(image.Width,(int)Math.Ceiling(maxX+padX));var bottom=Math.Min(image.Height,(int)Math.Ceiling(maxY+padY));
            if(right-left<4||bottom-top<4)return;var roi=new Rect(left,top,right-left,bottom-top);
            using(var view=new Mat(image,roi))using(var input=view.Clone())using(var output=new Mat())using(var mapX=new Mat(roi.Height,roi.Width,MatType.CV_32FC1))using(var mapY=new Mat(roi.Height,roi.Width,MatType.CV_32FC1))
            {
                var mx=(float*)mapX.DataPointer;var my=(float*)mapY.DataPointer;
                var faceCx=points[30].X;var browY=(points[19].Y+points[24].Y)*.5f;var faceRx=Math.Max(1,(points[16].X-points[0].X)*.5f);var faceRy=Math.Max(1,points[8].Y-browY);
                var leftEye=Center(points,36,42);var rightEye=Center(points,42,48);var eyeRadius=Math.Max(4,(Distance(points[36],points[39])+Distance(points[42],points[45]))*.65f);
                var slim=settings.SlimFace/100f*.13f;var enlarge=settings.EyeSize/100f*.16f;
                for(var y=0;y<roi.Height;y++)for(var x=0;x<roi.Width;x++)
                {
                    var gx=x+left;var gy=y+top;var sx=(float)gx;var sy=(float)gy;
                    if(slim>0&&gy>browY){var nx=(gx-faceCx)/faceRx;var ny=(gy-browY)/faceRy;var d=nx*nx+ny*ny;if(d<1.25f){var fall=(float)Math.Max(0,1-d/1.25f);sx=faceCx+(gx-faceCx)*(1+slim*fall);}}
                    if(enlarge>0){ApplyEyeInverse(ref sx,ref sy,gx,gy,leftEye,eyeRadius,enlarge);ApplyEyeInverse(ref sx,ref sy,gx,gy,rightEye,eyeRadius,enlarge);}
                    var index=y*roi.Width+x;mx[index]=sx-left;my[index]=sy-top;
                }
                Cv2.Remap(input,output,mapX,mapY,InterpolationFlags.Linear,BorderTypes.Reflect101);output.CopyTo(view);
            }
        }
        static Point2f Center(Point2f[] p,int start,int end){float x=0,y=0;for(var i=start;i<end;i++){x+=p[i].X;y+=p[i].Y;}var n=end-start;return new Point2f(x/n,y/n);}
        static float Distance(Point2f a,Point2f b){var x=a.X-b.X;var y=a.Y-b.Y;return (float)Math.Sqrt(x*x+y*y);}
        static void ApplyEyeInverse(ref float sx,ref float sy,float x,float y,Point2f center,float radius,float amount){var dx=x-center.X;var dy=y-center.Y;var d=(float)Math.Sqrt(dx*dx+dy*dy);var limit=radius*2.2f;if(d>=limit)return;var fall=1-d/limit;var factor=1-amount*fall*fall;sx=center.X+(sx-center.X)*factor;sy=center.Y+(sy-center.Y)*factor;}
        static Mat ResizeForAnalysis(Mat source,int max,out double scale){scale=Math.Min(1d,max/(double)Math.Max(source.Width,source.Height));if(scale>=.999)return source.Clone();var output=new Mat();Cv2.Resize(source,output,new Size(0,0),scale,scale,InterpolationFlags.Area);return output;}
        void EnsureLoaded(){if(disposed)throw new ObjectDisposedException(nameof(OpenCvBeautyRetouchService));if(detector!=null)return;if(!File.Exists(cascadePath))throw new FileNotFoundException("Beauty face detector is missing.",cascadePath);if(!File.Exists(modelPath))throw new FileNotFoundException("Beauty landmark model is missing.",modelPath);detector=new CascadeClassifier(cascadePath);if(detector.Empty())throw new InvalidDataException("Beauty face detector cannot be loaded.");facemark=FacemarkLBF.Create();facemark.LoadModel(modelPath);}
        static void WriteValidated(string input,string output,Mat image,CancellationToken token){var full=Path.GetFullPath(output);var dir=Path.GetDirectoryName(full);Directory.CreateDirectory(dir);var temp=Path.Combine(dir,Path.GetFileName(full)+"."+Guid.NewGuid().ToString("N")+".tmp.jpg");try{if(image==null)File.Copy(input,temp,true);else if(!Cv2.ImWrite(temp,image))throw new IOException("Unable to encode Beauty output.");token.ThrowIfCancellationRequested();using(var check=Cv2.ImRead(temp,ImreadModes.Color))if(check.Empty())throw new InvalidDataException("Beauty output validation failed.");if(File.Exists(full))File.Delete(full);File.Move(temp,full);}finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}}
        static bool Same(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
        public void Dispose(){if(disposed)return;disposed=true;facemark?.Dispose();detector?.Dispose();gate.Dispose();}
    }
}
