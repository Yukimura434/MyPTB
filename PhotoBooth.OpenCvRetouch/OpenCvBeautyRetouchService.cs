using System;
using System.Diagnostics;
using System.IO;
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
        readonly string detectorPath;
        readonly string modelPath;
        YuNetFaceDetector detector;
        FacemarkLBF facemark;
        bool disposed;

        public OpenCvBeautyRetouchService() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Beauty")) { }
        public OpenCvBeautyRetouchService(string assetsDirectory)
        {
            BeautyRuntime.EnsureConfigured();
            detectorPath = Path.Combine(assetsDirectory, YuNetFaceDetector.ModelFileName);
            modelPath = Path.Combine(assetsDirectory, "lbfmodel.yaml");
        }

        public async Task<BeautyRetouchResult> ProcessAsync(string inputPath, string outputPath, BeautySettings settings, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("Input image path is required.", nameof(inputPath));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output image path is required.", nameof(outputPath));
            settings = settings?.Clone() ?? new BeautySettings();
            settings.ClampStrengths();
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
                    var faces=detector.Detect(analysis);
                    if(faces.Length==0){WriteValidated(inputPath,outputPath,null,token);return new BeautyRetouchResult{ElapsedMilliseconds=watch.ElapsedMilliseconds};}
                    Point2f[][] points; using(var faceMat=Mat.FromArray(faces)) if(!facemark.Fit(gray,faceMat,out points)) throw new InvalidOperationException("FacemarkLBF failed to fit detected faces.");
                    token.ThrowIfCancellationRequested();
                    // Own concrete buffers: Mat.Zeros returns a lazy MatExpr, whose
                    // implicit conversions would discard the masks written below.
                    using(var skin=new Mat(original.Size(),MatType.CV_8UC1,Scalar.Black))
                    using(var feature=new Mat(original.Size(),MatType.CV_8UC1,Scalar.Black))
                    using(var underEye=new Mat(original.Size(),MatType.CV_8UC1,Scalar.Black))
                    {
                        FaceSkinMaskBuilder.Build(original,points,scale,skin,feature,underEye);
                        using(var result=original.Clone())
                        {
                            ApplyBlemishRemoval(result,skin,settings.BlemishRemoval);
                            token.ThrowIfCancellationRequested();
                            ApplyEffects(result,result,skin,feature,settings,token);
                            FaceGeometry.Apply(result, points, scale, settings, token);
                            token.ThrowIfCancellationRequested();
                            WriteValidated(inputPath,outputPath,result,token);
                        }
                    }
                    return new BeautyRetouchResult{Applied=true,FacesDetected=points.Length,ElapsedMilliseconds=watch.ElapsedMilliseconds};
                }
            }
        }

        internal static void ApplyBlemishRemoval(Mat image,Mat skinMask,int amount)
        {
            if(amount<=0||skinMask==null)return;
            using(var gray=new Mat())using(var baseTone=new Mat())using(var texture=new Mat())using(var redness=new Mat())using(var averageBg=new Mat())using(var candidates=new Mat())using(var skinBinary=new Mat())using(var repaired=new Mat())
            {
                Cv2.CvtColor(image,gray,ColorConversionCodes.BGR2GRAY);Cv2.GaussianBlur(gray,baseTone,new Size(0,0),2.4);
                // Signed dark contrast selects the spot itself, not the bright blur
                // halo around it, which would join small spots into large repairs.
                Cv2.Subtract(baseTone,gray,texture);Cv2.Threshold(texture,candidates,Math.Max(4,15-amount*.1),255,ThresholdTypes.Binary);
                var channels=image.Split();try{Cv2.AddWeighted(channels[0],.5,channels[1],.5,0,averageBg);Cv2.Subtract(channels[2],averageBg,redness);using(var localRed=new Mat()){Cv2.GaussianBlur(redness,localRed,new Size(0,0),3.4);Cv2.Subtract(redness,localRed,redness);}Cv2.Threshold(redness,redness,Math.Max(7,22-amount*.15),255,ThresholdTypes.Binary);Cv2.BitwiseOr(candidates,redness,candidates);}finally{foreach(var channel in channels)channel.Dispose();}
                Cv2.Threshold(skinMask,skinBinary,80,255,ThresholdTypes.Binary);Cv2.BitwiseAnd(candidates,skinBinary,candidates);
                using(var kernel=Cv2.GetStructuringElement(MorphShapes.Ellipse,new Size(3,3))){Cv2.MorphologyEx(candidates,candidates,MorphTypes.Open,kernel);KeepSmallBlemishes(candidates,Math.Min(400,Math.Max(16,Cv2.CountNonZero(skinBinary)*.0008)));Cv2.Dilate(candidates,candidates,kernel,iterations:amount>=60?2:1);}
                // Dilation must not regrow repairs over eyes, lips, hair or outside skin.
                Cv2.Min(candidates,skinMask,candidates);
                if(Cv2.CountNonZero(candidates)==0)return;
                Cv2.Inpaint(image,candidates,repaired,Math.Max(2,2+amount*.035),InpaintMethod.Telea);
                // A broad cheek/lighting region is not a blemish. Also bound any
                // isolated repair's color excursion before blending it into skin.
                using(var low=new Mat())using(var high=new Mat())
                {Cv2.Subtract(image,Scalar.All(24),low);Cv2.Add(image,Scalar.All(24),high);Cv2.Max(repaired,low,repaired);Cv2.Min(repaired,high,repaired);}
                BlendMasked(image,repaired,candidates,Math.Min(1,.4+amount*.006));
            }
        }

        static unsafe void KeepSmallBlemishes(Mat mask,double maxArea)
        {
            using(var labels=new Mat())using(var stats=new Mat())using(var centers=new Mat())
            {
                int count=Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers);
                var keep=new bool[count];
                for(int i=1;i<count;i++)keep[i]=stats.At<int>(i,4)<=maxArea&&stats.At<int>(i,2)<=Math.Sqrt(maxArea)*2&&stats.At<int>(i,3)<=Math.Sqrt(maxArea)*2;
                for(int y=0;y<mask.Height;y++){var row=(byte*)mask.Ptr(y);var ids=(int*)labels.Ptr(y);for(int x=0;x<mask.Width;x++)if(!keep[ids[x]])row[x]=0;}
            }
        }

        internal static void ApplyDarkCircleReduction(Mat image,Mat mask,int amount)
        {
            if(amount<=0||mask==null||Cv2.CountNonZero(mask)==0)return;
            using(var changed=new Mat()){Cv2.Add(image,new Scalar(4+amount*.25,4+amount*.25,4+amount*.25),changed);BlendMasked(image,changed,mask,Math.Min(.9,amount*.009));}
        }

        internal static void ApplyEffects(Mat original,Mat result,Mat skinMask,Mat featureMask,BeautySettings s,CancellationToken token)
        {
            if(s.SmoothSkin>0&&skinMask!=null){using(var filtered=new Mat()) {var diameter=Math.Max(7,Math.Min(13,(Math.Min(original.Width,original.Height)/180)|1));var sigma=20+s.SmoothSkin*.75;Cv2.BilateralFilter(original,filtered,diameter,sigma,sigma*.65);BlendMasked(result,filtered,skinMask,Math.Min(.92,s.SmoothSkin*.009));}}
            token.ThrowIfCancellationRequested();
            if(s.BrightenSkin>0&&skinMask!=null){using(var bright=new Mat()){result.ConvertTo(bright,-1,1,3+s.BrightenSkin*.28);BlendMasked(result,bright,skinMask,s.BrightenSkin/100d);}}
            if(s.SkinTone>0&&skinMask!=null){using(var toned=result.Clone()){var warm=s.SkinTone/100d;Cv2.Add(toned,new Scalar(-7*warm,2*warm,12*warm),toned);BlendMasked(result,toned,skinMask,.75);}}
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

        static Mat ResizeForAnalysis(Mat source,int max,out double scale){scale=Math.Min(1d,max/(double)Math.Max(source.Width,source.Height));if(scale>=.999)return source.Clone();var output=new Mat();Cv2.Resize(source,output,new Size(0,0),scale,scale,InterpolationFlags.Area);return output;}
        void EnsureLoaded(){if(disposed)throw new ObjectDisposedException(nameof(OpenCvBeautyRetouchService));if(detector!=null)return;if(!File.Exists(detectorPath))throw new FileNotFoundException("Beauty face detector is missing.",detectorPath);if(!File.Exists(modelPath))throw new FileNotFoundException("Beauty landmark model is missing.",modelPath);var loaded=FacemarkLBF.Create();try{loaded.LoadModel(modelPath);var detected=new YuNetFaceDetector(detectorPath);facemark=loaded;detector=detected;}catch{loaded.Dispose();throw;}}
        static void WriteValidated(string input,string output,Mat image,CancellationToken token){var full=Path.GetFullPath(output);var dir=Path.GetDirectoryName(full);Directory.CreateDirectory(dir);var temp=Path.Combine(dir,Path.GetFileName(full)+"."+Guid.NewGuid().ToString("N")+".tmp.jpg");try{if(image==null)File.Copy(input,temp,true);else if(!Cv2.ImWrite(temp,image))throw new IOException("Unable to encode Beauty output.");token.ThrowIfCancellationRequested();using(var check=Cv2.ImRead(temp,ImreadModes.Color))if(check.Empty())throw new InvalidDataException("Beauty output validation failed.");if(File.Exists(full))File.Delete(full);File.Move(temp,full);}finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}}
        static bool Same(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
        public void Dispose(){if(disposed)return;disposed=true;facemark?.Dispose();detector?.Dispose();gate.Dispose();}
    }
}
