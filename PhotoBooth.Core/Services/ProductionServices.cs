using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IImageEffectProcessor { string Name { get; } Task<string> ProcessAsync(string inputPath, PresetProcessingOptions options, CancellationToken token); }
    public interface IPresetProcessor { Task<string> ProcessAsync(string inputPath, Preset preset, string outputPath, CancellationToken token); }
    public interface ICompositionProcessor { int Order { get; } Task<string> ProcessAsync(string inputPath, CancellationToken token); }
    public interface IUploadService { string ProviderName { get; } Task<UploadResult> UploadAsync(string filePath, CancellationToken token); }
    public interface IQrCodeService { Task<byte[]> GeneratePngAsync(Uri content, int pixels, CancellationToken token); }
    public interface IGifAnimationService { Task<string> CreateAsync(IReadOnlyList<string> imagePaths, string outputPath, int frameDurationMilliseconds, CancellationToken token); }
    public interface ILocalShareService
    {
        Task StartAsync(CancellationToken token);
        Task<LocalShareTicket> CreateAsync(Guid sessionId, string captureId, IReadOnlyList<string> files, CancellationToken token);
        bool IsRunning { get; }
        string BaseUrl { get; }
    }
    public interface IPrintQueueService
    {
        event EventHandler<PrintQueueItem> JobChanged;
        Task<Guid> EnqueueAsync(PrintJob job, CancellationToken token);
        Task CancelAsync(Guid jobId, CancellationToken token);
        Task RetryAsync(Guid jobId, CancellationToken token);
        IReadOnlyList<PrintQueueItem> Snapshot();
    }
    public interface IStorageManager { string GetPath(string area); Task CleanupAsync(CancellationToken token); }
    public interface IBackupService { Task<string> ExportAsync(string destinationZip, CancellationToken token); Task ImportAsync(string backupZip, CancellationToken token); }
    public interface IHealthStatusService { Task<HealthSnapshot> GetSnapshotAsync(CancellationToken token); }
    public interface IRecoveryService { Task StartAsync(CancellationToken token); Task StopAsync(CancellationToken token); }
    public interface ISettingsTransferService { Task ExportAsync(string path, CancellationToken token); Task ImportAsync(string path, CancellationToken token); }
    public interface IUpdateService { Task<bool> IsUpdateAvailableAsync(CancellationToken token); Task<string> GetAvailableVersionAsync(CancellationToken token); }
    public interface IFeatureFlagService { Task<bool> IsEnabledAsync(string feature, CancellationToken token); }
    public interface IPasswordService { string Hash(string password); bool Verify(string password, string encodedHash); }
    public interface ILocalizationService { string Culture { get; } string Get(string key); void SetCulture(string culture); }
}
