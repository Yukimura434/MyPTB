using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IDeliverableRepository
    {
        Task<Deliverable> GetAsync(string deliverableId, CancellationToken token);
        Task<Deliverable> GetAsync(Guid boothSessionId, string deliverableId, CancellationToken token);
        Task<IReadOnlyList<Deliverable>> GetByBoothSessionAsync(Guid boothSessionId, CancellationToken token);
        Task SaveAsync(Deliverable deliverable, CancellationToken token);
    }
}
