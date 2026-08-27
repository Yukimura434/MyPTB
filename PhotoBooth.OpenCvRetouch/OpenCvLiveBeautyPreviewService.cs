using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Face;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
namespace PhotoBooth.OpenCvRetouch
{
    public sealed class OpenCvLiveBeautyPreviewService : ILiveBeautyPreviewService, IDisposable
    {
        const int AnalysisInterval=6;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);readonly string cascadePath,modelPath;
        CascadeClassifier detector;FacemarkLBF facemark;Point2f[][] landmarks=new Point2f[0][];double landmarkScale=1;int frameCounter,width,height;bool disposed;
        Mat cachedSkinMask,cachedFeatureMask;byte[] lastInput,lastOutput;int lastSettingsKey;
        public OpenCvLiveBeautyPreviewService():this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","Beauty")){}
        public OpenCvLiveBeautyPreviewService(string assets){cascadePath=Path.Combine(assets,"frontface.xml");modelPath=Path.Combine(assets,"lbfmodel.yaml");}
        public async Task<byte[]> ProcessAsync(byte[] jpegData,BeautySettings settings,CancellationToken token)
        {
            if(jpegData==null||jpegData.Length==0||settings==null||!settings.HasEffect)return jpegData;
            if(!await gate.WaitAsync(0,token).ConfigureAwait(false))return jpegData;
            try{return await Task.Run(()=>ProcessCore(jpegData,settings,token),token).ConfigureAwait(false);}finally{gate.Release();}
        }
        byte[] ProcessCore(byte[] jpeg,BeautySettings settings,CancellationToken token)
        {
            EnsureLoaded();token.ThrowIfCancellationRequested();var settingsKey=SettingsKey(settings);if(settingsKey==lastSettingsKey&&SameBytes(jpeg,lastInput)&&lastOutput!=null)return lastOutput;
            using(var original=Cv2.ImDecode(jpeg,ImreadModes.Color)){if(original.Empty())return jpeg;
                if(original.Width!=width||original.Height!=height){width=original.Width;height=original.Height;frameCounter=0;landmarks=new Point2f[0][];ClearMasks();}
                if(frameCounter++%AnalysisInterval==0||landmarks.Length==0){Analyze(original);ClearMasks();}
                if(landmarks.Length==0){Remember(jpeg,jpeg,settingsKey);return jpeg;}var light=Light(settings);EnsureMasks(original.Size(),light);
                using(var result=original.Clone())
                {
                    OpenCvBeautyRetouchService.ApplyEffects(original,result,cachedSkinMask,cachedFeatureMask,light,token);
                    if(light.EyeSize>0||light.SlimFace>0)foreach(var face in landmarks)OpenCvBeautyRetouchService.ApplyGeometry(result,face,landmarkScale,light);
                    var encoded=result.ImEncode(".jpg",new[]{(int)ImwriteFlags.JpegQuality,88});Remember(jpeg,encoded,settingsKey);return encoded;
                }}
        }
        void EnsureMasks(Size size,BeautySettings settings)
        {
            var needSkin=settings.SmoothSkin>0||settings.BrightenSkin>0||settings.SkinTone>0;var needFeature=settings.Sharpen>0;
            if(needSkin&&cachedSkinMask==null){using(var raw=Mat.Zeros(size,MatType.CV_8UC1)){foreach(var face in landmarks)OpenCvBeautyRetouchService.BuildMasks(face,landmarkScale,raw,null);cachedSkinMask=new Mat();Cv2.GaussianBlur(raw,cachedSkinMask,new Size(0,0),Math.Max(1.5,width/600d));}}
            if(needFeature&&cachedFeatureMask==null){using(var raw=Mat.Zeros(size,MatType.CV_8UC1)){foreach(var face in landmarks)OpenCvBeautyRetouchService.BuildMasks(face,landmarkScale,null,raw);cachedFeatureMask=new Mat();Cv2.GaussianBlur(raw,cachedFeatureMask,new Size(0,0),1.2);}}
        }
        void Analyze(Mat original)
        {
            var scale=Math.Min(1d,640d/Math.Max(original.Width,original.Height));using(var analysis=new Mat())using(var gray=new Mat())
            {if(scale<.999)Cv2.Resize(original,analysis,new Size(0,0),scale,scale,InterpolationFlags.Area);else original.CopyTo(analysis);Cv2.CvtColor(analysis,gray,ColorConversionCodes.BGR2GRAY);Cv2.EqualizeHist(gray,gray);var faces=detector.DetectMultiScale(gray,1.15,3,HaarDetectionTypes.ScaleImage,new Size(35,35));Point2f[][] found;if(faces.Length==0){landmarks=new Point2f[0][];landmarkScale=scale;return;}using(var faceMat=Mat.FromArray(faces)){if(!facemark.Fit(gray,faceMat,out found))found=new Point2f[0][];}landmarks=found;landmarkScale=scale;}
        }
        static BeautySettings Light(BeautySettings s)=>new BeautySettings{Enabled=s.Enabled,SmoothSkin=(int)(s.SmoothSkin*.55),BrightenSkin=(int)(s.BrightenSkin*.7),SkinTone=(int)(s.SkinTone*.6),Sharpen=(int)(s.Sharpen*.65),EyeSize=(int)(s.EyeSize*.6),SlimFace=(int)(s.SlimFace*.6)};
        void EnsureLoaded(){if(disposed)throw new ObjectDisposedException(nameof(OpenCvLiveBeautyPreviewService));if(detector!=null)return;if(!File.Exists(cascadePath)||!File.Exists(modelPath))throw new FileNotFoundException("Live Beauty model assets are unavailable.");detector=new CascadeClassifier(cascadePath);facemark=FacemarkLBF.Create();facemark.LoadModel(modelPath);}
        static bool SameBytes(byte[] a,byte[] b){if(a==null||b==null||a.Length!=b.Length)return false;for(var i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        static int SettingsKey(BeautySettings s){unchecked{var h=s.SmoothSkin;h=h*397^s.BrightenSkin;h=h*397^s.SkinTone;h=h*397^s.Sharpen;h=h*397^s.EyeSize;return h*397^s.SlimFace;}}
        void Remember(byte[] input,byte[] output,int settingsKey){lastInput=(byte[])input.Clone();lastOutput=output;lastSettingsKey=settingsKey;}
        void ClearMasks(){cachedSkinMask?.Dispose();cachedSkinMask=null;cachedFeatureMask?.Dispose();cachedFeatureMask=null;lastInput=null;lastOutput=null;lastSettingsKey=0;}
        public void Reset(){if(!gate.Wait(0))return;try{landmarks=new Point2f[0][];frameCounter=0;width=height=0;ClearMasks();}finally{gate.Release();}}
        public void Dispose(){if(disposed)return;disposed=true;ClearMasks();facemark?.Dispose();detector?.Dispose();gate.Dispose();}
    }
}
