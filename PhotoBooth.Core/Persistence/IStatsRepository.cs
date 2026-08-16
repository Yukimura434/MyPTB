using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Persistence
{
    public interface IStatsRepository
    {
        Task<long> CountSessionsAsync(CancellationToken token);
        Task<long> CountCapturedImagesAsync(CancellationToken token);
        Task<long> CountSuccessfulPrintsAsync(CancellationToken token);
    }
}
