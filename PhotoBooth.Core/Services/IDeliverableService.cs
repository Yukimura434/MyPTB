using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IDeliverableService
    {
        Task<Deliverable> CreateAsync(Guid boothSessionId, Guid? frameId, string finalCompositeAssetId, string finalCompositePath, IReadOnlyList<CapturedShot> shots, DateTime? expiresAtUtc, CancellationToken token);
        Task<Deliverable> CreateWithCompositeVideoAsync(Guid boothSessionId, Guid? frameId, string finalCompositeAssetId, string finalCompositePath, IReadOnlyList<CapturedShot> shots, string compositeVideoPath, IReadOnlyList<string> videoSourcePaths, DateTime? expiresAtUtc, CancellationToken token);
        Task<Deliverable> GetAsync(string deliverableId, CancellationToken token);
        Task<Deliverable> GetAsync(Guid boothSessionId, string deliverableId, CancellationToken token);
        Task<IReadOnlyList<Deliverable>> GetByBoothSessionAsync(Guid boothSessionId, CancellationToken token);
        Task UpdateSharePathAsync(string deliverableId, string sharePath, CancellationToken token);
        Task<DeliverableAsset> AddAssetAsync(string deliverableId, string localPath, string role, IReadOnlyList<string> sourceAssetIds, CancellationToken token);
    }
}
