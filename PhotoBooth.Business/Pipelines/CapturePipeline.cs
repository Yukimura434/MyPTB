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
        readonly ILogger<CapturePipeline> log;

        public CapturePipeline(ICameraService camera, ISessionRepository sessions, ISettingsService settings, IVideoService videos, IColorLutService colorLuts = null, IFeatureFlagService features = null, IBeautySettingsService beautySettings = null, IBeautyRetouchService beauty = null, ILogger<CapturePipeline> log = null)
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
            var session = await sessions.GetAsync(id, token);
            if (session == null) throw new InvalidOperationException("Session not found.");

            var captureDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? session.OutputDirectory
                : Path.GetFullPath(workingDirectory);
            Directory.CreateDirectory(captureDirectory);
            var sequence = await sessions.GetNextCaptureSequenceAsync(session.Id, token);
            var imageId = session.StartedAtUtc.ToLocalTime().ToString("yyMMdd") + session.SessionNumber.ToString("D2") + sequence.ToString("D4");
            var pictureDestination = Path.Combine(captureDirectory, imageId + ".jpg");
            var videoDestination = Path.Combine(captureDirectory, imageId + ".mp4");

            var appSettings = await settings.GetAsync(token) ?? new Settings();
            var saveMode = appSettings.SaveLocation;
            var autoFlip = appSettings.AutoFlip;
            var imageRotation = NormalizeRotation(appSettings.ImageRotationDegrees);
            var videoEnabled = includeVideo && (features == null || await features.IsEnabledAsync("Video", token).ConfigureAwait(false));

            // Capture is staged to a temporary file first so that the camera's own
            // card copy (camera filename) and the software's renamed PC file never
            // collide. The auto-flip (mirror) is applied before rename and save.
            var staging = Path.Combine(captureDirectory, imageId + ".staging.jpg");
            var committed = false;
            try
            {
                var shutterTimestampUtc = DateTime.UtcNow;
                var result = await camera.CaptureAsync(cameraId, false, staging, saveMode, token);
                if (result == null || !result.Succeeded) throw new InvalidOperationException(result?.Error ?? "Capture failed.");
                if (!File.Exists(staging)) throw new IOException("The camera transfer completed without a session image.");

                var rawStill = Path.Combine(captureDirectory, imageId + ".video-still.jpg");
                FinalizeImage(staging, rawStill, autoFlip, imageRotation);
                File.Copy(rawStill, pictureDestination, true);
                if (beautySettings != null && beauty != null)
                {
                    var beautySnapshot = await beautySettings.GetAsync(token).ConfigureAwait(false);
                    if (beautySnapshot != null && beautySnapshot.HasEffect)
                    {
                        try { await beauty.ProcessAsync(pictureDestination, pictureDestination, beautySnapshot, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception) { log?.LogWarning(exception, "Beauty retouch failed; the original captured picture will continue through the pipeline"); }
                    }
                }
                var colorPresetId = session.PresetId ?? appSettings.DefaultPresetId;
                if (colorPresetId.HasValue && colorLuts != null)
                    await colorLuts.ApplyCaptureAsync(colorPresetId.Value, pictureDestination, token).ConfigureAwait(false);
                try
                {
                    if (videoEnabled)
                        await videos.CreateAsync(rawStill, videoDestination, shutterTimestampUtc, videoDurationSeconds, autoFlip, imageRotation, token).ConfigureAwait(false);
                }
                finally
                {
                    try { if (File.Exists(rawStill)) File.Delete(rawStill); } catch { }
                }

                result.ImageId = imageId;
                await sessions.AddCapturedShotAsync(session.Id, new CapturedShot
                {
                    Id = imageId,
                    Sequence = sequence,
                    PicturePath = pictureDestination,
                    VideoPath = videoEnabled ? videoDestination : null,
                    CapturedAtUtc = shutterTimestampUtc
                }, token);
                committed = true;
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch { }
                if (!committed)
                {
                    try { if (File.Exists(pictureDestination)) File.Delete(pictureDestination); } catch { }
                    try { if (File.Exists(videoDestination)) File.Delete(videoDestination); } catch { }
                }
            }
            return await sessions.GetAsync(session.Id, token);
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
