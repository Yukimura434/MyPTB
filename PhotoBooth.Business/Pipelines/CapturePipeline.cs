using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Pipelines;
using PhotoBooth.Core.Services;
using Microsoft.Extensions.Logging;

namespace PhotoBooth.Business.Pipelines
{
    public sealed class CapturePipeline : ICapturePipeline
    {
        readonly ICameraService camera;
        readonly ISessionRepository sessions;
        readonly ISettingsService settings;
        readonly IVideoService videos;
        readonly IColorLutService colorLuts;
        readonly IFeatureFlagService features;
        readonly IBeautySettingsService beautySettings;
        readonly IBeautyRetouchService beauty;
        readonly ICaptureAttemptRepository captureAttempts;
        readonly IMediaAssetRepository mediaAssets;
        readonly IStorageManager storage;
        readonly ILogger<CapturePipeline> log;

        public CapturePipeline(ICameraService camera, ISessionRepository sessions, ISettingsService settings, IVideoService videos, IColorLutService colorLuts = null, IFeatureFlagService features = null, IBeautySettingsService beautySettings = null, IBeautyRetouchService beauty = null, ILogger<CapturePipeline> log = null, ICaptureAttemptRepository captureAttemptRepository = null, IMediaAssetRepository mediaAssetRepository = null, IStorageManager storageManager = null)
        {
            this.camera = camera;
            this.sessions = sessions;
            this.settings = settings;
            this.videos = videos;
            this.colorLuts = colorLuts;
            this.features = features;
            this.beautySettings = beautySettings;
            this.beauty = beauty;
            this.log = log;
            captureAttempts=captureAttemptRepository;
            mediaAssets=mediaAssetRepository;
            storage=storageManager;
        }

        public async Task<Session> ExecuteAsync(Guid id, string cameraId, CancellationToken token)
        {
            return await ExecuteAsync(id, cameraId, null, token).ConfigureAwait(false);
        }

        public async Task<Session> ExecuteAsync(Guid id, string cameraId, string workingDirectory, CancellationToken token)
        {
            return await ExecuteAsync(id, cameraId, workingDirectory, true, token).ConfigureAwait(false);
        }

        public async Task<Session> ExecuteAsync(Guid id, string cameraId, string workingDirectory, bool includeVideo, CancellationToken token)
        {
            var configured = await settings.GetAsync(token).ConfigureAwait(false) ?? new Settings();
            return await ExecuteAsync(id, cameraId, workingDirectory, includeVideo, configured.CountdownSeconds, token).ConfigureAwait(false);
        }

        public async Task<Session> ExecuteAsync(Guid id, string cameraId, string workingDirectory, bool includeVideo, int videoDurationSeconds, CancellationToken token)
        {
            var pending = await CapturePendingAsync(id, cameraId, workingDirectory, includeVideo, videoDurationSeconds, token).ConfigureAwait(false);
            await FinalizePendingAsync(id, new[] { pending }, token).ConfigureAwait(false);
            return await sessions.GetAsync(id, token).ConfigureAwait(false);
        }

        public async Task<PendingCapture> CapturePendingAsync(Guid sessionId,string cameraId,string workingDirectory,bool includeVideo,int videoDurationSeconds,CancellationToken token)
        {
            var session=await sessions.GetAsync(sessionId,token).ConfigureAwait(false);
            if(session==null)throw new InvalidOperationException("Session not found.");
            var captureDirectory=string.IsNullOrWhiteSpace(workingDirectory)?session.OutputDirectory:Path.GetFullPath(workingDirectory);
            Directory.CreateDirectory(captureDirectory);
            var sequence=await sessions.GetNextCaptureSequenceAsync(session.Id,token).ConfigureAwait(false);
            var appSettings=await settings.GetAsync(token).ConfigureAwait(false)??new Settings();
            var videoEnabled=includeVideo&&(features==null||await features.IsEnabledAsync("Video",token).ConfigureAwait(false));
            var attemptId=Guid.NewGuid().ToString("N");
            var pictureAssetId=Guid.NewGuid().ToString("N");
            var videoAssetId=videoEnabled?Guid.NewGuid().ToString("N"):null;
            var tracked=session.IsBoothSession&&captureAttempts!=null&&mediaAssets!=null&&storage!=null;
            var pending=new PendingCapture
            {
                Id=attemptId,Sequence=sequence,RawPicturePath=Path.Combine(captureDirectory,attemptId+".raw.jpg"),
                PicturePath=Path.Combine(captureDirectory,pictureAssetId+".jpg"),VideoPath=videoEnabled?Path.Combine(captureDirectory,videoAssetId+".mp4"):null,
                VideoSnapshotPath=videoEnabled?Path.Combine(captureDirectory,attemptId+".video-frames"):null,
                PictureAssetId=pictureAssetId,VideoAssetId=videoAssetId,FlipHorizontally=appSettings.AutoFlip,
                RotationDegrees=NormalizeRotation(appSettings.ImageRotationDegrees),VideoDurationSeconds=Math.Max(1,videoDurationSeconds),
                ColorPresetId=session.PresetId??appSettings.DefaultPresetId,IsTracked=tracked
            };
            if(beautySettings!=null&&beauty!=null)
            {
                var configured=await beautySettings.GetAsync(token).ConfigureAwait(false);
                pending.BeautySettings=configured?.Clone();
            }
            if(tracked)await captureAttempts.BeginAsync(new CaptureAttemptRecord{Id=attemptId,SessionId=session.Id,Sequence=sequence,AttemptNumber=1,CameraId=cameraId,PictureAssetId=pictureAssetId,VideoAssetId=videoAssetId,Status=CaptureAttemptStates.IntentRecorded,IntentAtUtc=DateTime.UtcNow},token).ConfigureAwait(false);
            var staging=Path.Combine(captureDirectory,attemptId+".partial.jpg");
            var cameraCompleted=false;
            try
            {
                pending.CapturedAtUtc=DateTime.UtcNow;
                var result=await camera.CaptureAsync(cameraId,false,staging,appSettings.SaveLocation,token).ConfigureAwait(false);
                cameraCompleted=true;
                if(result==null||!result.Succeeded)throw new InvalidOperationException(result?.Error??"Capture failed.");
                if(!File.Exists(staging))throw new IOException("The camera transfer completed without a session image.");
                if(File.Exists(pending.RawPicturePath))File.Delete(pending.RawPicturePath);
                File.Move(staging,pending.RawPicturePath);
                if(videoEnabled&&videos is IDeferredVideoService deferred)
                    pending.VideoSnapshotPath=await deferred.SnapshotAsync(pending.VideoSnapshotPath,pending.CapturedAtUtc,pending.VideoDurationSeconds,token).ConfigureAwait(false);
                return pending;
            }
            catch(Exception exception)
            {
                if(tracked)try{await captureAttempts.MarkFailedAsync(attemptId,exception.Message,!cameraCompleted,CancellationToken.None).ConfigureAwait(false);}catch(Exception recordError){log?.LogError(recordError,"Capture attempt {AttemptId} failure could not be checkpointed",attemptId);}
                DeleteFile(staging);DeletePendingFiles(pending);
                throw;
            }
            finally{DeleteFile(staging);}
        }

        public async Task<IReadOnlyList<CapturedShot>> FinalizePendingAsync(Guid sessionId,IReadOnlyList<PendingCapture> captures,CancellationToken token)
        {
            if(captures==null)throw new ArgumentNullException(nameof(captures));
            var pendingCaptures=captures.Where(x=>x!=null&&!x.IsFinalized).ToList();
            var result=new List<CapturedShot>(pendingCaptures.Count);
            try
            {
                // Complete every CPU/disk-heavy operation before writing the batch
                // to CapturedImages. A processing failure therefore cannot leave a
                // partially committed set of booth photos.
                foreach(var pending in pendingCaptures)
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(await ProcessOneAsync(pending,token).ConfigureAwait(false));
                }
                foreach(var pending in pendingCaptures.Where(x=>x.IsTracked))
                {
                    await SaveAsset(sessionId,pending.Id,pending.PictureAssetId,MediaAssetKinds.OriginalPicture,pending.PicturePath,"image/jpeg",token).ConfigureAwait(false);
                    if(!string.IsNullOrWhiteSpace(pending.VideoPath))await SaveAsset(sessionId,pending.Id,pending.VideoAssetId,MediaAssetKinds.OriginalVideo,pending.VideoPath,"video/mp4",token).ConfigureAwait(false);
                }
                await sessions.AddCapturedShotsAsync(sessionId,result,token).ConfigureAwait(false);
                foreach(var pending in pendingCaptures)pending.IsFinalized=true;
                foreach(var pending in pendingCaptures.Where(x=>x.IsTracked))
                    try{await captureAttempts.MarkAcceptedAsync(pending.Id,CancellationToken.None).ConfigureAwait(false);}
                    catch(Exception error){log?.LogError(error,"Committed capture attempt {AttemptId} could not be marked accepted",pending.Id);}
                return result;
            }
            catch(Exception exception)
            {
                foreach(var pending in pendingCaptures.Where(x=>x.IsTracked))
                {
                    try{await captureAttempts.MarkFailedAsync(pending.Id,exception.Message,false,CancellationToken.None).ConfigureAwait(false);}catch(Exception recordError){log?.LogError(recordError,"Capture attempt {AttemptId} failure could not be checkpointed",pending.Id);}
                    try{await mediaAssets.MarkDeletedAsync(pending.PictureAssetId,CancellationToken.None).ConfigureAwait(false);}catch{}
                    try{await mediaAssets.MarkDeletedAsync(pending.VideoAssetId,CancellationToken.None).ConfigureAwait(false);}catch{}
                }
                foreach(var pending in pendingCaptures)DeletePendingFiles(pending);
                throw;
            }
            finally
            {
                foreach(var pending in pendingCaptures)
                {
                    DeleteFile(pending.RawPicturePath+".video-source.jpg");DeleteDirectory(pending.VideoSnapshotPath);
                    if(pending.IsFinalized)DeleteFile(pending.RawPicturePath);
                }
            }
        }

        async Task<CapturedShot> ProcessOneAsync(PendingCapture pending,CancellationToken token)
        {
            if(!File.Exists(pending.RawPicturePath))throw new FileNotFoundException("The pending camera image is unavailable.",pending.RawPicturePath);
            var videoEnabled=!string.IsNullOrWhiteSpace(pending.VideoPath);
            var videoSource=videoEnabled?pending.RawPicturePath+".video-source.jpg":null;
            await Task.Run(()=>FinalizeImage(pending.RawPicturePath,videoEnabled?videoSource:pending.PicturePath,pending.FlipHorizontally,pending.RotationDegrees),token).ConfigureAwait(false);
            if(videoEnabled)File.Copy(videoSource,pending.PicturePath,true);
            if(pending.BeautySettings?.HasEffect==true&&beauty!=null)
            {
                try{await beauty.ProcessAsync(pending.PicturePath,pending.PicturePath,pending.BeautySettings,token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                catch(Exception exception){log?.LogWarning(exception,"Beauty retouch failed; the original captured picture will continue through the pipeline");}
            }
            if(pending.ColorPresetId.HasValue&&colorLuts!=null)
                await colorLuts.ApplyCaptureAsync(pending.ColorPresetId.Value,pending.PicturePath,token).ConfigureAwait(false);
            if(videoEnabled)
            {
                if(videos is IDeferredVideoService deferred&&!string.IsNullOrWhiteSpace(pending.VideoSnapshotPath))
                    await deferred.CreateFromSnapshotAsync(videoSource,pending.VideoPath,pending.VideoSnapshotPath,pending.FlipHorizontally,pending.RotationDegrees,token).ConfigureAwait(false);
                else
                    await videos.CreateAsync(videoSource,pending.VideoPath,pending.CapturedAtUtc,pending.VideoDurationSeconds,pending.FlipHorizontally,pending.RotationDegrees,token).ConfigureAwait(false);
            }
            return new CapturedShot{Id=pending.Id,Sequence=pending.Sequence,PicturePath=pending.PicturePath,VideoPath=videoEnabled?pending.VideoPath:null,PictureAssetId=pending.PictureAssetId,VideoAssetId=pending.VideoAssetId,CapturedAtUtc=pending.CapturedAtUtc};
        }

        public async Task DiscardPendingAsync(IReadOnlyList<PendingCapture> captures,string reason,CancellationToken token)
        {
            if(captures==null)return;
            foreach(var pending in captures)
            {
                token.ThrowIfCancellationRequested();
                if(pending==null||pending.IsFinalized)continue;
                if(pending.IsTracked)try{await captureAttempts.MarkFailedAsync(pending.Id,string.IsNullOrWhiteSpace(reason)?"Pending capture discarded.":reason,false,CancellationToken.None).ConfigureAwait(false);}catch(Exception error){log?.LogWarning(error,"Pending capture {AttemptId} could not be marked failed",pending.Id);}
                DeletePendingFiles(pending);
            }
        }

        static void DeletePendingFiles(PendingCapture pending)
        {
            if(pending==null)return;
            DeleteFile(pending.RawPicturePath);DeleteFile(pending.RawPicturePath+".video-source.jpg");DeleteFile(pending.PicturePath);DeleteFile(pending.VideoPath);DeleteDirectory(pending.VideoSnapshotPath);
        }

        static void DeleteFile(string path){try{if(!string.IsNullOrWhiteSpace(path)&&File.Exists(path))File.Delete(path);}catch{}}
        static void DeleteDirectory(string path){try{if(!string.IsNullOrWhiteSpace(path)&&Directory.Exists(path))Directory.Delete(path,true);}catch{}}

        async Task SaveAsset(Guid sessionId,string attemptId,string assetId,string kind,string path,string mime,CancellationToken token)
        {
            var now=DateTime.UtcNow;await mediaAssets.SaveAsync(new MediaAssetRecord{Id=assetId,SessionId=sessionId,CaptureAttemptId=attemptId,Kind=kind,RelativePath=storage.GetRelativePath(path),MimeType=mime,FileLength=new FileInfo(path).Length,Status=MediaAssetStates.Ready,RetentionClass=MediaRetentionClasses.Original,CreatedAtUtc=now,UpdatedAtUtc=now},token).ConfigureAwait(false);
        }

        static void FinalizeImage(string staging, string destination, bool autoFlip, int rotationDegrees)
        {
            if (!autoFlip && rotationDegrees == 0)
            {
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(staging, destination);
                return;
            }
            using (var image = System.Drawing.Bitmap.FromFile(staging))
            {
                if (autoFlip) image.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipX);
                if (rotationDegrees == 90) image.RotateFlip(System.Drawing.RotateFlipType.Rotate90FlipNone);
                else if (rotationDegrees == -90) image.RotateFlip(System.Drawing.RotateFlipType.Rotate270FlipNone);
                else if (rotationDegrees == 180) image.RotateFlip(System.Drawing.RotateFlipType.Rotate180FlipNone);
                image.Save(destination, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
        }

        static int NormalizeRotation(int value) => value == 90 || value == -90 || value == 180 ? value : 0;
    }
}
