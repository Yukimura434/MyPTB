using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IColorLutService
    {
        Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken token);
        Task<ColorLutData> GetLiveAsync(Guid presetId, CancellationToken token);
        Task ApplyCaptureAsync(Guid presetId, string imagePath, CancellationToken token);
        Task ApplyToFileAsync(Guid presetId, string sourcePath, string destinationPath, float strength, CancellationToken token);
        Task<byte[]> RenderPreviewAsync(Guid assetId, string imagePath, float strength, CancellationToken token);
        Task<ColorLutImportResult> ImportAsync(string sourcePath, string displayName, CancellationToken token);
        Task AttachAsync(Guid presetId, Guid assetId, CancellationToken token);
        Task DetachAsync(Guid presetId, CancellationToken token);
        Task DeleteAsync(Guid assetId, long expectedRowVersion, CancellationToken token);
        Task ReconcileAsync(CancellationToken token);
    }

    public interface IColorLutParser
    {
        ColorLutValidationResult Validate(string filePath, CancellationToken token);
        ColorLutData Parse(string filePath, CancellationToken token);
    }

    public interface IColorLutPathResolver
    {
        string CubeDirectory { get; }
        string StagingDirectory { get; }
        string GetFullPath(string relativePath);
        string CreateRelativeAssetPath(Guid assetId, string sha256);
    }

    public sealed class ColorLutData : IDisposable
    {
        public const float DefaultStrength = 0.5f;
        public ColorLutMetadata Metadata { get; set; }
        public float[] Values { get; set; }
        public float Strength { get; set; } = DefaultStrength;
        public long ManagedBytes => Values == null ? 0 : (long)Values.Length * sizeof(float);
        public void Dispose() { Values = null; }
    }
}
