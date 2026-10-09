using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IRevenueReportRepository
    {
        Task<IReadOnlyList<RevenueReport>> GetPendingAsync(int limit, CancellationToken token);
        Task MarkSyncedAsync(System.Guid boothSessionId, CancellationToken token);
        Task MarkRetryAsync(System.Guid boothSessionId, string error, CancellationToken token);
    }
}
