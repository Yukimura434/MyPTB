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

        public async Task<PhotoCapture> CreateAsync(Guid sessionId, Guid? frameId, string compositeImageId, string compositePath, IReadOnlyList<CapturedShot> shots, DateTime? expiresAtUtc, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(compositePath)) throw new ArgumentException("Composite path is required.", nameof(compositePath));
            var session = await sessions.GetAsync(sessionId, token);
            if (session == null) throw new InvalidOperationException("Session not found.");
            var selected = (shots ?? new CapturedShot[0]).Where(x => x != null && !string.IsNullOrWhiteSpace(x.PicturePath)).ToList();
            var captureId = Guid.NewGuid().ToString("N");
            var photos = new List<CapturePhoto>();
            for (var i = 0; i < selected.Count; i++) photos.Add(CreateAsset(captureId, selected[i].Id, selected[i].PicturePath, CaptureAssetTypes.Picture, i+1, null));
            var motionAssetIds=photos.Select(x=>x.Id).ToList();
            photos.Add(CreateAsset(captureId, null, compositePath, CaptureAssetTypes.Composite, 1, motionAssetIds));
            var capture = new PhotoCapture { Id=captureId, SessionId=sessionId, FrameId=frameId, CompositeImageId=compositeImageId, CompositePath=compositePath, MediaMode=CaptureMediaModes.PictureOnly, SharePath="/s/"+sessionId.ToString("N")+"/c/"+captureId+"/", Status="Pending", CreatedAtUtc=DateTime.UtcNow, ExpiresAtUtc=expiresAtUtc, Photos=photos };
            await captures.SaveAsync(capture, token);
            return capture;
        }

        public async Task<PhotoCapture> CreateWithMotionCompositeAsync(Guid sessionId, Guid? frameId, string compositeImageId, string compositePath, IReadOnlyList<CapturedShot> shots, string motionCompositePath, IReadOnlyList<string> motionSourcePaths, DateTime? expiresAtUtc, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(compositePath)) throw new ArgumentException("Composite path is required.", nameof(compositePath));
            if (string.IsNullOrWhiteSpace(motionCompositePath)) throw new ArgumentException("Motion composite path is required.", nameof(motionCompositePath));
            var session = await sessions.GetAsync(sessionId, token);
            if (session == null) throw new InvalidOperationException("Session not found.");
            var selected = (shots ?? new CapturedShot[0]).Where(x => x != null).ToList();
            if (selected.Count == 0 || selected.Any(x=>string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.PicturePath)||!x.HasMotionPhoto)) throw new InvalidDataException("Every captured shot must contain its Picture and Motion Photo pair.");
            var captureId = Guid.NewGuid().ToString("N"); var photos = new List<CapturePhoto>();
            var pictureAssets = new List<CapturePhoto>(); var motionAssets = new List<CapturePhoto>();
            for (var i = 0; i < selected.Count; i++) { pictureAssets.Add(CreateAsset(captureId, selected[i].Id, selected[i].PicturePath, CaptureAssetTypes.Picture, i + 1, null)); motionAssets.Add(CreateAsset(captureId, selected[i].Id, selected[i].MotionPhotoPath, CaptureAssetTypes.MotionPhoto, i + 1, null)); }
            photos.AddRange(pictureAssets); photos.AddRange(motionAssets);
            photos.Add(CreateAsset(captureId, null, compositePath, CaptureAssetTypes.Composite, 1, pictureAssets.Select(x => x.Id).ToList()));
            var selectedIds = motionAssets.Where(asset => (motionSourcePaths ?? new string[0]).Contains(asset.LocalPath, StringComparer.OrdinalIgnoreCase)).Select(x => x.Id).ToList();
            if(selectedIds.Count==0)throw new InvalidDataException("Motion Photo composite has no linked source assets.");
            photos.Add(CreateAsset(captureId, null, motionCompositePath, CaptureAssetTypes.MotionPhotoComposite, 1, selectedIds));
            var capture = new PhotoCapture { Id = captureId, SessionId = sessionId, FrameId = frameId, CompositeImageId = compositeImageId, CompositePath = compositePath, MediaMode = CaptureMediaModes.PictureAndMotion, SharePath = "/s/" + sessionId.ToString("N") + "/c/" + captureId + "/", Status = "Pending", CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = expiresAtUtc, Photos = photos };
            await captures.SaveAsync(capture, token); return capture;
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
        static string Mime(string type,string path){if(string.Equals(type,CaptureAssetTypes.Picture,StringComparison.Ordinal)||string.Equals(type,CaptureAssetTypes.MotionPhoto,StringComparison.Ordinal)||string.Equals(type,CaptureAssetTypes.MotionPhotoComposite,StringComparison.Ordinal))return "image/jpeg";if(string.Equals(type,CaptureAssetTypes.Gif,StringComparison.Ordinal))return "image/gif";if(string.Equals(type,CaptureAssetTypes.ShareArchive,StringComparison.Ordinal))return "application/zip";var extension=Path.GetExtension(path);return string.Equals(extension,".png",StringComparison.OrdinalIgnoreCase)?"image/png":"application/octet-stream";}
        static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();}
    }
}
