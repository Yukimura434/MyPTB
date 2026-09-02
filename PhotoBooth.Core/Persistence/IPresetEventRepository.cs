using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IPresetEventRepository
    {
        Task<IReadOnlyList<PresetEvent>> GetAllAsync(CancellationToken token);
        Task SaveAsync(PresetEvent value, CancellationToken token);
        Task DeleteAsync(Guid id, CancellationToken token);
    }
}
