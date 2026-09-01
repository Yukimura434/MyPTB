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
    public class DeliverableService : ICaptureService, IDeliverableService
    {
        private readonly ICaptureRepository captures;
        private readonly ISessionRepository sessions;
        private readonly IMediaAssetRepository mediaAssets;
        private readonly IStorageManager storage;

        public DeliverableService(ICaptureRepository captures, ISessionRepository sessions) : this(captures,sessions,null,null) { }
        public DeliverableService(ICaptureRepository captures, ISessionRepository sessions,IMediaAssetRepository mediaAssetRepository,IStorageManager storageManager) { this.captures = captures; this.sessions = sessions;mediaAssets=mediaAssetRepository;storage=storageManager; }

        public async Task<PhotoCapture> CreateAsync(Guid sessionId, Guid? frameId, string compositeImageId, string compositePath, IReadOnlyList<CapturedShot> shots, DateTime? expiresAtUtc, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(compositePath)) throw new ArgumentException("Composite path is required.", nameof(compositePath));
            var session = await sessions.GetAsync(sessionId, token);
            if (session == null) throw new InvalidOperationException("Session not found.");
            var selected = (shots ?? new CapturedShot[0]).Where(x => x != null && !string.IsNullOrWhiteSpace(x.PicturePath)).ToList();
            var captureId = Guid.NewGuid().ToString("N");
            var photos = new List<CapturePhoto>();
            for (var i = 0; i < selected.Count; i++) photos.Add(CreateAsset(captureId, selected[i].Id, selected[i].PicturePath, CaptureAssetTypes.Picture, i+1, null,selected[i].PictureAssetId));
            var videoAssetIds=photos.Select(x=>x.Id).ToList();
            photos.Add(CreateAsset(captureId, null, compositePath, CaptureAssetTypes.Composite, 1, videoAssetIds,compositeImageId));
            var capture = new PhotoCapture { Id=captureId, SessionId=sessionId, FrameId=frameId, CompositeImageId=compositeImageId, CompositePath=compositePath, MediaMode=CaptureMediaModes.PictureOnly, SharePath="/s/"+sessionId.ToString("N")+"/c/"+captureId+"/", Status="Pending", CreatedAtUtc=DateTime.UtcNow, ExpiresAtUtc=expiresAtUtc, Photos=photos };
            await captures.SaveAsync(capture, token);
            await SaveBusinessAssets(session,capture,token).ConfigureAwait(false);
            return capture;
        }

        public async Task<PhotoCapture> CreateWithCompositeVideoAsync(Guid sessionId, Guid? frameId, string compositeImageId, string compositePath, IReadOnlyList<CapturedShot> shots, string compositeVideoPath, IReadOnlyList<string> videoSourcePaths, DateTime? expiresAtUtc, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(compositePath)) throw new ArgumentException("Composite path is required.", nameof(compositePath));
            if (string.IsNullOrWhiteSpace(compositeVideoPath)) throw new ArgumentException("Composite video path is required.", nameof(compositeVideoPath));
            var session = await sessions.GetAsync(sessionId, token);
            if (session == null) throw new InvalidOperationException("Session not found.");
            var selected = (shots ?? new CapturedShot[0]).Where(x => x != null).ToList();
            if (selected.Count == 0 || selected.Any(x=>string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.PicturePath)||!x.HasVideo)) throw new InvalidDataException("Every captured shot must contain its Picture and MP4 video pair.");
            var captureId = Guid.NewGuid().ToString("N"); var photos = new List<CapturePhoto>();
            var pictureAssets = new List<CapturePhoto>(); var videoAssets = new List<CapturePhoto>();
            for (var i = 0; i < selected.Count; i++) { pictureAssets.Add(CreateAsset(captureId, selected[i].Id, selected[i].PicturePath, CaptureAssetTypes.Picture, i + 1, null,selected[i].PictureAssetId)); videoAssets.Add(CreateAsset(captureId, selected[i].Id, selected[i].VideoPath, CaptureAssetTypes.Video, i + 1, null,selected[i].VideoAssetId)); }
            photos.AddRange(pictureAssets); photos.AddRange(videoAssets);
            photos.Add(CreateAsset(captureId, null, compositePath, CaptureAssetTypes.Composite, 1, pictureAssets.Select(x => x.Id).ToList(),compositeImageId));
            var selectedIds = videoAssets.Where(asset => (videoSourcePaths ?? new string[0]).Contains(asset.LocalPath, StringComparer.OrdinalIgnoreCase)).Select(x => x.Id).ToList();
            if(selectedIds.Count==0)throw new InvalidDataException("Composite MP4 has no linked source videos.");
            photos.Add(CreateAsset(captureId, null, compositeVideoPath, CaptureAssetTypes.CompositeVideo, 1, selectedIds,FileIdentity(compositeVideoPath)));
            var capture = new PhotoCapture { Id = captureId, SessionId = sessionId, FrameId = frameId, CompositeImageId = compositeImageId, CompositePath = compositePath, MediaMode = CaptureMediaModes.PictureAndVideo, SharePath = "/s/" + sessionId.ToString("N") + "/c/" + captureId + "/", Status = "Pending", CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = expiresAtUtc, Photos = photos };
            await captures.SaveAsync(capture, token);await SaveBusinessAssets(session,capture,token).ConfigureAwait(false); return capture;
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
            var asset=CreateAsset(captureId,null,localPath,photoType,1,sourceAssetIds,FileIdentity(localPath));
            photos.Add(asset);capture.Photos=photos;await captures.SaveAsync(capture,token);var session=await sessions.GetAsync(capture.SessionId,token);if(session!=null)await SaveBusinessAssets(session,capture,token).ConfigureAwait(false);return asset;
        }

        static CapturePhoto CreateAsset(string captureId,string capturedImageId,string path,string type,int position,IReadOnlyList<string> sourceIds,string preferredId)
        {
            if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new FileNotFoundException("Capture asset is unavailable.",path);
            return new CapturePhoto{Id=string.IsNullOrWhiteSpace(preferredId)?Guid.NewGuid().ToString("N"):preferredId,CaptureId=captureId,CapturedImageId=capturedImageId,LocalPath=path,PhotoType=type,Position=position,MimeType=Mime(type,path),FileLength=new FileInfo(path).Length,ContentHashSha256=Hash(path),CreatedAtUtc=DateTime.UtcNow,AssetStatus="Ready",SourceAssetIds=(sourceIds??new string[0]).Distinct(StringComparer.Ordinal).ToList(),IsUploaded=false};
        }
        async Task SaveBusinessAssets(Session session,PhotoCapture capture,CancellationToken token)
        {
            if(session==null||!session.IsBoothSession||mediaAssets==null||storage==null)return;
            foreach(var photo in capture.Photos??new CapturePhoto[0])
            {
                var kind=BusinessKind(photo.PhotoType);if(kind==null)continue;var now=photo.CreatedAtUtc==DateTime.MinValue?DateTime.UtcNow:photo.CreatedAtUtc;
                await mediaAssets.SaveAsync(new MediaAssetRecord{Id=photo.Id,SessionId=session.Id,CaptureAttemptId=photo.CapturedImageId,Kind=kind,RelativePath=storage.GetRelativePath(photo.LocalPath),MimeType=photo.MimeType,FileLength=photo.FileLength,ContentHashSha256=photo.ContentHashSha256,Status=MediaAssetStates.Ready,RetentionClass=photo.PhotoType==CaptureAssetTypes.Picture||photo.PhotoType==CaptureAssetTypes.Video?MediaRetentionClasses.Original:MediaRetentionClasses.Deliverable,CreatedAtUtc=now,UpdatedAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
            }
        }
        static string BusinessKind(string type){if(type==CaptureAssetTypes.Picture)return MediaAssetKinds.OriginalPicture;if(type==CaptureAssetTypes.Video)return MediaAssetKinds.OriginalVideo;if(type==CaptureAssetTypes.Composite)return MediaAssetKinds.FinalComposite;if(type==CaptureAssetTypes.CompositeVideo)return MediaAssetKinds.FinalVideo;if(type==CaptureAssetTypes.Gif)return MediaAssetKinds.Gif;if(type==CaptureAssetTypes.ShareArchive)return MediaAssetKinds.ShareArchive;return null;}
        static string FileIdentity(string path){Guid id;var name=Path.GetFileNameWithoutExtension(path);return Guid.TryParseExact(name,"N",out id)?name:null;}
        static string Mime(string type,string path){if(string.Equals(type,CaptureAssetTypes.Video,StringComparison.Ordinal)||string.Equals(type,CaptureAssetTypes.CompositeVideo,StringComparison.Ordinal))return "video/mp4";if(string.Equals(type,CaptureAssetTypes.Picture,StringComparison.Ordinal))return "image/jpeg";if(string.Equals(type,CaptureAssetTypes.Gif,StringComparison.Ordinal))return "image/gif";if(string.Equals(type,CaptureAssetTypes.ShareArchive,StringComparison.Ordinal))return "application/zip";var extension=Path.GetExtension(path);return string.Equals(extension,".png",StringComparison.OrdinalIgnoreCase)?"image/png":"application/octet-stream";}
        static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",string.Empty).ToLowerInvariant();}
        async Task<Deliverable> IDeliverableService.CreateAsync(Guid boothSessionId,Guid? frameId,string finalCompositeAssetId,string finalCompositePath,IReadOnlyList<CapturedShot> shots,DateTime? expiresAtUtc,CancellationToken token)=>ToDeliverable(await CreateAsync(boothSessionId,frameId,finalCompositeAssetId,finalCompositePath,shots,expiresAtUtc,token).ConfigureAwait(false));
        async Task<Deliverable> IDeliverableService.CreateWithCompositeVideoAsync(Guid boothSessionId,Guid? frameId,string finalCompositeAssetId,string finalCompositePath,IReadOnlyList<CapturedShot> shots,string compositeVideoPath,IReadOnlyList<string> videoSourcePaths,DateTime? expiresAtUtc,CancellationToken token)=>ToDeliverable(await CreateWithCompositeVideoAsync(boothSessionId,frameId,finalCompositeAssetId,finalCompositePath,shots,compositeVideoPath,videoSourcePaths,expiresAtUtc,token).ConfigureAwait(false));
        async Task<Deliverable> IDeliverableService.GetAsync(string deliverableId,CancellationToken token)=>ToDeliverable(await GetAsync(deliverableId,token).ConfigureAwait(false));
        async Task<Deliverable> IDeliverableService.GetAsync(Guid boothSessionId,string deliverableId,CancellationToken token)=>ToDeliverable(await GetAsync(boothSessionId,deliverableId,token).ConfigureAwait(false));
        async Task<IReadOnlyList<Deliverable>> IDeliverableService.GetByBoothSessionAsync(Guid boothSessionId,CancellationToken token)=>(await GetBySessionAsync(boothSessionId,token).ConfigureAwait(false)).Select(ToDeliverable).ToList();
        Task IDeliverableService.UpdateSharePathAsync(string deliverableId,string sharePath,CancellationToken token)=>UpdateSharePathAsync(deliverableId,sharePath,token);
        async Task<DeliverableAsset> IDeliverableService.AddAssetAsync(string deliverableId,string localPath,string role,IReadOnlyList<string> sourceAssetIds,CancellationToken token)=>ToDeliverableAsset(await AddFileAsync(deliverableId,localPath,role,sourceAssetIds,token).ConfigureAwait(false));
        static Deliverable ToDeliverable(PhotoCapture value)=>value==null?null:new Deliverable{Id=value.Id,BoothSessionId=value.SessionId,FrameId=value.FrameId,CompositeImageId=value.CompositeImageId,CompositePath=value.CompositePath,SharePath=value.SharePath,Status=value.Status,MediaMode=value.MediaMode,UploadAttempts=value.UploadAttempts,CreatedAtUtc=value.CreatedAtUtc,UploadedAtUtc=value.UploadedAtUtc,ExpiresAtUtc=value.ExpiresAtUtc,LastError=value.LastError,Assets=(value.Photos??new CapturePhoto[0]).Select(ToDeliverableAsset).ToList()};
        static DeliverableAsset ToDeliverableAsset(CapturePhoto value)=>value==null?null:new DeliverableAsset{Id=value.Id,DeliverableId=value.CaptureId,CapturedShotId=value.CapturedImageId,LocalPath=value.LocalPath,Role=value.PhotoType,Position=value.Position,MimeType=value.MimeType,FileLength=value.FileLength,ContentHashSha256=value.ContentHashSha256,CreatedAtUtc=value.CreatedAtUtc,AssetStatus=value.AssetStatus,SourceAssetIds=value.SourceAssetIds,CloudinaryPublicId=value.CloudinaryPublicId,IsUploaded=value.IsUploaded,UploadAttempts=value.UploadAttempts,UploadedAtUtc=value.UploadedAtUtc,LastError=value.LastError};
    }

    public sealed class CaptureService : DeliverableService
    {
        public CaptureService(ICaptureRepository captures,ISessionRepository sessions):base(captures,sessions){}
        public CaptureService(ICaptureRepository captures,ISessionRepository sessions,IMediaAssetRepository mediaAssets,IStorageManager storage):base(captures,sessions,mediaAssets,storage){}
    }
}
