using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface ICaptureService
    {
        Task<PhotoCapture> CreateAsync(Guid sessionId, Guid? frameId, string compositeImageId, string compositePath, IReadOnlyList<CapturedShot> shots, DateTime? expiresAtUtc, CancellationToken token);
        Task<PhotoCapture> CreateWithMotionCompositeAsync(Guid sessionId, Guid? frameId, string compositeImageId, string compositePath, IReadOnlyList<CapturedShot> shots, string motionCompositePath, IReadOnlyList<string> motionSourcePaths, DateTime? expiresAtUtc, CancellationToken token);
        Task<PhotoCapture> GetAsync(string captureId, CancellationToken token);
        Task<PhotoCapture> GetAsync(Guid sessionId, string captureId, CancellationToken token);
        Task<IReadOnlyList<PhotoCapture>> GetBySessionAsync(Guid sessionId, CancellationToken token);
        Task UpdateSharePathAsync(string captureId, string sharePath, CancellationToken token);
        Task<CapturePhoto> AddFileAsync(string captureId, string localPath, string photoType, IReadOnlyList<string> sourceAssetIds, CancellationToken token);
    }
}
