using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Face;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.OpenCvRetouch
{
    /// <summary>Light preview with a bounded detector worker and owned BGR24 output.</summary>
    public sealed class OpenCvLiveBeautyPreviewService : ILiveBeautyPreviewService, IDisposable
    {
        readonly SemaphoreSlim previewGate = new SemaphoreSlim(1, 1);
        readonly object sync = new object();
        readonly string assets;
        YuNetFaceDetector detector;
        FacemarkLBF facemark;
        Task detectorTask = Task.CompletedTask;
        Snapshot pending, ready;
        Exception detectorError;
        bool detecting, disposed;
        long generation, trackedGeneration = -1, trackEpoch;
        Mat previous = new Mat();
        Point2f[][] points = Array.Empty<Point2f[]>();
        double lastRequest = double.NegativeInfinity, confidence = 1;
        static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
        public OpenCvLiveBeautyPreviewService() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Beauty")) { }
        public OpenCvLiveBeautyPreviewService(string assetsDirectory) { assets = assetsDirectory; BeautyRuntime.EnsureConfigured(); }

        public async Task<LiveBeautyPixels> ProcessAsync(byte[] jpegData, BeautySettings settings, CancellationToken token)
        {
            if (jpegData == null || jpegData.Length == 0 || settings?.HasLiveEffect != true || token.IsCancellationRequested) return null;
            var snapshot = settings.Clone();
            snapshot.ClampStrengths();
            long version = Interlocked.Read(ref generation);
            double submitted = Now;
            if (!await previewGate.WaitAsync(0).ConfigureAwait(false)) return null;
            try { return await Task.Run(() => Process(jpegData, snapshot, token, version, submitted)).ConfigureAwait(false); }
            finally { previewGate.Release(); }
        }

        LiveBeautyPixels Process(byte[] jpeg, BeautySettings settings, CancellationToken token, long version, double submitted)
        {
            if (Stale(version, token)) return null;
            if (trackedGeneration != version)
            {
                points = Array.Empty<Point2f[]>(); previous.Dispose(); previous = new Mat();
                trackEpoch++; lastRequest = double.NegativeInfinity; trackedGeneration = version;
            }
            using (var decoded = Cv2.ImDecode(jpeg, ImreadModes.Color))
            using (var image = new Mat())
            using (var gray = new Mat())
            {
                if (decoded.Empty()) return null;
                // Bound preview work independently of camera JPEG resolution.
                double scale = Math.Min(1d, 640d / Math.Max(decoded.Width, decoded.Height));
                if (scale < 1) Cv2.Resize(decoded, image, new Size(0, 0), scale, scale, InterpolationFlags.Area);
                else decoded.CopyTo(image);
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                if (points.Length > 0 && (previous.Size() != gray.Size() || !LiveBeautyTracking.Track(previous, gray, ref points, out confidence)))
                { points = Array.Empty<Point2f[]>(); trackEpoch++; lastRequest = double.NegativeInfinity; }
                Snapshot completed;
                lock (sync)
                {
                    if (detectorError != null) throw new InvalidOperationException("Live Beauty detection failed.", detectorError);
                    completed = ready; ready = null;
                }
                if (completed != null) using (completed)
                {
                    if (completed.Generation == version && completed.Epoch == trackEpoch && Now - completed.Submitted <= 150 &&
                        completed.Submitted <= submitted && completed.Gray.Size() == gray.Size())
                    {
                        var candidate = completed.Points; double quality = 1;
                        if (candidate.Length == 0 || LiveBeautyTracking.Track(completed.Gray, gray, ref candidate, out quality))
                        { points = candidate; confidence = quality; }
                    }
                }
                double interval = points.Length == 0 ? 100 : confidence >= .65 ? 400 : confidence >= .35 ? 200 : 100;
                if (Now - lastRequest >= interval && !Stale(version, token))
                {
                    var job = new Snapshot { Image = image.Clone(), Gray = gray.Clone(), Generation = version, Epoch = trackEpoch, Submitted = submitted };
                    lock (sync)
                    {
                        if (disposed || generation != version) job.Dispose();
                        else
                        {
                            pending?.Dispose(); pending = job;
                            if (!detecting) { detecting = true; detectorTask = Task.Run((Action)DetectLoop); }
                        }
                    }
                    lastRequest = Now;
                }
                gray.CopyTo(previous);
                bool applied = false;
                if (points.Length > 0)
                using (var skin = new Mat(image.Size(), MatType.CV_8UC1))
                using (var features = new Mat())
                using (var underEyes = new Mat())
                {
                    FaceSkinMaskBuilder.Build(image, points, 1, skin, features, underEyes);
                    applied = Cv2.CountNonZero(skin) > 0;
                    LiveBeautyTracking.Light(image, skin, settings.SmoothSkin, settings.BrightenSkin);
                }
                if (Stale(version, token)) return null;
                int stride = image.Width * 3;
                var pixels = new byte[stride * image.Height];
                for (int y = 0; y < image.Height; y++) Marshal.Copy(image.Ptr(y), pixels, y * stride, stride);
                return new LiveBeautyPixels(pixels, image.Width, image.Height, applied);
            }
        }
        bool Stale(long version, CancellationToken token) => token.IsCancellationRequested || Volatile.Read(ref disposed) || version != Interlocked.Read(ref generation);

        void DetectLoop()
        {
            while (true)
            {
                Snapshot job;
                lock (sync) { job = pending; pending = null; if (job == null) { detecting = false; return; } }
                try
                {
                    if (detector == null)
                    {
                        var loaded = FacemarkLBF.Create();
                        try
                        {
                            loaded.LoadModel(Path.Combine(assets, "lbfmodel.yaml"));
                            detector = new YuNetFaceDetector(Path.Combine(assets, YuNetFaceDetector.ModelFileName));
                            facemark = loaded;
                        }
                        catch { loaded.Dispose(); throw; }
                    }
                    var boxes = detector.Detect(job.Image);
                    job.Points = Array.Empty<Point2f[]>();
                    if (boxes.Length > 0) using (var eq = new Mat()) using (var faces = Mat.FromArray(boxes))
                    {
                        Cv2.EqualizeHist(job.Gray, eq);
                        if (!facemark.Fit(eq, faces, out job.Points)) job.Points = Array.Empty<Point2f[]>();
                    }
                    job.Image.Dispose(); job.Image = null;
                    lock (sync) if (!disposed && generation == job.Generation) { ready?.Dispose(); ready = job; job = null; }
                }
                catch (Exception error) { lock (sync) if (!disposed && job.Generation == generation) detectorError = error; }
                finally { job?.Dispose(); }
            }
        }
        public void Reset()
        {
            lock (sync)
            {
                Interlocked.Increment(ref generation);
                pending?.Dispose(); pending = null; ready?.Dispose(); ready = null; detectorError = null;
            }
        }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; }
            Reset(); previewGate.Wait();
            try { Task worker; lock (sync) worker = detectorTask; worker.GetAwaiter().GetResult(); previous.Dispose(); facemark?.Dispose(); detector?.Dispose(); }
            finally { previewGate.Release(); }
            // Do not dispose a semaphore which already-entered callers may still release.
        }
        sealed class Snapshot : IDisposable
        {
            internal Mat Image, Gray;
            internal Point2f[][] Points;
            internal long Generation, Epoch;
            internal double Submitted;
            public void Dispose() { Image?.Dispose(); Gray?.Dispose(); }
        }
    }
}
