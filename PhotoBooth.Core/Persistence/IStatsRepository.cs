using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IStatsRepository
    {
        Task<long> CountSessionsAsync(CancellationToken token);
        Task<long> CountCapturedImagesAsync(CancellationToken token);
        Task<long> CountSuccessfulPrintsAsync(CancellationToken token);
        Task<DataStatisticsSnapshot> GetDataStatisticsAsync(CancellationToken token);
        Task<CaptureLibrarySnapshot> SearchCaptureLibraryAsync(CaptureLibraryFilter filter, CancellationToken token);
        Task<IReadOnlyList<string>> GetEventSuggestionsAsync(CancellationToken token);
        Task<IReadOnlyList<CaptureLibraryMedia>> GetCaptureMediaAsync(string captureId, CancellationToken token);
    }
}
