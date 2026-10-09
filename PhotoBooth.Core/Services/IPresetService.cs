using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IPresetService
    {
        // Replace the local used set atomically; collection membership is not changed.
        Task ReplacePinnedAsync(System.Collections.Generic.IReadOnlyCollection<System.Guid> ids, CancellationToken token);

        Task<IReadOnlyList<Preset>> GetAllAsync(CancellationToken cancellationToken);
        Task<Preset> GetAsync(System.Guid id, CancellationToken cancellationToken);
        Task SaveAsync(Preset preset, CancellationToken cancellationToken);
        Task SetPinnedAsync(System.Guid id, bool isPinned, CancellationToken cancellationToken);
        Task DeleteAsync(System.Guid id, CancellationToken cancellationToken);
    }
}
