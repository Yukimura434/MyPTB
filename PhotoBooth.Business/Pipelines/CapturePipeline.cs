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

        public CapturePipeline(ICameraService camera, ISessionRepository sessions, ISettingsService settings, IVideoService videos, IColorLutService colorLuts = null, IFeatureFlagService features = null)
        {
            this.camera = camera;
            this.sessions = sessions;
            this.settings = settings;
            this.videos = videos;
            this.colorLuts = colorLuts;
            this.features = features;
        }

        public async Task<Session> ExecuteAsync(Guid id, string cameraId, CancellationToken token)
        {
            return await ExecuteAsync(id, cameraId, null, token).ConfigureAwait(false);
        }

        public async Task<Session> ExecuteAsync(Guid id, string cameraId, string workingDirectory, CancellationToken token)
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
            var videoEnabled = features == null || await features.IsEnabledAsync("Video", token).ConfigureAwait(false);

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
                FinalizeImage(staging, rawStill, autoFlip);
                File.Copy(rawStill, pictureDestination, true);
                var colorPresetId = session.PresetId ?? appSettings.DefaultPresetId;
                if (colorPresetId.HasValue && colorLuts != null)
                    await colorLuts.ApplyCaptureAsync(colorPresetId.Value, pictureDestination, token).ConfigureAwait(false);
                try
                {
                    if (videoEnabled)
                        await videos.CreateAsync(rawStill, videoDestination, shutterTimestampUtc, autoFlip, token).ConfigureAwait(false);
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

        static void FinalizeImage(string staging, string destination, bool autoFlip)
        {
            if (!autoFlip)
            {
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(staging, destination);
                return;
            }
            using (var image = System.Drawing.Bitmap.FromFile(staging))
            {
                image.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipX);
                image.Save(destination, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
        }
    }
}
