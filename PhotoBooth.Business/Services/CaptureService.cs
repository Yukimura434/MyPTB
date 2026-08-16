using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Services
{
    public sealed class CaptureService : ICaptureService
    {
        private readonly ICaptureRepository captures;
        private readonly ISessionRepository sessions;

        public CaptureService(ICaptureRepository captures, ISessionRepository sessions) { this.captures = captures; this.sessions = sessions; }

        public async Task<PhotoCapture> CreateAsync(Guid sessionId, Guid? frameId, string compositeImageId, string compositePath, IReadOnlyList<string> originalPaths, DateTime? expiresAtUtc, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(compositePath)) throw new ArgumentException("Composite path is required.", nameof(compositePath));
            var session = await sessions.GetAsync(sessionId, token);
            if (session == null) throw new InvalidOperationException("Session not found.");
            var paths = (originalPaths ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var sessionFiles = (session.CapturedFiles ?? new string[0]).ToList();
            var sessionImageIds = (session.CapturedImageIds ?? new string[0]).ToList();
            var captureId = Guid.NewGuid().ToString("N");
            var photos = new List<CapturePhoto>();
            for (var i = 0; i < paths.Count; i++)
            {
                var sessionIndex = sessionFiles.FindIndex(x => string.Equals(x, paths[i], StringComparison.OrdinalIgnoreCase));
                photos.Add(new CapturePhoto { Id=Guid.NewGuid().ToString("N"), CaptureId=captureId, CapturedImageId=sessionIndex>=0&&sessionIndex<sessionImageIds.Count?sessionImageIds[sessionIndex]:null, LocalPath=paths[i], PhotoType="Original", Position=i+1, IsUploaded=false });
            }
            photos.Add(new CapturePhoto { Id=Guid.NewGuid().ToString("N"), CaptureId=captureId, LocalPath=compositePath, PhotoType="Composite", Position=1, IsUploaded=false });
            var capture = new PhotoCapture { Id=captureId, SessionId=sessionId, FrameId=frameId, CompositeImageId=compositeImageId, CompositePath=compositePath, SharePath="/s/"+sessionId.ToString("N")+"/c/"+captureId+"/", Status="Pending", CreatedAtUtc=DateTime.UtcNow, ExpiresAtUtc=expiresAtUtc, Photos=photos };
            await captures.SaveAsync(capture, token);
            return capture;
        }

        public Task<PhotoCapture> GetAsync(string captureId, CancellationToken token) => captures.GetAsync(captureId, token);
        public Task<PhotoCapture> GetAsync(Guid sessionId, string captureId, CancellationToken token) => captures.GetAsync(sessionId, captureId, token);
        public Task<IReadOnlyList<PhotoCapture>> GetBySessionAsync(Guid sessionId, CancellationToken token) => captures.GetBySessionAsync(sessionId, token);
        public async Task UpdateSharePathAsync(string captureId,string sharePath,CancellationToken token){var capture=await captures.GetAsync(captureId,token);if(capture==null)throw new InvalidOperationException("Capture not found.");capture.SharePath=sharePath;await captures.SaveAsync(capture,token);}
        public async Task AddFileAsync(string captureId,string localPath,string photoType,CancellationToken token)
        {
            var capture=await captures.GetAsync(captureId,token);
            if(capture==null)throw new InvalidOperationException("Capture not found.");
            var photos=(capture.Photos??new CapturePhoto[0]).ToList();
            if(!photos.Any(x=>string.Equals(x.LocalPath,localPath,StringComparison.OrdinalIgnoreCase)))
            {
                photos.Add(new CapturePhoto{Id=Guid.NewGuid().ToString("N"),CaptureId=captureId,LocalPath=localPath,PhotoType=photoType,Position=1,IsUploaded=false});
                capture.Photos=photos;
                await captures.SaveAsync(capture,token);
            }
        }
    }
}
