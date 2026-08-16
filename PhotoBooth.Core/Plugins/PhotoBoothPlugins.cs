using System;using System.Collections.Generic;using PhotoBooth.Core.Services;
namespace PhotoBooth.Core.Plugins
{
 public interface IPhotoBoothPlugin{string Id{get;}string Name{get;}Version Version{get;}void Initialize(IPluginHost context);}
 public interface IPluginHost{IServiceProvider Services{get;}void RegisterCapability(string capability,object implementation);}
 public interface IImageProcessorPlugin:IPhotoBoothPlugin{IEnumerable<IImageEffectProcessor> CreateProcessors();}
 public interface IPrinterPlugin:IPhotoBoothPlugin{IPrinterService CreatePrinterService();}
 public interface IUploadPlugin:IPhotoBoothPlugin{IUploadService CreateUploadService();}
 public interface IQrPlugin:IPhotoBoothPlugin{IQrCodeService CreateQrService();}
 public interface ICameraPlugin:IPhotoBoothPlugin{ICameraService CreateCameraService();}
}
