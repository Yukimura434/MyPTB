using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IFrameEventRepository
    {
        Task<IReadOnlyList<FrameEvent>> GetAllAsync(CancellationToken token);
        Task SaveAsync(FrameEvent value, CancellationToken token);
        Task DeleteAsync(Guid id, CancellationToken token);
    }
}
