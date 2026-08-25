using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IFrameEventService
    {
        Task<IReadOnlyList<FrameEvent>> GetAllAsync(CancellationToken token);
        Task<FrameEvent> CreateAsync(string name, CancellationToken token);
        Task RenameAsync(Guid id, string name, CancellationToken token);
        Task DeleteAsync(Guid id, CancellationToken token);
        Task AssignFrameAsync(Guid frameId, Guid? eventId, CancellationToken token);
    }
}
