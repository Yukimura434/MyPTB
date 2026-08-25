using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IStatsRepository
    {
        Task<long> CountSessionsAsync(CancellationToken token);
        Task<long> CountCapturedImagesAsync(CancellationToken token);
        Task<long> CountSuccessfulPrintsAsync(CancellationToken token);
        Task<DataStatisticsSnapshot> GetDataStatisticsAsync(CancellationToken token);
    }
}
