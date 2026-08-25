using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoBooth.Color.D3D11.Interop;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MediaColor = System.Windows.Media.Color;

namespace PhotoBooth.Color.D3D11
{
    public sealed class LiveColorSurface : Border
    {
        public static readonly DependencyProperty FrameDataProperty=DependencyProperty.Register(nameof(FrameData),typeof(object),typeof(LiveColorSurface),new PropertyMetadata(null,OnFrame));
        public static readonly DependencyProperty LutValuesProperty=DependencyProperty.Register(nameof(LutValues),typeof(object),typeof(LiveColorSurface),new PropertyMetadata(null,OnLut));
        public static readonly DependencyProperty LutSizeProperty=DependencyProperty.Register(nameof(LutSize),typeof(int),typeof(LiveColorSurface),new PropertyMetadata(0,OnLut));
        public static readonly DependencyProperty DomainMinProperty=DependencyProperty.Register(nameof(DomainMin),typeof(MediaColor),typeof(LiveColorSurface),new PropertyMetadata(MediaColor.FromScRgb(1,0,0,0),OnLut));
        public static readonly DependencyProperty DomainMaxProperty=DependencyProperty.Register(nameof(DomainMax),typeof(MediaColor),typeof(LiveColorSurface),new PropertyMetadata(MediaColor.FromScRgb(1,1,1,1),OnLut));
        public static readonly DependencyProperty StrengthProperty=DependencyProperty.Register(nameof(Strength),typeof(double),typeof(LiveColorSurface),new PropertyMetadata(1d,OnLut));
        public static readonly DependencyProperty FrameWidthProperty=DependencyProperty.Register(nameof(FrameWidth),typeof(int),typeof(LiveColorSurface),new PropertyMetadata(0,OnReportedSize));
        public static readonly DependencyProperty FrameHeightProperty=DependencyProperty.Register(nameof(FrameHeight),typeof(int),typeof(LiveColorSurface),new PropertyMetadata(0,OnReportedSize));

        readonly DrawingSurface surface;readonly Renderer renderer;int frameWidth,frameHeight;
        public LiveColorSurface(){Background=Brushes.Black;ClipToBounds=true;surface=new DrawingSurface();renderer=new Renderer(this);surface.LoadContent+=renderer.Load;surface.Draw+=renderer.Draw;surface.UnloadContent+=renderer.Unload;surface.Failed+=(s,e)=>Disable(e.Error);Child=surface;}
        public object FrameData{get=>GetValue(FrameDataProperty);set=>SetValue(FrameDataProperty,value);}
        public object LutValues{get=>GetValue(LutValuesProperty);set=>SetValue(LutValuesProperty,value);}
        public int LutSize{get=>(int)GetValue(LutSizeProperty);set=>SetValue(LutSizeProperty,value);}
        public MediaColor DomainMin{get=>(MediaColor)GetValue(DomainMinProperty);set=>SetValue(DomainMinProperty,value);}
        public MediaColor DomainMax{get=>(MediaColor)GetValue(DomainMaxProperty);set=>SetValue(DomainMaxProperty,value);}
        public double Strength{get=>(double)GetValue(StrengthProperty);set=>SetValue(StrengthProperty,value);}
        public int FrameWidth{get=>(int)GetValue(FrameWidthProperty);set=>SetValue(FrameWidthProperty,value);}
        public int FrameHeight{get=>(int)GetValue(FrameHeightProperty);set=>SetValue(FrameHeightProperty,value);}
        public event EventHandler<LiveColorFailedEventArgs> Failed;
        static void OnFrame(DependencyObject value,DependencyPropertyChangedEventArgs args){((LiveColorSurface)value).renderer.Publish((byte[])args.NewValue);}
        static void OnLut(DependencyObject value,DependencyPropertyChangedEventArgs args){((LiveColorSurface)value).renderer.InvalidateLut();}
        static void OnReportedSize(DependencyObject value,DependencyPropertyChangedEventArgs args){var owner=(LiveColorSurface)value;if(owner.FrameWidth>0&&owner.FrameHeight>0)owner.SetFrameAspect(owner.FrameWidth,owner.FrameHeight);}
        void Disable(Exception error){Visibility=Visibility.Collapsed;Failed?.Invoke(this,new LiveColorFailedEventArgs(error));}
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo){base.OnRenderSizeChanged(sizeInfo);UpdateAspect();}
        void SetFrameAspect(int width,int height){if(FrameWidth>0&&FrameHeight>0){width=FrameWidth;height=FrameHeight;}if(width<=0||height<=0||(frameWidth==width&&frameHeight==height))return;frameWidth=width;frameHeight=height;UpdateAspect();}
        void UpdateAspect()
        {
            // Keep one stable interop surface. Letterboxing is performed by the D3D
            // viewport; resizing the child Image through WPF can be overridden by
            // template/layout measurement and produced a square render target.
            surface.HorizontalAlignment=HorizontalAlignment.Stretch;surface.VerticalAlignment=VerticalAlignment.Stretch;
            surface.ClearValue(WidthProperty);surface.ClearValue(HeightProperty);
        }
        void SetFrameViewport(ID3D11DeviceContext context,int targetWidth,int targetHeight)
        {
            if(frameWidth<=0||frameHeight<=0){context.RSSetViewport(0,0,targetWidth,targetHeight);return;}
            var source=frameWidth/(double)frameHeight;var available=targetWidth/(double)targetHeight;float width,height,x,y;
            if(available>source){height=targetHeight;width=(float)(targetHeight*source);x=(targetWidth-width)/2f;y=0;}
            else{width=targetWidth;height=(float)(targetWidth/source);x=0;y=(targetHeight-height)/2f;}
            context.RSSetViewport(x,y,width,height);
        }

        sealed class Renderer
        {
            readonly LiveColorSurface owner;byte[] pending;int lutDirty=1;ID3D11VertexShader vs;ID3D11PixelShader ps;ID3D11Texture2D input;ID3D11ShaderResourceView inputView;ID3D11Texture3D lut;ID3D11ShaderResourceView lutView;ID3D11SamplerState sampler;ID3D11RasterizerState raster;ID3D11DepthStencilState depth;byte[] pixels;int width,height;
            internal Renderer(LiveColorSurface owner){this.owner=owner;}
            internal void Publish(byte[] value){if(value!=null)Interlocked.Exchange(ref pending,value);}
            internal void InvalidateLut(){Interlocked.Exchange(ref lutDirty,1);}
            internal void Load(object sender,SurfaceEventArgs e){var path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","LiveColor.hlsl");var vertex=Compiler.CompileFromFile(path,"VSMain","vs_4_0");vs=e.Device.CreateVertexShader(vertex.Span);var pixel=Compiler.CompileFromFile(path,"PSMain","ps_4_0");ps=e.Device.CreatePixelShader(pixel.Span);sampler=e.Device.CreateSamplerState(SamplerDescription.LinearClamp);raster=e.Device.CreateRasterizerState(new RasterizerDescription(CullMode.None,FillMode.Solid));depth=e.Device.CreateDepthStencilState(DepthStencilDescription.None);CreateLut(e.Device);}
            internal void Draw(object sender,DrawEventArgs e)
            {
                if(Interlocked.Exchange(ref lutDirty,0)!=0)CreateLut(e.Device);var frame=Interlocked.Exchange(ref pending,null);if(frame!=null)Upload(e,frame);e.Context.ClearRenderTargetView(e.Surface.ColorTextureView,new Color4(0,0,0,1));if(inputView==null||lutView==null||ps==null)return;owner.SetFrameViewport(e.Context,e.Surface.TextureWidth,e.Surface.TextureHeight);e.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);e.Context.RSSetState(raster);e.Context.OMSetDepthStencilState(depth);e.Context.VSSetShader(vs);e.Context.PSSetShader(ps);e.Context.PSSetShaderResource(0,inputView);e.Context.PSSetShaderResource(1,lutView);e.Context.PSSetSampler(0,sampler);e.Context.PSSetSampler(1,sampler);e.Context.Draw(3,0);
            }
            unsafe void CreateLut(ID3D11Device device)
            {
                var values=owner.LutValues as float[];var size=owner.LutSize;if(values==null||size==0)return;if(size<2||size>65||values.Length!=size*size*size*3)throw new InvalidDataException("Live LUT must contain a complete 3D cube between 2³ and 65³.");
                lutView?.Dispose();lutView=null;lut?.Dispose();lut=null;var strength=(float)Math.Max(0,Math.Min(1,owner.Strength));var rgba=new float[size*size*size*4];for(int z=0,s=0,d=0;z<size;z++)for(int y=0;y<size;y++)for(int x=0;x<size;x++,s+=3){rgba[d++]=x/(float)(size-1)+(values[s]-x/(float)(size-1))*strength;rgba[d++]=y/(float)(size-1)+(values[s+1]-y/(float)(size-1))*strength;rgba[d++]=z/(float)(size-1)+(values[s+2]-z/(float)(size-1))*strength;rgba[d++]=1;}
                fixed(float* ptr=rgba){var description=new Texture3DDescription{Width=size,Height=size,Depth=size,MipLevels=1,Format=Format.R32G32B32A32_Float,Usage=ResourceUsage.Immutable,BindFlags=BindFlags.ShaderResource};lut=device.CreateTexture3D(description,new[]{new SubresourceData((IntPtr)ptr,size*16,size*size*16)});}lutView=device.CreateShaderResourceView(lut);
            }
            void Upload(DrawEventArgs e,byte[] frame)
            {
                BitmapSource source;using(var stream=new MemoryStream(frame,false)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);source=decoder.Frames[0];}if(source.Format!=PixelFormats.Bgra32)source=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);EnsureInput(e.Device,source.PixelWidth,source.PixelHeight);source.CopyPixels(pixels,width*4,0);var mapped=e.Context.Map(input,0,MapMode.WriteDiscard);try{for(var y=0;y<height;y++)Marshal.Copy(pixels,y*width*4,mapped.DataPointer+y*(int)mapped.RowPitch,width*4);}finally{e.Context.Unmap(input,0);}
            }
            void EnsureInput(ID3D11Device device,int w,int h){if(input!=null&&w==width&&h==height)return;inputView?.Dispose();inputView=null;input?.Dispose();input=null;width=w;height=h;owner.SetFrameAspect(w,h);pixels=new byte[w*h*4];input=device.CreateTexture2D(new Texture2DDescription{Width=w,Height=h,ArraySize=1,MipLevels=1,Format=Format.B8G8R8A8_UNorm,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Dynamic,BindFlags=BindFlags.ShaderResource,CPUAccessFlags=CpuAccessFlags.Write});inputView=device.CreateShaderResourceView(input);}
            internal void Unload(object sender,SurfaceEventArgs e){e.Context.PSSetShaderResource(0,null);e.Context.PSSetShaderResource(1,null);inputView?.Dispose();inputView=null;input?.Dispose();input=null;lutView?.Dispose();lutView=null;lut?.Dispose();lut=null;sampler?.Dispose();sampler=null;raster?.Dispose();raster=null;depth?.Dispose();depth=null;ps?.Dispose();ps=null;vs?.Dispose();vs=null;pixels=null;pending=null;}
        }
    }
    public sealed class LiveColorFailedEventArgs:EventArgs{internal LiveColorFailedEventArgs(Exception error){Error=error;}public Exception Error{get;}}
}
