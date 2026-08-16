using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IPresetRepository
    {
        Task<IReadOnlyList<Preset>> GetAllAsync(CancellationToken cancellationToken);
        Task<Preset> GetAsync(System.Guid id, CancellationToken cancellationToken);
        Task SaveAsync(Preset preset, CancellationToken cancellationToken);
        Task DeleteAsync(System.Guid id, CancellationToken cancellationToken);
    }
}
