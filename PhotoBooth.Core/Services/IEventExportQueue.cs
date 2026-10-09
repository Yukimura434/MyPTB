using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IEventExportQueue
    {
        Task EnqueueAsync(PhotoEvent photoEvent, BoothSession boothSession, Deliverable deliverable, CancellationToken token);
        Task StartAsync(CancellationToken token);
        Task StopAsync(CancellationToken token);
    }
}
