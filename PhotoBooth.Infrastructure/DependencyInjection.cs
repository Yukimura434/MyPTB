using System;
using System.Threading;
using CameraControl.Devices;
using Microsoft.Extensions.DependencyInjection;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Infrastructure.Cameras;
using PhotoBooth.Business.Services;
using PhotoBooth.Business.Pipelines;
using PhotoBooth.Business.Repositories;
using PhotoBooth.Core.Pipelines;
using PhotoBooth.FrameEngine;
using PhotoBooth.Shared;
using PhotoBooth.Business.Imaging;

namespace PhotoBooth.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddPhotoBoothInfrastructure(this IServiceCollection services, ApplicationOptions options)
        {
            services.AddSingleton(options);
            services.AddSingleton<CameraOperationGate>();
            services.AddSingleton(provider =>
            {
                var gate = provider.GetRequiredService<CameraOperationGate>();
                return gate.RunAsync(() =>
                {
                    // Construct WIA and native camera state on the same STA that owns EDSDK.
                    var cameraManager = new CameraDeviceManager();
                    cameraManager.DetectWebcams = true;
                    cameraManager.StopDiscoveryAfterFirstCamera = true;
                    if (options.UseFakeCamera)
                    {
                        cameraManager.DisableNativeDrivers = true;
                        cameraManager.LoadWiaDevices = false;
                        cameraManager.AddFakeCamera();
                    }
                    return cameraManager;
                }, CancellationToken.None).GetAwaiter().GetResult();
            });
            services.AddSingleton<CameraDeviceResolver>();
            services.AddSingleton<IPhotoBoothCameraAdapter, CanonEdsCameraAdapter>();
            services.AddSingleton<IPhotoBoothCameraAdapter, NikonMtpCameraAdapter>();
            services.AddSingleton<IPhotoBoothCameraAdapter, SonyRemoteCameraAdapter>();
            services.AddSingleton<IPhotoBoothCameraAdapter, GenericCameraAdapter>();
            services.AddSingleton<CameraAdapterRegistry>();
            services.AddSingleton(provider => new SqliteDatabase(options.DatabasePath));
            services.AddSingleton<IPresetRepository, SqlitePresetRepository>();
            services.AddSingleton<IColorLutAssetRepository, SqliteColorLutAssetRepository>();
            services.AddSingleton<IPresetColorRepository, SqlitePresetColorRepository>();
            services.AddSingleton<IColorLutPathResolver, ColorLutPathResolver>();
            services.AddSingleton<IColorLutParser, CubeLutParser>();
            services.AddSingleton<IColorLutService, ColorLutService>();
            services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
            services.AddSingleton<ICaptureRepository, SqliteCaptureRepository>();
            services.AddSingleton<IFrameRepository, SqliteFrameRepository>();
            services.AddSingleton<IFrameEventRepository, SqliteFrameEventRepository>();
            services.AddSingleton<IPrinterProfileRepository, SqlitePrinterProfileRepository>();
            services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();
            services.AddSingleton<IInterfaceAssetRepository, SqliteInterfaceAssetRepository>();
            services.AddSingleton<IPrintJobRepository, SqlitePrintJobRepository>();
            services.AddSingleton<IStatsRepository, SqliteStatsRepository>();
            services.AddSingleton<IFrameAnalyzer, PngFrameAnalyzer>();
            services.AddSingleton<ICameraService, CameraService>();
            services.AddSingleton<ILiveViewService, LiveViewService>();
            services.AddSingleton<IMotionPhotoService, MotionPhotoService>();
            services.AddSingleton<IFrameService, FrameService>();
            services.AddSingleton<IFrameEventService, FrameEventService>();
            services.AddSingleton<IPresetService, PresetService>();
            services.AddSingleton<IPrinterService, PrinterService>();
            services.AddSingleton<ISessionService, SessionService>();
            services.AddSingleton<ICaptureService, CaptureService>();
            services.AddSingleton<ICaptureIntegrityService, CaptureIntegrityService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<IFileStorageService, FileStorageService>();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IInterfaceAssetService, InterfaceAssetService>();
            services.AddSingleton<IImageCompositionService, ImageCompositionService>();
            services.AddSingleton<IImageEffectProcessor, BrightnessProcessor>();
            services.AddSingleton<IImageEffectProcessor, ContrastProcessor>();
            services.AddSingleton<IImageEffectProcessor, SaturationProcessor>();
            services.AddSingleton<IImageEffectProcessor, GammaProcessor>();
            services.AddSingleton<IImageEffectProcessor, ExposureProcessor>();
            services.AddSingleton<IImageEffectProcessor, TemperatureProcessor>();
            services.AddSingleton<IImageEffectProcessor, TintProcessor>();
            services.AddSingleton<IImageEffectProcessor, SharpenProcessor>();
            services.AddSingleton<IImageEffectProcessor, BlurProcessor>();
            services.AddSingleton<IImageEffectProcessor, VignetteProcessor>();
            services.AddSingleton<IImageEffectProcessor, BlackAndWhiteProcessor>();
            services.AddSingleton<IImageEffectProcessor, SepiaProcessor>();
            services.AddSingleton<IImageEffectProcessor, WatermarkProcessor>();
            services.AddSingleton<IImageEffectProcessor, ResizeProcessor>();
            services.AddSingleton<IPresetProcessor, PresetProcessor>();
            services.AddSingleton<IFeatureFlagService, FeatureFlagService>();
            services.AddSingleton<IStorageManager, StorageManager>();
            services.AddSingleton<IUploadService, LocalUploadService>();
            services.AddSingleton<IQrCodeService, QrCodeService>();
            services.AddSingleton<IGifAnimationService, GifAnimationService>();
            services.AddSingleton<ILocalShareService, LocalShareService>();
            services.AddSingleton<IPrintQueueService, PrintQueueService>();
            services.AddSingleton<IRecoveryService, RecoveryService>();
            services.AddSingleton<IHealthStatusService, HealthStatusService>();
            services.AddSingleton<IBackupService, BackupService>();
            services.AddSingleton<IPasswordService, PasswordService>();
            services.AddSingleton<ILocalizationService, LocalizationService>();
            services.AddSingleton<ISettingsTransferService, SettingsTransferService>();
            services.AddSingleton<ICapturePipeline, CapturePipeline>();
            services.AddSingleton<IPrintPipeline, PrintPipeline>();
            return services;
        }

        public static void InitializePhotoBooth(this IServiceProvider provider)
        {
            provider.GetRequiredService<SqliteDatabase>().Initialize();
            var settingsService=provider.GetRequiredService<ISettingsService>();
            var settings=settingsService.GetAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            if(string.IsNullOrWhiteSpace(settings.CaptureDirectory))settings.CaptureDirectory=System.IO.Path.Combine(provider.GetRequiredService<ApplicationOptions>().DataDirectory,"Captures");
            if(string.IsNullOrWhiteSpace(settings.AdminPasswordHash)){settings.AdminPasswordHash=provider.GetRequiredService<IPasswordService>().Hash("1~6");settingsService.SaveAsync(settings,System.Threading.CancellationToken.None).GetAwaiter().GetResult();}
            else settingsService.SaveAsync(settings,System.Threading.CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
