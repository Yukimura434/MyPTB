using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface ISettingsService
    {
        Task<Settings> GetAsync(CancellationToken cancellationToken);
        Task SaveAsync(Settings settings, CancellationToken cancellationToken);
    }
}
