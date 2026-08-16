using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface ICaptureRepository
    {
        Task<PhotoCapture> GetAsync(string captureId, CancellationToken token);
        Task<PhotoCapture> GetAsync(Guid sessionId, string captureId, CancellationToken token);
        Task<IReadOnlyList<PhotoCapture>> GetBySessionAsync(Guid sessionId, CancellationToken token);
        Task SaveAsync(PhotoCapture capture, CancellationToken token);
    }
}
