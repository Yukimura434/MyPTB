// Adapted from Vortice.Wpf, Copyright (c) Amer Koleci and Contributors.
// Licensed under the MIT License. Local changes add idempotent net48 lifecycle guards.
using System;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using D3D9Format = Vortice.Direct3D9.Format;
using DxgiFormat = Vortice.DXGI.Format;
using static Vortice.Direct3D9.D3D9;

namespace PhotoBooth.Color.D3D11.Interop
{
    internal static class D3D9DeviceService
    {
        static int clients; static IDirect3D9Ex d3d; static IDirect3DDevice9Ex device;
        internal static IDirect3DDevice9Ex Device => device ?? throw new InvalidOperationException("D3D9Ex device is not initialized.");
        internal static void Start(Window window)
        {
            if (clients++ > 0) return;
            try
            {
                d3d=Direct3DCreate9Ex();
                var p=new Vortice.Direct3D9.PresentParameters{Windowed=true,SwapEffect=Vortice.Direct3D9.SwapEffect.Discard,DeviceWindowHandle=new WindowInteropHelper(window).Handle,PresentationInterval=PresentInterval.Default};
                device=d3d.CreateDeviceEx(0,DeviceType.Hardware,IntPtr.Zero,CreateFlags.HardwareVertexProcessing|CreateFlags.Multithreaded|CreateFlags.FpuPreserve,p);
            }
            catch { clients=0; Dispose(); throw; }
        }
        internal static void End(){if(clients==0||--clients!=0)return;Dispose();}
        static void Dispose(){device?.Dispose();device=null;d3d?.Dispose();d3d=null;}
    }

    internal static class D3D9Extensions
    {
        internal static bool IsShareable(this ID3D11Texture2D value)=>(value.Description.MiscFlags&ResourceOptionFlags.Shared)!=0;
        internal static D3D9Format ToD3D9Format(this ID3D11Texture2D value)
        {
            switch(value.Description.Format){case DxgiFormat.R10G10B10A2_UNorm:return D3D9Format.A2B10G10R10;case DxgiFormat.R16G16B16A16_Float:return D3D9Format.A16B16G16R16F;case DxgiFormat.B8G8R8A8_UNorm:return D3D9Format.A8R8G8B8;default:return D3D9Format.Unknown;}
        }
        internal static IntPtr SharedHandle(this ID3D11Texture2D value){using(var resource=value.QueryInterface<IDXGIResource>())return resource.SharedHandle;}
    }

    internal sealed class D3D11ImageSource : D3DImage, IDisposable
    {
        IDirect3DTexture9 target; bool disposed;
        internal D3D11ImageSource(Window window){D3D9DeviceService.Start(window);}
        public void Dispose(){if(disposed)return;SetRenderTarget(null);disposed=true;D3D9DeviceService.End();}
        internal void InvalidateImage(){if(disposed||target==null||!IsFrontBufferAvailable)return;Lock();try{AddDirtyRect(new Int32Rect(0,0,PixelWidth,PixelHeight));}finally{Unlock();}}
        internal void SetRenderTarget(ID3D11Texture2D value)
        {
            if(disposed&&value!=null)throw new ObjectDisposedException(GetType().Name);
            if(target!=null){Lock();try{SetBackBuffer(D3DResourceType.IDirect3DSurface9,IntPtr.Zero);}finally{Unlock();}target.Dispose();target=null;}
            if(value==null)return;if(!value.IsShareable())throw new ArgumentException("D3D11 texture must be shared.");
            var format=value.ToD3D9Format();if(format==D3D9Format.Unknown)throw new ArgumentException("Unsupported shared format.");var handle=value.SharedHandle();if(handle==IntPtr.Zero)throw new InvalidOperationException("Shared handle is unavailable.");
            target=D3D9DeviceService.Device.CreateTexture(value.Description.Width,value.Description.Height,1,Vortice.Direct3D9.Usage.RenderTarget,format,Pool.Default,ref handle);
            using(var surface=target.GetSurfaceLevel(0)){Lock();try{SetBackBuffer(D3DResourceType.IDirect3DSurface9,surface.NativePointer);}finally{Unlock();}}
        }
    }
}
