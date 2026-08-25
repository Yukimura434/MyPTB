// Adapted from Vortice.Wpf, Copyright (c) Amer Koleci and Contributors.
// Licensed under the MIT License. Render/resize/dispose are serialized for net48.
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace PhotoBooth.Color.D3D11.Interop
{
    public class SurfaceEventArgs:EventArgs{internal SurfaceEventArgs(ID3D11Device d,ID3D11DeviceContext c){Device=d;Context=c;}public ID3D11Device Device{get;}public ID3D11DeviceContext Context{get;}}
    public sealed class DrawEventArgs:SurfaceEventArgs{internal DrawEventArgs(DrawingSurface s,ID3D11Device d,ID3D11DeviceContext c):base(d,c){Surface=s;}public DrawingSurface Surface{get;}}
    public sealed class SurfaceFailedEventArgs:EventArgs{internal SurfaceFailedEventArgs(Exception error){Error=error;}public Exception Error{get;}}

    public sealed class DrawingSurface:Image
    {
        public event EventHandler<SurfaceEventArgs> LoadContent;public event EventHandler<DrawEventArgs> Draw;public event EventHandler<SurfaceEventArgs> UnloadContent;public event EventHandler<SurfaceFailedEventArgs> Failed;
        ID3D11Device device;ID3D11DeviceContext context;D3D11ImageSource source;bool running,changing,disposed,failed;
        public ID3D11Texture2D ColorTexture{get;private set;}public ID3D11RenderTargetView ColorTextureView{get;private set;}public int TextureWidth{get;private set;}public int TextureHeight{get;private set;}
        public DrawingSurface(){Loaded+=OnLoaded;Unloaded+=OnUnloaded;IsVisibleChanged+=OnVisibleChanged;Stretch=Stretch.Fill;}
        void OnLoaded(object sender,RoutedEventArgs args)
        {
            TryStart();
        }
        void OnVisibleChanged(object sender,DependencyPropertyChangedEventArgs args){if(IsLoaded&&IsVisible)TryStart();}
        void TryStart()
        {
            if(DesignerProperties.GetIsInDesignMode(this)||!IsVisible||running||failed||device!=null)return;disposed=false;
            try{D3D11CreateDevice(IntPtr.Zero,DriverType.Hardware,DeviceCreationFlags.BgraSupport,new[]{FeatureLevel.Level_11_0},out device,out context).CheckError();source=new D3D11ImageSource(Window.GetWindow(this));source.IsFrontBufferAvailableChanged+=OnFrontBufferChanged;Source=source;CreateTargets();LoadContent?.Invoke(this,new SurfaceEventArgs(device,context));CompositionTarget.Rendering+=OnRendering;running=true;}
            catch(Exception error){Fail(error);}
        }
        void OnUnloaded(object sender,RoutedEventArgs args)=>DisposeSurface();
        void OnRendering(object sender,EventArgs args)
        {
            if(!running||changing||disposed||ColorTexture==null||ColorTextureView==null)return;
            try{context.OMSetRenderTargets(ColorTextureView);context.RSSetViewport(0,0,TextureWidth,TextureHeight);Draw?.Invoke(this,new DrawEventArgs(this,device,context));context.Flush();source.InvalidateImage();}
            catch(Exception error){Fail(error);}
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo info){base.OnRenderSizeChanged(info);if(device!=null&&!disposed&&!failed)try{CreateTargets();}catch(Exception error){Fail(error);}}
        void CreateTargets()
        {
            if(changing||disposed)return;changing=true;try{source?.SetRenderTarget(null);DisposeTargets();TextureWidth=Math.Max((int)ActualWidth,100);TextureHeight=Math.Max((int)ActualHeight,100);ColorTexture=device.CreateTexture2D(new Texture2DDescription{Width=TextureWidth,Height=TextureHeight,ArraySize=1,MipLevels=1,Format=Format.B8G8R8A8_UNorm,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource,CPUAccessFlags=CpuAccessFlags.None,MiscFlags=ResourceOptionFlags.Shared});ColorTextureView=device.CreateRenderTargetView(ColorTexture);source.SetRenderTarget(ColorTexture);}finally{changing=false;}
        }
        void OnFrontBufferChanged(object sender,DependencyPropertyChangedEventArgs args){if(disposed||failed)return;if(source.IsFrontBufferAvailable){try{CreateTargets();running=true;}catch(Exception error){Fail(error);}}else running=false;}
        void Fail(Exception error){if(failed)return;failed=true;DisposeSurface();Failed?.Invoke(this,new SurfaceFailedEventArgs(error));}
        void DisposeTargets(){ColorTextureView?.Dispose();ColorTextureView=null;ColorTexture?.Dispose();ColorTexture=null;}
        void DisposeSurface(){if(disposed)return;disposed=true;running=false;CompositionTarget.Rendering-=OnRendering;if(device!=null&&context!=null)try{UnloadContent?.Invoke(this,new SurfaceEventArgs(device,context));}catch{}if(source!=null){source.IsFrontBufferAvailableChanged-=OnFrontBufferChanged;source.Dispose();source=null;}Source=null;DisposeTargets();if(context!=null){context.ClearState();context.Flush();context.Dispose();context=null;}device?.Dispose();device=null;}
    }
}
