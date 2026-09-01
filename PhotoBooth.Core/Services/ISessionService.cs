using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface ISessionService
    {
        Task<Session> StartAsync(Guid? presetId, CancellationToken cancellationToken);
        Task<Session> CreateDraftAsync(Guid? presetId, CancellationToken cancellationToken);
        Task<Session> CreateAsync(Session draft, CancellationToken cancellationToken);
        Task<Session> GetAsync(Guid id, CancellationToken cancellationToken);
        Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken);
        Task<Session> GetBaseAsync(CancellationToken cancellationToken);
        Task<Session> GetDefaultAsync(CancellationToken cancellationToken);
        Task<Session> StartBoothSessionAsync(Guid eventId, Guid? presetId, CancellationToken cancellationToken);
        Task SetDefaultAsync(Guid id, CancellationToken cancellationToken);
        Task CompleteAsync(Session session, CancellationToken cancellationToken);
        Task AbandonAsync(Session session, string reason, CancellationToken cancellationToken);
        Task UpdateAsync(Session session, CancellationToken cancellationToken);
        Task ReplaceCapturedShotAsync(Guid sessionId, string previousShotId, CapturedShot replacement, CancellationToken cancellationToken);
        Task ReplaceCapturedShotsAsync(Guid sessionId, IReadOnlyDictionary<string, CapturedShot> replacements, CancellationToken cancellationToken);
    }
}
