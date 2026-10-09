using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IFrameService
    {
        // Replace the local used set atomically; collection membership is not changed.
        Task ReplacePinnedAsync(System.Collections.Generic.IReadOnlyCollection<System.Guid> ids, CancellationToken token);

        Task<Frame> ImportAsync(string pngPath, FrameAnalysisOptions options, CancellationToken cancellationToken);
        Task<Frame> GetAsync(Guid id, CancellationToken cancellationToken);
        Task<IReadOnlyList<Frame>> GetAllAsync(CancellationToken cancellationToken);
        Task SetSlotOrderAsync(Guid id, IReadOnlyList<Guid> orderedSlotIds, CancellationToken cancellationToken);
        Task DeleteAsync(Guid id, CancellationToken cancellationToken);
        Task SetPinnedAsync(Guid id, bool pinned, CancellationToken cancellationToken);
    }
}
