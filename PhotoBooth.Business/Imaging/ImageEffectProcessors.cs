using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Imaging
{
    internal static class ImageEffectIo
    {
        public static Task<string> Transform(string input, string effect, CancellationToken token, Func<Bitmap, Bitmap> transform)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using (var source = new Bitmap(input))
                using (var result = transform(source))
                {
                    var path = Path.Combine(Path.GetTempPath(), "photobooth-" + effect + "-" + Guid.NewGuid().ToString("N") + ".png");
                    result.SetResolution(source.HorizontalResolution > 0 ? source.HorizontalResolution : 300, source.VerticalResolution > 0 ? source.VerticalResolution : 300);
                    result.Save(path, ImageFormat.Png);
                    return path;
                }
            }, token);
        }

        public static Bitmap Pixels(Bitmap source, Func<Color, int, int, Color> map)
        {
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            result.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            for (var y = 0; y < source.Height; y++)
                for (var x = 0; x < source.Width; x++) result.SetPixel(x, y, map(source.GetPixel(x, y), x, y));
            return result;
        }

        public static int Clamp(double value) => (int)Math.Max(0, Math.Min(255, value));
        public static Color Rgb(Color c, double r, double g, double b) => Color.FromArgb(c.A, Clamp(r), Clamp(g), Clamp(b));
    }

    public abstract class ImageEffectProcessorBase : IImageEffectProcessor
    {
        public abstract string Name { get; }
        protected abstract bool Enabled(PresetProcessingOptions o);
        protected abstract Bitmap Apply(Bitmap bitmap, PresetProcessingOptions options);
        public Task<string> ProcessAsync(string inputPath, PresetProcessingOptions options, CancellationToken token) =>
            !Enabled(options) ? Task.FromResult(inputPath) : ImageEffectIo.Transform(inputPath, Name, token, b => Apply(b, options));
    }

    public sealed class BrightnessProcessor : ImageEffectProcessorBase { public override string Name => "brightness"; protected override bool Enabled(PresetProcessingOptions o)=>Math.Abs(o.Brightness)>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>ImageEffectIo.Rgb(c,c.R+o.Brightness*255,c.G+o.Brightness*255,c.B+o.Brightness*255)); }
    public sealed class ContrastProcessor : ImageEffectProcessorBase { public override string Name=>"contrast"; protected override bool Enabled(PresetProcessingOptions o)=>Math.Abs(o.Contrast)>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o){var f=Math.Pow((100+o.Contrast*100)/100,2);return ImageEffectIo.Pixels(b,(c,x,y)=>ImageEffectIo.Rgb(c,(((c.R/255d-.5)*f)+.5)*255,(((c.G/255d-.5)*f)+.5)*255,(((c.B/255d-.5)*f)+.5)*255));} }
    public sealed class SaturationProcessor : ImageEffectProcessorBase { public override string Name=>"saturation"; protected override bool Enabled(PresetProcessingOptions o)=>Math.Abs(o.Saturation)>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>{var l=.2126*c.R+.7152*c.G+.0722*c.B;var f=1+o.Saturation;return ImageEffectIo.Rgb(c,l+(c.R-l)*f,l+(c.G-l)*f,l+(c.B-l)*f);}); }
    public sealed class GammaProcessor : ImageEffectProcessorBase { public override string Name=>"gamma"; protected override bool Enabled(PresetProcessingOptions o)=>Math.Abs(o.Gamma-1)>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>ImageEffectIo.Rgb(c,255*Math.Pow(c.R/255d,1/o.Gamma),255*Math.Pow(c.G/255d,1/o.Gamma),255*Math.Pow(c.B/255d,1/o.Gamma))); }
    public sealed class ExposureProcessor : ImageEffectProcessorBase { public override string Name=>"exposure"; protected override bool Enabled(PresetProcessingOptions o)=>Math.Abs(o.Exposure)>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o){var f=Math.Pow(2,o.Exposure);return ImageEffectIo.Pixels(b,(c,x,y)=>ImageEffectIo.Rgb(c,c.R*f,c.G*f,c.B*f));} }
    public sealed class TemperatureProcessor : ImageEffectProcessorBase { public override string Name=>"temperature"; protected override bool Enabled(PresetProcessingOptions o)=>Math.Abs(o.Temperature)>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>ImageEffectIo.Rgb(c,c.R+o.Temperature*35,c.G,c.B-o.Temperature*35)); }
    public sealed class TintProcessor : ImageEffectProcessorBase { public override string Name=>"tint"; protected override bool Enabled(PresetProcessingOptions o)=>Math.Abs(o.Tint)>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>ImageEffectIo.Rgb(c,c.R+o.Tint*15,c.G-o.Tint*25,c.B+o.Tint*15)); }
    public sealed class BlackAndWhiteProcessor : ImageEffectProcessorBase { public override string Name=>"bw"; protected override bool Enabled(PresetProcessingOptions o)=>o.BlackAndWhite; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>{var l=.2126*c.R+.7152*c.G+.0722*c.B;return ImageEffectIo.Rgb(c,l,l,l);}); }
    public sealed class SepiaProcessor : ImageEffectProcessorBase { public override string Name=>"sepia"; protected override bool Enabled(PresetProcessingOptions o)=>o.Sepia; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>ImageEffectIo.Rgb(c,.393*c.R+.769*c.G+.189*c.B,.349*c.R+.686*c.G+.168*c.B,.272*c.R+.534*c.G+.131*c.B)); }
    public sealed class VignetteProcessor : ImageEffectProcessorBase { public override string Name=>"vignette"; protected override bool Enabled(PresetProcessingOptions o)=>o.Vignette>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>{var dx=(x-b.Width/2d)/(b.Width/2d);var dy=(y-b.Height/2d)/(b.Height/2d);var f=Math.Max(0,1-o.Vignette*Math.Pow(Math.Sqrt(dx*dx+dy*dy),2));return ImageEffectIo.Rgb(c,c.R*f,c.G*f,c.B*f);}); }
    public sealed class BlurProcessor : ImageEffectProcessorBase { public override string Name=>"blur"; protected override bool Enabled(PresetProcessingOptions o)=>o.Blur>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o){var result=new Bitmap(b.Width,b.Height);using(var g=Graphics.FromImage(result)){g.InterpolationMode=InterpolationMode.HighQualityBilinear;var scale=Math.Max(.05,1-Math.Min(.9,o.Blur));using(var small=new Bitmap(b,(int)(b.Width*scale),(int)(b.Height*scale)))g.DrawImage(small,new Rectangle(0,0,b.Width,b.Height));}return result;} }
    public sealed class SharpenProcessor : ImageEffectProcessorBase { public override string Name=>"sharpen"; protected override bool Enabled(PresetProcessingOptions o)=>o.Sharpen>.001; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o)=>ImageEffectIo.Pixels(b,(c,x,y)=>{if(x==0||y==0||x==b.Width-1||y==b.Height-1)return c;var k=Math.Min(2,o.Sharpen);var a=b.GetPixel(x-1,y);var d=b.GetPixel(x+1,y);var u=b.GetPixel(x,y-1);var n=b.GetPixel(x,y+1);return ImageEffectIo.Rgb(c,c.R*(1+4*k)-(a.R+d.R+u.R+n.R)*k,c.G*(1+4*k)-(a.G+d.G+u.G+n.G)*k,c.B*(1+4*k)-(a.B+d.B+u.B+n.B)*k);}); }
    public sealed class WatermarkProcessor : ImageEffectProcessorBase { public override string Name=>"watermark"; protected override bool Enabled(PresetProcessingOptions o)=>!string.IsNullOrWhiteSpace(o.WatermarkPath)&&File.Exists(o.WatermarkPath); protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o){var r=new Bitmap(b);using(var g=Graphics.FromImage(r))using(var mark=Image.FromFile(o.WatermarkPath))using(var ia=new ImageAttributes()){var m=new ColorMatrix{Matrix33=Math.Max(0,Math.Min(1,o.WatermarkOpacity))};ia.SetColorMatrix(m);var w=Math.Min(b.Width/3,mark.Width);var h=(int)(mark.Height*(w/(double)mark.Width));g.DrawImage(mark,new Rectangle(b.Width-w-20,b.Height-h-20,w,h),0,0,mark.Width,mark.Height,GraphicsUnit.Pixel,ia);}return r;} }
    public sealed class ResizeProcessor : ImageEffectProcessorBase { public override string Name=>"resize"; protected override bool Enabled(PresetProcessingOptions o)=>o.OutputWidth>0&&o.OutputHeight>0; protected override Bitmap Apply(Bitmap b,PresetProcessingOptions o){var r=new Bitmap(o.OutputWidth,o.OutputHeight);r.SetResolution(o.Dpi,o.Dpi);using(var g=Graphics.FromImage(r)){g.CompositingQuality=CompositingQuality.HighQuality;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(b,0,0,r.Width,r.Height);}return r;} }

    public sealed class PresetProcessor : IPresetProcessor
    {
        readonly IReadOnlyList<IImageEffectProcessor> processors;
        public PresetProcessor(IEnumerable<IImageEffectProcessor> processors) { this.processors = new List<IImageEffectProcessor>(processors); }
        public async Task<string> ProcessAsync(string inputPath, Preset preset, string outputPath, CancellationToken token)
        {
            var options = Deserialize(preset?.SettingsJson);
            var current = inputPath; var temporary = new List<string>();
            try
            {
                foreach (var processor in processors) { var next=await processor.ProcessAsync(current,options,token); if(next!=current){if(current!=inputPath)temporary.Add(current);current=next;} }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                await Task.Run(()=>File.Copy(current,outputPath,true),token);
                return outputPath;
            }
            finally { foreach(var file in temporary)try{File.Delete(file);}catch{} if(current!=inputPath&&current!=outputPath)try{File.Delete(current);}catch{} }
        }
        static PresetProcessingOptions Deserialize(string json)
        {
            if(string.IsNullOrWhiteSpace(json))return new PresetProcessingOptions();
            try{using(var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))return (PresetProcessingOptions)new DataContractJsonSerializer(typeof(PresetProcessingOptions)).ReadObject(stream);}catch{return new PresetProcessingOptions();}
        }
    }
}
