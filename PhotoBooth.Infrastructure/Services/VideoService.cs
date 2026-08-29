using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Accord.Video.FFMPEG;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class VideoService : IVideoService
    {
        internal const int FramesPerSecond = 18;
        internal const int MaximumDurationSeconds = 8;
        internal static readonly TimeSpan MaximumPreroll = TimeSpan.FromSeconds(MaximumDurationSeconds);
        internal const int MaximumBufferedFrames = FramesPerSecond * MaximumDurationSeconds + 2;
        static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / FramesPerSecond);
        readonly object sync = new object();
        readonly List<BufferedFrame> frames = new List<BufferedFrame>();
        readonly bool nativeEncoderEnabled;
        readonly bool isolateNativeEncoder;
        readonly ILogger<VideoService> log;
        DateTime lastAcceptedUtc;

        // Kept for in-process encoder tests. Production DI isolates native
        // FFmpeg work in a child process so a native crash cannot kill the UI.
        public VideoService() : this(true, false, null) { }
        public VideoService(ApplicationOptions options, ILogger<VideoService> logger = null) : this(
            options != null && options.Features.TryGetValue("Video", out var moduleEnabled) && moduleEnabled &&
            options.Features.TryGetValue("VideoNativeEncoder", out var encoderEnabled) && encoderEnabled, true, logger) { }
        VideoService(bool nativeEncoderEnabled, bool isolateNativeEncoder, ILogger<VideoService> logger) { this.nativeEncoderEnabled = nativeEncoderEnabled; this.isolateNativeEncoder = isolateNativeEncoder; log=logger; }

        public void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc)
        {
            if (!nativeEncoderEnabled) return;
            if (imageData == null || imageData.Length == 0) return;
            timestampUtc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
            lock (sync)
            {
                if (lastAcceptedUtc != default(DateTime) && timestampUtc - lastAcceptedUtc < FrameInterval) return;
                frames.Add(new BufferedFrame(timestampUtc, (byte[])imageData.Clone()));
                lastAcceptedUtc = timestampUtc;
                var cutoff = timestampUtc - MaximumPreroll - FrameInterval;
                frames.RemoveAll(x => x.TimestampUtc < cutoff);
                if (frames.Count > MaximumBufferedFrames)
                    frames.RemoveRange(0, frames.Count - MaximumBufferedFrames);
            }
        }

        public void ClearLiveViewFrames()
        {
            lock (sync)
            {
                frames.Clear();
                lastAcceptedUtc = default(DateTime);
            }
        }

        internal int BufferedFrameCount { get { lock (sync) return frames.Count; } }
        internal long BufferedBytes { get { lock (sync) return frames.Sum(x => (long)x.ImageData.Length); } }

        public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, int durationSeconds, bool flipHorizontally, int rotationDegrees, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(stillImagePath)) throw new ArgumentException("Still image path is required.", nameof(stillImagePath));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));
            if (!nativeEncoderEnabled)
            {
                throw new InvalidOperationException("MP4 video encoding is disabled.");
            }
            durationSeconds = NormalizeDuration(durationSeconds);
            rotationDegrees = NormalizeRotation(rotationDegrees);
            var preroll = TimeSpan.FromSeconds(durationSeconds);
            shutterTimestampUtc = shutterTimestampUtc.Kind == DateTimeKind.Utc ? shutterTimestampUtc : shutterTimestampUtc.ToUniversalTime();
            BufferedFrame[] snapshot;
            lock (sync)
                snapshot = frames.Where(x => x.TimestampUtc > shutterTimestampUtc - preroll - FrameInterval && x.TimestampUtc <= shutterTimestampUtc).ToArray();
            LogMemory("Video capture encode starting", snapshot.Length, snapshot.Sum(x => (long)x.ImageData.Length));
            return Task.Run(() =>
            {
                if (isolateNativeEncoder)
                    CreateExternal(stillImagePath, destinationPath, shutterTimestampUtc, durationSeconds, snapshot, flipHorizontally, rotationDegrees, token);
                else
                    Create(stillImagePath, destinationPath, shutterTimestampUtc, durationSeconds, snapshot, flipHorizontally, rotationDegrees, token);
            }, token);
        }

        public Task ComposeAsync(string stillCompositePath, Frame frame, IReadOnlyDictionary<int, string> slotAssignments, string destinationPath, CancellationToken token)
        {
            if (!nativeEncoderEnabled) throw new InvalidOperationException("MP4 video encoding is disabled.");
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (slotAssignments == null || slotAssignments.Count == 0) throw new ArgumentException("MP4 video slot assignments are required.", nameof(slotAssignments));
            LogMemory("Composite video encode starting", slotAssignments.Count, 0);
            return Task.Run(() => ComposeExternal(stillCompositePath, frame, slotAssignments, destinationPath, token), token);
        }

        public Task<string> CreatePreviewVideoAsync(string videoPath, string previewDirectory, CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(previewDirectory);
                if (!string.Equals(Path.GetExtension(videoPath), ".mp4", StringComparison.OrdinalIgnoreCase) || !IsValidMp4(videoPath)) throw new InvalidDataException("The video is not a valid MP4 file: " + videoPath);
                return Path.GetFullPath(videoPath);
            }, token);
        }

        void ComposeExternal(string stillPath, Frame frame, IReadOnlyDictionary<int, string> assignments, string destinationPath, CancellationToken token)
        {
            if (!File.Exists(stillPath)) throw new FileNotFoundException("The still composite is unavailable.", stillPath);
            if (!File.Exists(frame.SourcePath)) throw new FileNotFoundException("The frame overlay is unavailable.", frame.SourcePath);
            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            Directory.CreateDirectory(directory);
            var attempt = Path.Combine(directory, ".video-composite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attempt);
            try
            {
                var primaryJpeg = Path.Combine(attempt, "primary.jpg");
                using (var source = new Bitmap(stillPath))
                using (var flattened = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(flattened)) { graphics.Clear(Color.White); graphics.DrawImage(source, 0, 0, source.Width, source.Height); }
                    flattened.Save(primaryJpeg, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                var manifest = Path.Combine(attempt, "slots.txt");
                using (var writer = new StreamWriter(manifest, false, new UTF8Encoding(false)))
                    foreach (var slot in frame.Slots.OrderBy(x => x.Index))
                    {
                        string source;
                        if (!assignments.TryGetValue(slot.Index, out source) || !File.Exists(source)) throw new FileNotFoundException("The video assigned to slot " + slot.Index + " is unavailable.", source);
                        writer.WriteLine(slot.Index + "\t" + slot.X + "\t" + slot.Y + "\t" + slot.Width + "\t" + slot.Height + "\t" +
                            slot.MediaZoom.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + slot.MediaCenterX.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" +
                            slot.MediaCenterY.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + source);
                    }
                var output = Path.Combine(attempt, "result.mp4");
                RunHelper("--video-compose " + Quote(primaryJpeg) + " " + Quote(frame.SourcePath) + " " + Quote(manifest) + " " + Quote(output), token);
                if (!IsValidMp4(output)) throw new InvalidDataException("The composed MP4 video is invalid.");
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(output, destinationPath);
            }
            finally { try { if (Directory.Exists(attempt)) Directory.Delete(attempt, true); } catch { } }
        }

        void CreateExternal(string stillImagePath, string destinationPath, DateTime shutterUtc, int durationSeconds, BufferedFrame[] source, bool flipHorizontally, int rotationDegrees, CancellationToken token)
        {
            ValidateSource(stillImagePath, shutterUtc, durationSeconds, source);
            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            Directory.CreateDirectory(directory);
            var attemptDirectory = Path.Combine(directory, ".video-" + Guid.NewGuid().ToString("N"));
            var frameDirectory = Path.Combine(attemptDirectory, "frames");
            var outputPath = Path.Combine(attemptDirectory, "result.mp4");
            Directory.CreateDirectory(frameDirectory);
            try
            {
                var selected = Resample(source, shutterUtc, durationSeconds);
                for (var i = 0; i < selected.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    File.WriteAllBytes(Path.Combine(frameDirectory, i.ToString("D3") + ".jpg"), selected[i].ImageData);
                }
                var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PhotoBooth.Admin.UI.exe");
                if (!File.Exists(helper)) throw new FileNotFoundException("The isolated Video encoder is unavailable.", helper);
                var start = new ProcessStartInfo
                {
                    FileName = helper,
                    Arguments = "--video-encode " + Quote(stillImagePath) + " " + Quote(frameDirectory) + " " + Quote(outputPath) + (flipHorizontally ? " 1 " : " 0 ") + rotationDegrees,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.EnvironmentVariables["PHOTOBOOTH_ENCODER_PARENT_PID"] = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("The isolated Video encoder could not start.");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        if (token.IsCancellationRequested || elapsed.Elapsed > TimeSpan.FromMinutes(2))
                        {
                            try { process.Kill(); } catch { }
                            token.ThrowIfCancellationRequested();
                            throw new TimeoutException("Video encoding exceeded two minutes.");
                        }
                    }
                    LogChildPeak("Video capture encoder", process);
                    if (process.ExitCode != 0) throw new InvalidOperationException("Video encoder failed (" + process.ExitCode + "): " + stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
                }
                if (!IsValidMp4(outputPath)) throw new InvalidDataException("The encoder output is not a valid MP4 video.");
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(outputPath, destinationPath);
            }
            finally
            {
                try { if (Directory.Exists(attemptDirectory)) Directory.Delete(attemptDirectory, true); } catch { }
            }
        }

        public static int RunEncoderCommand(string[] args)
        {
            try
            {
                if (args != null && args.Length > 0 && string.Equals(args[0], "--video-compose", StringComparison.Ordinal))
                    return RunCompositeCommand(args);
                if (args == null || args.Length < 4 || args.Length > 6) throw new ArgumentException("Expected still image, frame directory, output path, optional flip flag and rotation.");
                var files = Directory.GetFiles(args[2], "*.jpg").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (files.Length < FramesPerSecond || files.Length > FramesPerSecond * MaximumDurationSeconds) throw new InvalidDataException("The MP4 encoder received an unsupported frame count.");
                var selected = new List<BufferedFrame>(files.Length);
                for (var i = 0; i < files.Length; i++) selected.Add(new BufferedFrame(DateTime.UtcNow.AddTicks(i), File.ReadAllBytes(files[i])));
                var videoPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3])), Guid.NewGuid().ToString("N") + ".mp4");
                try
                {
                    var rotation = args.Length == 6 ? NormalizeRotation(int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture)) : 0;
                    EncodeVideo(videoPath, selected, args.Length >= 5 && args[4] == "1", rotation, CancellationToken.None);
                    if (!IsValidMp4(videoPath)) throw new InvalidDataException("MP4 video validation failed.");
                    if (File.Exists(args[3])) File.Delete(args[3]);
                    File.Move(videoPath, args[3]);
                    return 0;
                }
                finally { try { if (File.Exists(videoPath)) File.Delete(videoPath); } catch { } }
            }
            catch (Exception exception)
            {
                try { Console.Error.WriteLine(exception); } catch { }
                return 1;
            }
        }

        public static void StartEncoderParentWatchdog()
        {
            int parentId;
            if (!int.TryParse(Environment.GetEnvironmentVariable("PHOTOBOOTH_ENCODER_PARENT_PID"), out parentId) || parentId <= 0) return;
            var watcher = new Thread(() =>
            {
                try
                {
                    using (var parent = Process.GetProcessById(parentId)) parent.WaitForExit();
                }
                catch { }
                try { Process.GetCurrentProcess().Kill(); } catch { }
            }) { IsBackground = true, Name = "PhotoBooth encoder parent watchdog" };
            watcher.Start();
        }

        static int RunCompositeCommand(string[] args)
        {
            if (args.Length != 5) throw new ArgumentException("Expected still composite, frame overlay, slot manifest and output path.");
            var specs = File.ReadAllLines(args[3]).Where(x => !string.IsNullOrWhiteSpace(x)).Select(ParseSlotSpec).OrderBy(x => x.Index).ToList();
            if (specs.Count == 0) throw new InvalidDataException("The MP4 composition has no slots.");
            var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[4])), Guid.NewGuid().ToString("N") + ".mp4");
            var extracted = new List<string>();
            var readers = new List<VideoFileReader>();
            var readerIndexes = new List<int>();
            try
            {
                var sources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var spec in specs)
                {
                    int readerIndex;
                    if (sources.TryGetValue(Path.GetFullPath(spec.Path), out readerIndex))
                    {
                        readerIndexes.Add(readerIndex);
                        continue;
                    }
                    var mp4 = Path.Combine(Path.GetDirectoryName(temp), Guid.NewGuid().ToString("N") + ".source.mp4");
                    CopyVideo(spec.Path, mp4); extracted.Add(mp4);
                    var reader = new VideoFileReader(); reader.Open(mp4); readers.Add(reader);
                    readerIndex = readers.Count - 1;
                    sources.Add(Path.GetFullPath(spec.Path), readerIndex);
                    readerIndexes.Add(readerIndex);
                }
                var rendered = RenderCompositeFrames(args[2], specs, readers, readerIndexes);
                foreach (var reader in readers) reader.Close();
                readers.Clear();
                EncodeVideo(temp, rendered, false, 0, CancellationToken.None);
                if (!IsValidMp4(temp)) throw new InvalidDataException("Composed MP4 video validation failed.");
                if (File.Exists(args[4])) File.Delete(args[4]);
                File.Move(temp, args[4]);
                return 0;
            }
            finally
            {
                foreach (var reader in readers) try { reader.Close(); } catch { }
                foreach (var path in extracted.Concat(new[] { temp })) try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        static SlotSpec ParseSlotSpec(string line)
        {
            var parts = line.Split(new[] { '\t' }, 9);
            if (parts.Length != 9) throw new InvalidDataException("Invalid Video slot manifest.");
            var culture=System.Globalization.CultureInfo.InvariantCulture;
            return new SlotSpec(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]),
                double.Parse(parts[5],culture),double.Parse(parts[6],culture),double.Parse(parts[7],culture),parts[8]);
        }

        static void CopyVideo(string videoPath, string destination)
        {
            if (!IsValidMp4(videoPath)) throw new InvalidDataException("The assigned MP4 video is invalid: " + videoPath);
            File.Copy(videoPath, destination, false);
        }

        static List<BufferedFrame> RenderCompositeFrames(string overlayPath, IReadOnlyList<SlotSpec> specs, IReadOnlyList<VideoFileReader> readers, IReadOnlyList<int> readerIndexes)
        {
            using (var overlay = new Bitmap(overlayPath))
            {
                const int max = 640;
                var scale = Math.Min(1d, (double)max / Math.Max(overlay.Width, overlay.Height));
                var width = AlignVideoDimension((int)Math.Round(overlay.Width * scale));
                var height = AlignVideoDimension((int)Math.Round(overlay.Height * scale));
                var scaleX = (double)width / overlay.Width;
                var scaleY = (double)height / overlay.Height;
                var last = new Bitmap[readers.Count];
                var frameCount = Math.Max(1, Math.Min(FramesPerSecond * MaximumDurationSeconds, readers.Min(x => (int)x.FrameCount)));
                var rendered = new List<BufferedFrame>(frameCount);
                try
                {
                    for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    using (var canvas = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        for (var readerIndex = 0; readerIndex < readers.Count; readerIndex++)
                        {
                            var next = readers[readerIndex].ReadVideoFrame();
                            if (next != null) { last[readerIndex]?.Dispose(); last[readerIndex] = next; }
                        }
                        using (var graphics = Graphics.FromImage(canvas))
                        {
                            graphics.Clear(Color.White); graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            for (var i = 0; i < specs.Count; i++)
                            {
                                var frame = last[readerIndexes[i]];
                                if (frame != null) DrawCrop(graphics, frame, specs[i], scaleX, scaleY);
                            }
                            graphics.DrawImage(overlay, new Rectangle(0, 0, width, height));
                        }
                        using (var stream = new MemoryStream())
                        {
                            canvas.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
                            rendered.Add(new BufferedFrame(DateTime.UtcNow.AddTicks(frameIndex), stream.ToArray()));
                        }
                    }
                }
                finally { foreach (var bitmap in last) bitmap?.Dispose(); }
                return rendered;
            }
        }

        static void DrawCrop(Graphics graphics, Image image, SlotSpec slot, double scaleX, double scaleY)
        {
            var target = slot.Width * scaleX / (slot.Height * scaleY); var source = (double)image.Width / image.Height;
            double coverWidth,coverHeight;
            if (source > target) { coverWidth=image.Height*target;coverHeight=image.Height; }
            else { coverWidth=image.Width;coverHeight=image.Width/target; }
            var zoom=MediaTransformGeometry.Clamp(slot.Zoom,1,2);var cropWidth=coverWidth/zoom;var cropHeight=coverHeight/zoom;
            var centerX=MediaTransformGeometry.Clamp(slot.CenterX,cropWidth/(2*image.Width),1-cropWidth/(2*image.Width));
            var centerY=MediaTransformGeometry.Clamp(slot.CenterY,cropHeight/(2*image.Height),1-cropHeight/(2*image.Height));
            var crop=new RectangleF((float)(centerX*image.Width-cropWidth/2),(float)(centerY*image.Height-cropHeight/2),(float)cropWidth,(float)cropHeight);
            graphics.DrawImage(image, new Rectangle((int)Math.Round(slot.X * scaleX), (int)Math.Round(slot.Y * scaleY), Math.Max(1, (int)Math.Round(slot.Width * scaleX)), Math.Max(1, (int)Math.Round(slot.Height * scaleY))), crop, GraphicsUnit.Pixel);
        }

        static int AlignVideoDimension(int value)
        {
            value = Math.Max(4, value);
            return value - value % 4;
        }

        void RunHelper(string arguments, CancellationToken token)
        {
            var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PhotoBooth.Admin.UI.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("The isolated Video encoder is unavailable.", helper);
            var start = new ProcessStartInfo { FileName = helper, Arguments = arguments, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["PHOTOBOOTH_ENCODER_PARENT_PID"] = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("The isolated Video encoder could not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); var elapsed = Stopwatch.StartNew();
                while (!process.WaitForExit(100)) { if (token.IsCancellationRequested || elapsed.Elapsed > TimeSpan.FromMinutes(3)) { try { process.Kill(); } catch { } token.ThrowIfCancellationRequested(); throw new TimeoutException("Video composition exceeded three minutes."); } }
                LogChildPeak("Composite video encoder", process);
                if (process.ExitCode != 0) throw new InvalidOperationException("Video encoder failed (" + process.ExitCode + "): " + stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
            }
        }

        void LogMemory(string stage, int itemCount, long bufferedBytes)
        {
            if (log == null) return;
            try
            {
                using (var process = Process.GetCurrentProcess())
                    log.LogInformation("{Stage}: Items={ItemCount}, Buffer={BufferMb:F1} MB, WorkingSet={WorkingSetMb:F1} MB, Private={PrivateMb:F1} MB, Managed={ManagedMb:F1} MB",
                        stage, itemCount, bufferedBytes / 1048576d, process.WorkingSet64 / 1048576d, process.PrivateMemorySize64 / 1048576d, GC.GetTotalMemory(false) / 1048576d);
            }
            catch { }
        }

        void LogChildPeak(string stage, Process process)
        {
            if (log == null || process == null) return;
            try
            {
                process.Refresh();
                log.LogInformation("{Stage} completed: ChildPeakWorkingSet={PeakMb:F1} MB", stage, process.PeakWorkingSet64 / 1048576d);
                LogMemory(stage + " parent after completion", 0, BufferedBytes);
            }
            catch { }
        }

        static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        internal static bool IsValidMp4(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || new FileInfo(path).Length < 12) return false;
            var header = new byte[12];
            using (var stream = File.OpenRead(path)) if (stream.Read(header, 0, header.Length) != header.Length) return false;
            return header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';
        }

        static void ValidateSource(string stillImagePath, DateTime shutterUtc, int durationSeconds, BufferedFrame[] source)
        {
            if (!File.Exists(stillImagePath)) throw new FileNotFoundException("The captured still image is unavailable.", stillImagePath);
            var preroll = TimeSpan.FromSeconds(durationSeconds);
            if (source.Length < FramesPerSecond || source[0].TimestampUtc > shutterUtc - preroll + TimeSpan.FromMilliseconds(250))
                throw new InvalidOperationException("MP4 video requires a complete " + durationSeconds + "-second live-view buffer.");
        }

        static void Create(string stillImagePath, string destinationPath, DateTime shutterUtc, int durationSeconds, BufferedFrame[] source, bool flipHorizontally, int rotationDegrees, CancellationToken token)
        {
            ValidateSource(stillImagePath, shutterUtc, durationSeconds, source);

            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            Directory.CreateDirectory(directory);
            var attemptId = Guid.NewGuid().ToString("N");
            var videoPath = Path.Combine(directory, attemptId + ".video.mp4");
            var outputPath = Path.Combine(directory, attemptId + ".video.tmp");
            try
            {
                var selected = Resample(source, shutterUtc, durationSeconds);
                EncodeVideo(videoPath, selected, flipHorizontally, rotationDegrees, token);
                if (!IsValidMp4(videoPath)) throw new InvalidDataException("MP4 video output validation failed.");
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(videoPath, outputPath);
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(outputPath, destinationPath);
            }
            finally
            {
                try { if (File.Exists(videoPath)) File.Delete(videoPath); } catch { }
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
        }

        static List<BufferedFrame> Resample(BufferedFrame[] source, DateTime shutterUtc, int durationSeconds)
        {
            durationSeconds = NormalizeDuration(durationSeconds);
            var frameCount = FramesPerSecond * durationSeconds;
            var result = new List<BufferedFrame>(frameCount);
            var start = shutterUtc - TimeSpan.FromSeconds(durationSeconds);
            var sourceIndex = 0;
            for (var index = 0; index < frameCount; index++)
            {
                var target = start + TimeSpan.FromTicks(FrameInterval.Ticks * index);
                while (sourceIndex + 1 < source.Length &&
                       Math.Abs((source[sourceIndex + 1].TimestampUtc - target).Ticks) <= Math.Abs((source[sourceIndex].TimestampUtc - target).Ticks))
                    sourceIndex++;
                result.Add(source[sourceIndex]);
            }
            return result;
        }

        static int NormalizeDuration(int durationSeconds) => Math.Max(1, Math.Min(MaximumDurationSeconds, durationSeconds));

        static int NormalizeRotation(int value) => value == 90 || value == -90 || value == 180 ? value : 0;

        static void EncodeVideo(string path, IReadOnlyList<BufferedFrame> selected, bool flipHorizontally, int rotationDegrees, CancellationToken token)
        {
            using (var firstStream = new MemoryStream(selected[0].ImageData, false))
            using (var first = new Bitmap(firstStream))
            using (var writer = new VideoFileWriter())
            {
                // The legacy Accord FFmpeg runtime bundled with the desktop app
                // crashes in native code at common live-view sizes such as
                // 1280x720. Keep the primary JPEG untouched, but normalize the
                // appended preview video to a safe, aspect-preserving size.
                const int maximumVideoDimension = 640;
                var quarterTurn = rotationDegrees == 90 || rotationDegrees == -90;
                var sourceWidth = quarterTurn ? first.Height : first.Width;
                var sourceHeight = quarterTurn ? first.Width : first.Height;
                var scale = Math.Min(1d, (double)maximumVideoDimension / Math.Max(sourceWidth, sourceHeight));
                var width = AlignVideoDimension((int)Math.Round(sourceWidth * scale));
                var height = AlignVideoDimension((int)Math.Round(sourceHeight * scale));
                writer.Open(path, width, height, FramesPerSecond, VideoCodec.H264, 2500000);
                foreach (var frame in selected)
                {
                    token.ThrowIfCancellationRequested();
                    using (var stream = new MemoryStream(frame.ImageData, false))
                    using (var original = new Bitmap(stream))
                    using (var encoded = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        if (flipHorizontally) original.RotateFlip(RotateFlipType.RotateNoneFlipX);
                        if (rotationDegrees == 90) original.RotateFlip(RotateFlipType.Rotate90FlipNone);
                        else if (rotationDegrees == -90) original.RotateFlip(RotateFlipType.Rotate270FlipNone);
                        else if (rotationDegrees == 180) original.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        using (var graphics = Graphics.FromImage(encoded)) graphics.DrawImage(original, 0, 0, width, height);
                        writer.WriteVideoFrame(encoded);
                    }
                }
            }
        }

        sealed class BufferedFrame
        {
            public BufferedFrame(DateTime timestampUtc, byte[] imageData) { TimestampUtc = timestampUtc; ImageData = imageData; }
            public DateTime TimestampUtc { get; }
            public byte[] ImageData { get; }
        }

        sealed class SlotSpec
        {
            public SlotSpec(int index, int x, int y, int width, int height, double zoom, double centerX, double centerY, string path) { Index = index; X = x; Y = y; Width = width; Height = height; Zoom=zoom;CenterX=centerX;CenterY=centerY;Path = path; }
            public int Index { get; }
            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }
            public double Zoom { get; }
            public double CenterX { get; }
            public double CenterY { get; }
            public string Path { get; }
        }
    }
}
