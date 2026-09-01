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
    public class BusinessWorkflowService : ISessionService, IEventService, IBoothSessionService
    {
        readonly ISessionRepository repository;
        readonly ApplicationOptions options;
        readonly IStorageManager storage;
        readonly IMediaAssetRepository mediaAssets;
        static readonly SemaphoreSlim SessionLock = new SemaphoreSlim(1, 1);
        public BusinessWorkflowService(ISessionRepository repository, ApplicationOptions options) : this(repository, options, null, null) { }
        public BusinessWorkflowService(ISessionRepository repository, ApplicationOptions options, IStorageManager storageManager,IMediaAssetRepository mediaAssetRepository) { this.repository = repository; this.options = options; storage = storageManager;mediaAssets=mediaAssetRepository; }

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
                if (number == 0) throw new InvalidOperationException("The daily event limit (99) has been reached.");
                var name = day + "_" + number; var folder = System.IO.Path.Combine(root, name);
                return new Session { PresetId = presetId, StartedAtUtc = DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow, OutputDirectory = folder, SessionName = name, SessionNumber = number, Kind=SessionKinds.Event, Status=BoothSessionStates.Active, CapturedShots = new List<CapturedShot>(), CapturedFiles = new List<string>(), CapturedImageIds = new List<string>() };
            }
            finally { SessionLock.Release(); }
        }

        public async Task<Session> CreateAsync(Session draft, CancellationToken token)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (string.IsNullOrWhiteSpace(draft.SessionName)) throw new InvalidOperationException("Event name is required.");
            if (string.IsNullOrWhiteSpace(draft.OutputDirectory)) throw new InvalidOperationException("Event storage location is unavailable.");
            await SessionLock.WaitAsync(token);
            try
            {
                var folder = System.IO.Path.GetFullPath(draft.OutputDirectory);
                System.IO.Directory.CreateDirectory(folder);
                var value = new Session { Id = Guid.NewGuid(), PresetId = draft.PresetId, StartedAtUtc = DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow, OutputDirectory = folder, SessionName = draft.SessionName.Trim(), SessionNumber = draft.SessionNumber, Kind=SessionKinds.Event, Status=BoothSessionStates.Active, CapturedShots = new List<CapturedShot>(), CapturedFiles = new List<string>(), CapturedImageIds = new List<string>() };
                await repository.SaveAsync(value, token); return value;
            }
            finally { SessionLock.Release(); }
        }

        public Task<Session> GetAsync(Guid id, CancellationToken token) => repository.GetAsync(id, token);
        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken token) => repository.GetAllAsync(token);
        public async Task<Session> GetBaseAsync(CancellationToken token)
        {
            var all = await repository.GetAllAsync(token);
            var existing = all.FirstOrDefault(x => x.SessionNumber == 0 && (string.Equals(x.SessionName, "Sự kiện mặc định", StringComparison.OrdinalIgnoreCase)||string.Equals(x.SessionName, "Base_session", StringComparison.OrdinalIgnoreCase)));
            if (existing != null) { System.IO.Directory.CreateDirectory(existing.OutputDirectory); return existing; }
            var folder = System.IO.Path.Combine(options.DataDirectory, "Captures", "Base_session"); System.IO.Directory.CreateDirectory(folder);
            var value = new Session { Id = Guid.NewGuid(), StartedAtUtc = DateTime.UtcNow, UpdatedAtUtc=DateTime.UtcNow, OutputDirectory = folder, SessionName = "Sự kiện mặc định", SessionNumber = 0, IsDefault = !all.Any(x => x.IsDefault), Kind=SessionKinds.Event, Status=BoothSessionStates.Active, CapturedShots = new List<CapturedShot>(), CapturedFiles = new List<string>(), CapturedImageIds = new List<string>() };
            await repository.SaveAsync(value, token); return value;
        }
        public async Task<Session> GetDefaultAsync(CancellationToken token)
        {
            var all = await repository.GetAllAsync(token); var selected = all.FirstOrDefault(x => x.IsDefault);
            if (selected != null) return selected;
            var baseSession = await GetBaseAsync(token); await repository.SetDefaultAsync(baseSession.Id, token); baseSession.IsDefault = true; return baseSession;
        }
        public Task SetDefaultAsync(Guid id, CancellationToken token) => repository.SetDefaultAsync(id, token);
        async Task<PhotoEvent> IEventService.CreateDraftAsync(Guid? presetId,CancellationToken token)
        {
            var legacy=await CreateDraftAsync(presetId,token).ConfigureAwait(false);var value=ToEvent(legacy);value.Name="Sự kiện "+DateTime.Now.ToString("yyyyMMdd")+" #"+legacy.SessionNumber;return value;
        }
        async Task<PhotoEvent> IEventService.CreateAsync(PhotoEvent draft,CancellationToken token)
        {
            if(draft==null)throw new ArgumentNullException(nameof(draft));
            var legacy=await CreateDraftAsync(draft.PresetId,token).ConfigureAwait(false);
            legacy.SessionName=draft.Name;
            if(!string.IsNullOrWhiteSpace(draft.OutputDirectory))legacy.OutputDirectory=draft.OutputDirectory;
            var created=await CreateAsync(legacy,token).ConfigureAwait(false);
            return ToEvent(created);
        }
        async Task<IReadOnlyList<PhotoEvent>> IEventService.GetAllAsync(CancellationToken token)=>(await GetAllAsync(token).ConfigureAwait(false)).Select(ToEvent).ToList();
        async Task<PhotoEvent> IEventService.GetDefaultAsync(CancellationToken token)=>ToEvent(await GetDefaultAsync(token).ConfigureAwait(false));
        Task IEventService.SetDefaultAsync(Guid eventId,CancellationToken token)=>SetDefaultAsync(eventId,token);
        async Task<BoothSession> IBoothSessionService.StartAsync(Guid eventId,Guid? presetId,CancellationToken token)=>BoothSession.From(await StartBoothSessionAsync(eventId,presetId,token).ConfigureAwait(false));
        async Task<BoothSession> IBoothSessionService.GetAsync(Guid boothSessionId,CancellationToken token)=>BoothSession.From(await GetAsync(boothSessionId,token).ConfigureAwait(false));
        Task IBoothSessionService.UpdateAsync(BoothSession boothSession,CancellationToken token)=>UpdateAsync(boothSession,token);
        Task IBoothSessionService.CompleteAsync(BoothSession boothSession,CancellationToken token)=>CompleteAsync(boothSession,token);
        Task IBoothSessionService.AbandonAsync(BoothSession boothSession,string reason,CancellationToken token)=>AbandonAsync(boothSession,reason,token);
        public async Task<Session> StartBoothSessionAsync(Guid eventId,Guid? presetId,CancellationToken token)
        {
            var eventSession=await repository.GetAsync(eventId,token).ConfigureAwait(false);
            if(eventSession==null||eventSession.IsBoothSession)throw new InvalidOperationException("The selected event is unavailable.");
            var id=Guid.NewGuid();var now=DateTime.UtcNow;
            SessionStoragePaths paths;
            if(storage!=null)paths=storage.CreateSessionStorage(id,now);
            else
            {
                var root=System.IO.Path.Combine(options.DataDirectory,"Captures",now.ToString("yyyy"),now.ToString("MM"),now.ToString("dd"),id.ToString("N"));
                paths=new SessionStoragePaths{Root=root,Work=System.IO.Path.Combine(root,"Work"),Originals=System.IO.Path.Combine(root,"Originals"),Final=System.IO.Path.Combine(root,"Final")};
                System.IO.Directory.CreateDirectory(paths.Work);System.IO.Directory.CreateDirectory(paths.Originals);System.IO.Directory.CreateDirectory(paths.Final);
            }
            var session=new BoothSession{Id=id,EventId=eventId,Kind=SessionKinds.Booth,Status=BoothSessionStates.Active,StateVersion=1,PresetId=presetId??eventSession.PresetId,StartedAtUtc=now,UpdatedAtUtc=now,OutputDirectory=paths.Root,DisplayCode="PB-"+now.ToString("yyyyMMdd-HHmmss")+"-"+id.ToString("N").Substring(0,6).ToUpperInvariant(),CapturedShots=new List<CapturedShot>(),CapturedFiles=new List<string>(),CapturedVideoFiles=new List<string>(),CapturedImageIds=new List<string>()};
            await repository.SaveAsync(session,token).ConfigureAwait(false);return session;
        }
        public Task UpdateAsync(Session session, CancellationToken token) { session.UpdatedAtUtc=DateTime.UtcNow;session.StateVersion++;return repository.SaveAsync(session, token); }
        public Task ReplaceCapturedShotAsync(Guid sessionId,string previousShotId,CapturedShot replacement,CancellationToken token)=>ReplaceCapturedShotsAsync(sessionId,new Dictionary<string,CapturedShot>{{previousShotId,replacement}},token);
        public async Task ReplaceCapturedShotsAsync(Guid sessionId,IReadOnlyDictionary<string,CapturedShot> replacements,CancellationToken token){if(replacements==null)throw new ArgumentNullException(nameof(replacements));var session=await repository.GetAsync(sessionId,token).ConfigureAwait(false);var previous=(session?.CapturedShots??new CapturedShot[0]).Where(x=>replacements.ContainsKey(x.Id)).ToList();if(previous.Count!=replacements.Count)throw new InvalidOperationException("One or more captured shots were not found.");await repository.ReplaceCapturedShotsAsync(sessionId,replacements,token).ConfigureAwait(false);if(mediaAssets!=null)foreach(var shot in previous){await mediaAssets.MarkDeletedAsync(shot.PictureAssetId,token).ConfigureAwait(false);await mediaAssets.MarkDeletedAsync(shot.VideoAssetId,token).ConfigureAwait(false);}}
        public Task CompleteAsync(Session session, CancellationToken token) { session.CompletedAtUtc = DateTime.UtcNow;session.UpdatedAtUtc=session.CompletedAtUtc.Value;session.Status=BoothSessionStates.Completed;session.StateVersion++;return repository.SaveAsync(session, token); }
        public async Task AbandonAsync(Session session,string reason,CancellationToken token){if(session==null)return;session.CompletedAtUtc=DateTime.UtcNow;session.UpdatedAtUtc=session.CompletedAtUtc.Value;session.Status=BoothSessionStates.Abandoned;session.TerminalReason=string.IsNullOrWhiteSpace(reason)?"Cancelled":reason;session.StateVersion++;await repository.SaveAsync(session,token).ConfigureAwait(false);if(mediaAssets!=null)await mediaAssets.MarkDeletedBySessionAsync(session.Id,token).ConfigureAwait(false);}
        static PhotoEvent ToEvent(Session value)=>value==null?null:new PhotoEvent{Id=value.Id,PresetId=value.PresetId,Name=string.Equals(value.SessionName,"Base_session",StringComparison.OrdinalIgnoreCase)?"Sự kiện mặc định":value.SessionName,OutputDirectory=value.OutputDirectory,IsDefault=value.IsDefault,CreatedAtUtc=value.StartedAtUtc,UpdatedAtUtc=value.UpdatedAtUtc==DateTime.MinValue?value.StartedAtUtc:value.UpdatedAtUtc};
    }

    public sealed class SessionService : BusinessWorkflowService
    {
        public SessionService(ISessionRepository repository,ApplicationOptions options):base(repository,options){}
        public SessionService(ISessionRepository repository,ApplicationOptions options,IStorageManager storageManager,IMediaAssetRepository mediaAssetRepository):base(repository,options,storageManager,mediaAssetRepository){}
    }
}
