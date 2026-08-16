using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Business.Services
{
    public sealed class SessionService : ISessionService
    {
        readonly ISessionRepository repository;
        readonly ApplicationOptions options;
        static readonly SemaphoreSlim SessionLock = new SemaphoreSlim(1, 1);
        public SessionService(ISessionRepository repository, ApplicationOptions options) { this.repository = repository; this.options = options; }

        public async Task<Session> StartAsync(Guid? presetId, CancellationToken token)
        {
            var draft = await CreateDraftAsync(presetId, token);
            return await CreateAsync(draft, token);
        }

        public async Task<Session> CreateDraftAsync(Guid? presetId, CancellationToken token)
        {
            await SessionLock.WaitAsync(token);
            try
            {
                var now = DateTime.Now; var day = now.ToString("yyyyMMdd");
                var root = System.IO.Path.Combine(options.DataDirectory, "Captures");
                var all = await repository.GetAllAsync(token);
                var used = new HashSet<int>(all.Where(x => x.SessionNumber > 0 && x.SessionName != null && x.SessionName.StartsWith(day + "_", StringComparison.Ordinal)).Select(x => x.SessionNumber));
                if (System.IO.Directory.Exists(root)) foreach (var path in System.IO.Directory.EnumerateDirectories(root, day + "_*"))
                { var suffix = System.IO.Path.GetFileName(path).Substring(day.Length + 1); if (int.TryParse(suffix, out var n) && n > 0 && n <= 99) used.Add(n); }
                var number = Enumerable.Range(1, 99).FirstOrDefault(x => !used.Contains(x));
                if (number == 0) throw new InvalidOperationException("The daily session limit (99) has been reached.");
                var name = day + "_" + number; var folder = System.IO.Path.Combine(root, name);
                return new Session { PresetId = presetId, StartedAtUtc = DateTime.UtcNow, OutputDirectory = folder, SessionName = name, SessionNumber = number, CapturedFiles = new List<string>(), CapturedImageIds = new List<string>() };
            }
            finally { SessionLock.Release(); }
        }

        public async Task<Session> CreateAsync(Session draft, CancellationToken token)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (string.IsNullOrWhiteSpace(draft.SessionName)) throw new InvalidOperationException("Session name is required.");
            if (string.IsNullOrWhiteSpace(draft.OutputDirectory)) throw new InvalidOperationException("Session location is required.");
            await SessionLock.WaitAsync(token);
            try
            {
                var all = await repository.GetAllAsync(token);
                if (all.Any(x => string.Equals(x.SessionName, draft.SessionName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("A session with this name already exists.");
                var folder = System.IO.Path.GetFullPath(draft.OutputDirectory);
                System.IO.Directory.CreateDirectory(folder);
                var value = new Session { Id = Guid.NewGuid(), PresetId = draft.PresetId, StartedAtUtc = DateTime.UtcNow, OutputDirectory = folder, SessionName = draft.SessionName.Trim(), SessionNumber = draft.SessionNumber, CapturedFiles = new List<string>(), CapturedImageIds = new List<string>() };
                await repository.SaveAsync(value, token); return value;
            }
            finally { SessionLock.Release(); }
        }

        public Task<Session> GetAsync(Guid id, CancellationToken token) => repository.GetAsync(id, token);
        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken token) => repository.GetAllAsync(token);
        public async Task<Session> GetBaseAsync(CancellationToken token)
        {
            var all = await repository.GetAllAsync(token);
            var existing = all.FirstOrDefault(x => string.Equals(x.SessionName, "Base_session", StringComparison.OrdinalIgnoreCase));
            if (existing != null) { System.IO.Directory.CreateDirectory(existing.OutputDirectory); return existing; }
            var folder = System.IO.Path.Combine(options.DataDirectory, "Captures", "Base_session"); System.IO.Directory.CreateDirectory(folder);
            var value = new Session { Id = Guid.NewGuid(), StartedAtUtc = DateTime.UtcNow, OutputDirectory = folder, SessionName = "Base_session", SessionNumber = 0, IsDefault = !all.Any(x => x.IsDefault), CapturedFiles = new List<string>(), CapturedImageIds = new List<string>() };
            await repository.SaveAsync(value, token); return value;
        }
        public async Task<Session> GetDefaultAsync(CancellationToken token)
        {
            var all = await repository.GetAllAsync(token); var selected = all.FirstOrDefault(x => x.IsDefault);
            if (selected != null) return selected;
            var baseSession = await GetBaseAsync(token); await repository.SetDefaultAsync(baseSession.Id, token); baseSession.IsDefault = true; return baseSession;
        }
        public Task SetDefaultAsync(Guid id, CancellationToken token) => repository.SetDefaultAsync(id, token);
        public Task UpdateAsync(Session session, CancellationToken token) => repository.SaveAsync(session, token);
        public Task CompleteAsync(Session session, CancellationToken token) { session.CompletedAtUtc = DateTime.UtcNow; return repository.SaveAsync(session, token); }
    }
}
