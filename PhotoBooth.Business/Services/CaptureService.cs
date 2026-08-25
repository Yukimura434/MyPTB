using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
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
                photos.Add(CreateAsset(captureId, sessionIndex>=0&&sessionIndex<sessionImageIds.Count?sessionImageIds[sessionIndex]:null, paths[i], CaptureAssetTypes.MotionPhoto, i+1, null));
            }
            var motionAssetIds=photos.Select(x=>x.Id).ToList();
            photos.Add(CreateAsset(captureId, null, compositePath, CaptureAssetTypes.Composite, 1, motionAssetIds));
            var capture = new PhotoCapture { Id=captureId, SessionId=sessionId, FrameId=frameId, CompositeImageId=compositeImageId, CompositePath=compositePath, SharePath="/s/"+sessionId.ToString("N")+"/c/"+captureId+"/", Status="Pending", CreatedAtUtc=DateTime.UtcNow, ExpiresAtUtc=expiresAtUtc, Photos=photos };
            await captures.SaveAsync(capture, token);
            return capture;
        }

        public Task<PhotoCapture> GetAsync(string captureId, CancellationToken token) => captures.GetAsync(captureId, token);
        public Task<PhotoCapture> GetAsync(Guid sessionId, string captureId, CancellationToken token) => captures.GetAsync(sessionId, captureId, token);
        public Task<IReadOnlyList<PhotoCapture>> GetBySessionAsync(Guid sessionId, CancellationToken token) => captures.GetBySessionAsync(sessionId, token);
        public async Task UpdateSharePathAsync(string captureId,string sharePath,CancellationToken token){var capture=await captures.GetAsync(captureId,token);if(capture==null)throw new InvalidOperationException("Capture not found.");capture.SharePath=sharePath;await captures.SaveAsync(capture,token);}
        public async Task<CapturePhoto> AddFileAsync(string captureId,string localPath,string photoType,IReadOnlyList<string> sourceAssetIds,CancellationToken token)
        {
            var capture=await captures.GetAsync(captureId,token);
            if(capture==null)throw new InvalidOperationException("Capture not found.");
            var photos=(capture.Photos??new CapturePhoto[0]).ToList();
            var existing=photos.FirstOrDefault(x=>string.Equals(x.LocalPath,localPath,StringComparison.OrdinalIgnoreCase));
            if(existing!=null)return existing;
            var asset=CreateAsset(captureId,null,localPath,photoType,1,sourceAssetIds);
            photos.Add(asset);capture.Photos=photos;await captures.SaveAsync(capture,token);return asset;
        }

        static CapturePhoto CreateAsset(string captureId,string capturedImageId,string path,string type,int position,IReadOnlyList<string> sourceIds)
        {
            if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new FileNotFoundException("Capture asset is unavailable.",path);
            return new CapturePhoto{Id=Guid.NewGuid().ToString("N"),CaptureId=captureId,CapturedImageId=capturedImageId,LocalPath=path,PhotoType=type,Position=position,MimeType=Mime(type,path),FileLength=new FileInfo(path).Length,ContentHashSha256=Hash(path),CreatedAtUtc=DateTime.UtcNow,AssetStatus="Ready",SourceAssetIds=(sourceIds??new string[0]).Distinct(StringComparer.Ordinal).ToList(),IsUploaded=false};
        }
        static string Mime(string type,string path){if(string.Equals(type,CaptureAssetTypes.MotionPhoto,StringComparison.Ordinal))return "image/jpeg";if(string.Equals(type,CaptureAssetTypes.Gif,StringComparison.Ordinal))return "image/gif";if(string.Equals(type,CaptureAssetTypes.ShareArchive,StringComparison.Ordinal))return "application/zip";var extension=Path.GetExtension(path);return string.Equals(extension,".png",StringComparison.OrdinalIgnoreCase)?"image/png":"application/octet-stream";}
        static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();}
    }
}
