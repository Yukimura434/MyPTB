using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IEventService
    {
        Task<PhotoEvent> CreateDraftAsync(Guid? presetId, CancellationToken token);
        Task<PhotoEvent> CreateAsync(PhotoEvent draft, CancellationToken token);
        Task<IReadOnlyList<PhotoEvent>> GetAllAsync(CancellationToken token);
        Task<PhotoEvent> GetDefaultAsync(CancellationToken token);
        Task SetDefaultAsync(Guid eventId, CancellationToken token);
    }

    public interface IBoothSessionService
    {
        Task<BoothSession> StartAsync(Guid eventId, Guid? presetId, CancellationToken token);
        Task<BoothSession> GetAsync(Guid boothSessionId, CancellationToken token);
        Task UpdateAsync(BoothSession boothSession, CancellationToken token);
        Task CompleteAsync(BoothSession boothSession, CancellationToken token);
        Task AbandonAsync(BoothSession boothSession, string reason, CancellationToken token);
        Task ReplaceCapturedShotAsync(Guid boothSessionId, string previousShotId, CapturedShot replacement, CancellationToken token);
        Task ReplaceCapturedShotsAsync(Guid boothSessionId, IReadOnlyDictionary<string, CapturedShot> replacements, CancellationToken token);
    }
}
