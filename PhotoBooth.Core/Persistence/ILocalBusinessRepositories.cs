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

    public interface IMediaAssetReplicaRepository
    {
        Task UpsertAsync(MediaAssetReplicaRecord replica, CancellationToken token);
        Task<IReadOnlyList<MediaAssetReplicaRecord>> GetByAssetAsync(string assetId, CancellationToken token);
        Task SetStateAsync(string assetId, string provider, string state, string remoteReference, DateTime? nextAttemptAtUtc, string errorCategory, string error, CancellationToken token);
        Task ReplaceOnlineProviderAsync(string assetId, string provider, string state, string remoteReference, string contentHashSha256, long fileLength, DateTime? verifiedAtUtc, CancellationToken token);
    }

    public interface IDurableOutputJobRepository
    {
        Task<DurableOutputJobRecord> CreateIntentAsync(DurableOutputJobRecord job, CancellationToken token);
        Task<DurableOutputJobRecord> LeaseNextDueAsync(string leaseId, DateTime leaseExpiresAtUtc, CancellationToken token, string authorizationScope = null);
        Task CompleteAsync(string jobId, string externalReference, CancellationToken token);
        Task ScheduleRetryAsync(string jobId, DateTime nextRetryAtUtc, string error, CancellationToken token);
        Task FailAsync(string jobId, string error, CancellationToken token);
        Task FailPendingDependentsAsync(string deliverableId, string error, CancellationToken token);
        Task SetStateAsync(string jobId, string state, string error, CancellationToken token);
        Task ReconcileInterruptedAsync(CancellationToken token);
        Task<int> RetryPendingAsync(string deliverableId, CancellationToken token);
    }

    public interface IEventExportJobRepository
    {
        Task<EventExportJobRecord> CreateIntentAsync(EventExportJobRecord job, CancellationToken token);
        Task<EventExportJobRecord> LeaseNextDueAsync(string leaseId, DateTime leaseExpiresAtUtc, CancellationToken token);
        Task CompleteAsync(string jobId, string exportedPath, CancellationToken token);
        Task ScheduleRetryAsync(string jobId, DateTime nextRetryAtUtc, string error, CancellationToken token);
        Task FailAsync(string jobId, string error, CancellationToken token);
        Task ReconcileInterruptedAsync(CancellationToken token);
    }

    public interface IGalleryLinkCacheRepository
    {
        Task SaveAsync(GalleryLinkCacheRecord link, CancellationToken token);
        Task<GalleryLinkCacheRecord> GetAsync(string deliverableId, CancellationToken token);
    }

    public interface ILocalSessionRecoveryRepository
    {
        Task<IReadOnlyList<Session>> GetActiveBoothSessionsAsync(CancellationToken token);
        Task MarkRecoveredFailedAsync(Guid sessionId, string reason, CancellationToken token);
    }
}
