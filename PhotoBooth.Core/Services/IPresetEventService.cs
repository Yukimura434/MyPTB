using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IPresetEventService
    {
        Task<IReadOnlyList<PresetEvent>> GetAllAsync(CancellationToken token);
        Task<PresetEvent> CreateAsync(string name, CancellationToken token);
        Task RenameAsync(Guid id, string name, CancellationToken token);
        Task DeleteAsync(Guid id, CancellationToken token);
        Task AssignPresetAsync(Guid presetId, Guid? eventId, CancellationToken token);
    }
}
