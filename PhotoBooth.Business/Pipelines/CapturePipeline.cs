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

        public CapturePipeline(ICameraService camera, ISessionRepository sessions, ISettingsService settings)
        {
            this.camera = camera;
            this.sessions = sessions;
            this.settings = settings;
        }

        public async Task<Session> ExecuteAsync(Guid id, string cameraId, CancellationToken token)
        {
            var session = await sessions.GetAsync(id, token);
            if (session == null) throw new InvalidOperationException("Session not found.");

            Directory.CreateDirectory(session.OutputDirectory);
            var sequence = await sessions.GetNextCaptureSequenceAsync(session.Id, token);
            var imageId = session.StartedAtUtc.ToLocalTime().ToString("yyMMdd") + session.SessionNumber.ToString("D2") + sequence.ToString("D4");
            var destination = Path.Combine(session.OutputDirectory, imageId + ".jpg");

            var appSettings = await settings.GetAsync(token) ?? new Settings();
            var saveMode = appSettings.SaveLocation;
            var autoFlip = appSettings.AutoFlip;

            // Capture is staged to a temporary file first so that the camera's own
            // card copy (camera filename) and the software's renamed PC file never
            // collide. The auto-flip (mirror) is applied before rename and save.
            var staging = Path.Combine(session.OutputDirectory, imageId + ".staging.jpg");
            try
            {
                var result = await camera.CaptureAsync(cameraId, false, staging, saveMode, token);
                if (result == null || !result.Succeeded) throw new InvalidOperationException(result?.Error ?? "Capture failed.");
                if (!File.Exists(staging)) throw new IOException("The camera transfer completed without a session image.");

                FinalizeImage(staging, destination, autoFlip);

                result.ImageId = imageId;
                await sessions.AddCapturedImageAsync(session.Id, sequence, imageId, destination, token);
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch { }
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
