using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface ICaptureAttemptRepository
    {
        Task BeginAsync(CaptureAttemptRecord attempt, CancellationToken token);
        Task MarkAcceptedAsync(string attemptId, CancellationToken token);
        Task MarkFailedAsync(string attemptId, string error, bool outcomeUnknown, CancellationToken token);
        Task<IReadOnlyList<CaptureAttemptRecord>> GetIncompleteAsync(CancellationToken token);
    }

    public interface IMediaAssetRepository
    {
        Task SaveAsync(MediaAssetRecord asset, CancellationToken token);
        Task<MediaAssetRecord> GetAsync(string assetId, CancellationToken token);
        Task<IReadOnlyList<MediaAssetRecord>> GetBySessionAsync(Guid sessionId, CancellationToken token);
        Task<bool> HasPendingOutputAsync(Guid sessionId, CancellationToken token);
        Task MarkDeletedAsync(string assetId, CancellationToken token);
        Task MarkDeletedBySessionAsync(Guid sessionId, CancellationToken token);
    }

    public interface IDurableOutputJobRepository
    {
        Task<DurableOutputJobRecord> CreateIntentAsync(DurableOutputJobRecord job, CancellationToken token);
        Task SetStateAsync(string jobId, string state, string error, CancellationToken token);
        Task ReconcileInterruptedAsync(CancellationToken token);
    }

    public interface ILocalSessionRecoveryRepository
    {
        Task<IReadOnlyList<Session>> GetActiveBoothSessionsAsync(CancellationToken token);
        Task MarkRecoveredFailedAsync(Guid sessionId, string reason, CancellationToken token);
    }
}
