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
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class MotionPhotoService : IMotionPhotoService
    {
        internal const int FramesPerSecond = 18;
        internal static readonly TimeSpan Preroll = TimeSpan.FromSeconds(3);
        static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / FramesPerSecond);
        readonly object sync = new object();
        readonly List<BufferedFrame> frames = new List<BufferedFrame>();
        readonly bool nativeEncoderEnabled;
        readonly bool isolateNativeEncoder;
        DateTime lastAcceptedUtc;

        // Kept for in-process encoder tests. Production DI isolates native
        // FFmpeg work in a child process so a native crash cannot kill the UI.
        public MotionPhotoService() : this(true, false) { }
        public MotionPhotoService(ApplicationOptions options) : this(
            options != null && options.Features.TryGetValue("MotionPhoto", out var moduleEnabled) && moduleEnabled &&
            options.Features.TryGetValue("MotionPhotoNativeEncoder", out var encoderEnabled) && encoderEnabled, true) { }
        MotionPhotoService(bool nativeEncoderEnabled, bool isolateNativeEncoder) { this.nativeEncoderEnabled = nativeEncoderEnabled; this.isolateNativeEncoder = isolateNativeEncoder; }

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
                var cutoff = timestampUtc - Preroll - TimeSpan.FromSeconds(1);
                frames.RemoveAll(x => x.TimestampUtc < cutoff);
            }
        }

        public Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(stillImagePath)) throw new ArgumentException("Still image path is required.", nameof(stillImagePath));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));
            if (!nativeEncoderEnabled)
            {
                throw new InvalidOperationException("Motion Photo encoding is disabled. A static JPEG will not be saved with an _MP filename.");
            }
            shutterTimestampUtc = shutterTimestampUtc.Kind == DateTimeKind.Utc ? shutterTimestampUtc : shutterTimestampUtc.ToUniversalTime();
            BufferedFrame[] snapshot;
            lock (sync)
                snapshot = frames.Where(x => x.TimestampUtc > shutterTimestampUtc - Preroll - FrameInterval && x.TimestampUtc <= shutterTimestampUtc).ToArray();
            return Task.Run(() =>
            {
                if (isolateNativeEncoder)
                    CreateExternal(stillImagePath, destinationPath, shutterTimestampUtc, snapshot, token);
                else
                    Create(stillImagePath, destinationPath, shutterTimestampUtc, snapshot, token);
            }, token);
        }

        public Task ComposeAsync(string stillCompositePath, Frame frame, IReadOnlyDictionary<int, string> slotAssignments, string destinationPath, CancellationToken token)
        {
            if (!nativeEncoderEnabled) throw new InvalidOperationException("Motion Photo encoding is disabled.");
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (slotAssignments == null || slotAssignments.Count == 0) throw new ArgumentException("Motion Photo slot assignments are required.", nameof(slotAssignments));
            return Task.Run(() => ComposeExternal(stillCompositePath, frame, slotAssignments, destinationPath, token), token);
        }

        public Task<string> CreatePreviewVideoAsync(string motionPhotoPath, string previewDirectory, CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(previewDirectory);
                var destination = Path.Combine(previewDirectory, Path.GetFileNameWithoutExtension(motionPhotoPath) + ".preview.mp4");
                if (File.Exists(destination)) File.Delete(destination);
                ExtractEmbeddedVideo(motionPhotoPath, destination);
                return destination;
            }, token);
        }

        static void ComposeExternal(string stillPath, Frame frame, IReadOnlyDictionary<int, string> assignments, string destinationPath, CancellationToken token)
        {
            if (!File.Exists(stillPath)) throw new FileNotFoundException("The still composite is unavailable.", stillPath);
            if (!File.Exists(frame.SourcePath)) throw new FileNotFoundException("The frame overlay is unavailable.", frame.SourcePath);
            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            Directory.CreateDirectory(directory);
            var attempt = Path.Combine(directory, ".motion-composite-" + Guid.NewGuid().ToString("N"));
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
                        if (!assignments.TryGetValue(slot.Index, out source) || !File.Exists(source)) throw new FileNotFoundException("A Motion Photo assigned to slot " + slot.Index + " is unavailable.", source);
                        writer.WriteLine(slot.Index + "\t" + slot.X + "\t" + slot.Y + "\t" + slot.Width + "\t" + slot.Height + "\t" + source);
                    }
                var output = Path.Combine(attempt, "result_MP.jpg");
                RunHelper("--motion-photo-compose " + Quote(primaryJpeg) + " " + Quote(frame.SourcePath) + " " + Quote(manifest) + " " + Quote(output), token);
                if (!IsValidMotionPhoto(output)) throw new InvalidDataException("The composed Motion Photo is invalid.");
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(output, destinationPath);
            }
            finally { try { if (Directory.Exists(attempt)) Directory.Delete(attempt, true); } catch { } }
        }

        static void CreateExternal(string stillImagePath, string destinationPath, DateTime shutterUtc, BufferedFrame[] source, CancellationToken token)
        {
            ValidateSource(stillImagePath, shutterUtc, source);
            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            Directory.CreateDirectory(directory);
            var attemptDirectory = Path.Combine(directory, ".motion-" + Guid.NewGuid().ToString("N"));
            var frameDirectory = Path.Combine(attemptDirectory, "frames");
            var outputPath = Path.Combine(attemptDirectory, "result_MP.jpg");
            Directory.CreateDirectory(frameDirectory);
            try
            {
                var selected = Resample(source, shutterUtc);
                for (var i = 0; i < selected.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    File.WriteAllBytes(Path.Combine(frameDirectory, i.ToString("D3") + ".jpg"), selected[i].ImageData);
                }
                var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PhotoBooth.Admin.UI.exe");
                if (!File.Exists(helper)) throw new FileNotFoundException("The isolated Motion Photo encoder is unavailable.", helper);
                var start = new ProcessStartInfo
                {
                    FileName = helper,
                    Arguments = "--motion-photo-encode " + Quote(stillImagePath) + " " + Quote(frameDirectory) + " " + Quote(outputPath),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("The isolated Motion Photo encoder could not start.");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        if (token.IsCancellationRequested || elapsed.Elapsed > TimeSpan.FromMinutes(2))
                        {
                            try { process.Kill(); } catch { }
                            token.ThrowIfCancellationRequested();
                            throw new TimeoutException("Motion Photo encoding exceeded two minutes.");
                        }
                    }
                    if (process.ExitCode != 0) throw new InvalidOperationException("Motion Photo encoder failed (" + process.ExitCode + "): " + stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
                }
                if (!IsValidMotionPhoto(outputPath)) throw new InvalidDataException("The encoder output does not contain valid Motion Photo XMP and MP4 data.");
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
                if (args != null && args.Length > 0 && string.Equals(args[0], "--motion-photo-compose", StringComparison.Ordinal))
                    return RunCompositeCommand(args);
                if (args == null || args.Length != 4) throw new ArgumentException("Expected still image, frame directory and output path.");
                var files = Directory.GetFiles(args[2], "*.jpg").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (files.Length != FramesPerSecond * 3) throw new InvalidDataException("The Motion Photo encoder requires exactly 54 frames.");
                var selected = new List<BufferedFrame>(files.Length);
                for (var i = 0; i < files.Length; i++) selected.Add(new BufferedFrame(DateTime.UtcNow.AddTicks(i), File.ReadAllBytes(files[i])));
                var videoPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3])), Guid.NewGuid().ToString("N") + ".mp4");
                try
                {
                    EncodeVideo(videoPath, selected, CancellationToken.None);
                    var videoLength = new FileInfo(videoPath).Length;
                    if (videoLength <= 0) throw new IOException("The video encoder produced an empty file.");
                    WriteMotionPhoto(args[1], videoPath, args[3], videoLength, (selected.Count - 1L) * 1000000L / FramesPerSecond, CancellationToken.None);
                    if (!IsValidMotionPhoto(args[3])) throw new InvalidDataException("Motion Photo validation failed.");
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

        static int RunCompositeCommand(string[] args)
        {
            if (args.Length != 5) throw new ArgumentException("Expected still composite, frame overlay, slot manifest and output path.");
            var specs = File.ReadAllLines(args[3]).Where(x => !string.IsNullOrWhiteSpace(x)).Select(ParseSlotSpec).OrderBy(x => x.Index).ToList();
            if (specs.Count == 0) throw new InvalidDataException("The Motion Photo composition has no slots.");
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
                    ExtractEmbeddedVideo(spec.Path, mp4); extracted.Add(mp4);
                    var reader = new VideoFileReader(); reader.Open(mp4); readers.Add(reader);
                    readerIndex = readers.Count - 1;
                    sources.Add(Path.GetFullPath(spec.Path), readerIndex);
                    readerIndexes.Add(readerIndex);
                }
                var rendered = RenderCompositeFrames(args[2], specs, readers, readerIndexes);
                foreach (var reader in readers) reader.Close();
                readers.Clear();
                EncodeVideo(temp, rendered, CancellationToken.None);
                var length = new FileInfo(temp).Length;
                WriteMotionPhoto(args[1], temp, args[4], length, 53L * 1000000L / FramesPerSecond, CancellationToken.None);
                if (!IsValidMotionPhoto(args[4])) throw new InvalidDataException("Composed Motion Photo validation failed.");
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
            var parts = line.Split(new[] { '\t' }, 6);
            if (parts.Length != 6) throw new InvalidDataException("Invalid Motion Photo slot manifest.");
            return new SlotSpec(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]), parts[5]);
        }

        static void ExtractEmbeddedVideo(string motionPhoto, string destination)
        {
            if (!IsValidMotionPhoto(motionPhoto)) throw new InvalidDataException("The assigned file is not a valid Motion Photo: " + motionPhoto);
            var bytes = File.ReadAllBytes(motionPhoto);
            var text = Encoding.UTF8.GetString(bytes);
            const string marker = "Item:Semantic=\"MotionPhoto\" Item:Length=\"";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("Motion Photo video length is missing.");
            start += marker.Length; var end = text.IndexOf('"', start);
            long length;
            if (end < 0 || !long.TryParse(text.Substring(start, end - start), out length) || length <= 0 || length > bytes.LongLength) throw new InvalidDataException("Motion Photo video length is invalid.");
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) output.Write(bytes, bytes.Length - (int)length, (int)length);
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
                var rendered = new List<BufferedFrame>(FramesPerSecond * 3);
                try
                {
                    for (var frameIndex = 0; frameIndex < FramesPerSecond * 3; frameIndex++)
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
            RectangleF crop;
            if (source > target) { var w = (float)(image.Height * target); crop = new RectangleF((image.Width - w) / 2, 0, w, image.Height); }
            else { var h = (float)(image.Width / target); crop = new RectangleF(0, (image.Height - h) / 2, image.Width, h); }
            graphics.DrawImage(image, new Rectangle((int)Math.Round(slot.X * scaleX), (int)Math.Round(slot.Y * scaleY), Math.Max(1, (int)Math.Round(slot.Width * scaleX)), Math.Max(1, (int)Math.Round(slot.Height * scaleY))), crop, GraphicsUnit.Pixel);
        }

        static int AlignVideoDimension(int value)
        {
            value = Math.Max(4, value);
            return value - value % 4;
        }

        static void RunHelper(string arguments, CancellationToken token)
        {
            var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PhotoBooth.Admin.UI.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("The isolated Motion Photo encoder is unavailable.", helper);
            var start = new ProcessStartInfo { FileName = helper, Arguments = arguments, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("The isolated Motion Photo encoder could not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); var elapsed = Stopwatch.StartNew();
                while (!process.WaitForExit(100)) { if (token.IsCancellationRequested || elapsed.Elapsed > TimeSpan.FromMinutes(3)) { try { process.Kill(); } catch { } token.ThrowIfCancellationRequested(); throw new TimeoutException("Motion Photo composition exceeded three minutes."); } }
                if (process.ExitCode != 0) throw new InvalidOperationException("Motion Photo encoder failed (" + process.ExitCode + "): " + stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
            }
        }

        static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        internal static bool IsValidMotionPhoto(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var value = Encoding.ASCII.GetString(File.ReadAllBytes(path));
            return value.IndexOf("Camera:MotionPhoto=\"1\"", StringComparison.Ordinal) >= 0 && value.IndexOf("Item:Semantic=\"MotionPhoto\"", StringComparison.Ordinal) >= 0 && value.IndexOf("ftyp", StringComparison.Ordinal) >= 0;
        }

        static void ValidateSource(string stillImagePath, DateTime shutterUtc, BufferedFrame[] source)
        {
            if (!File.Exists(stillImagePath)) throw new FileNotFoundException("The captured still image is unavailable.", stillImagePath);
            if (source.Length < FramesPerSecond || source[0].TimestampUtc > shutterUtc - Preroll + TimeSpan.FromMilliseconds(250))
                throw new InvalidOperationException("Motion Photo requires a complete three-second live-view buffer.");
        }

        static void Create(string stillImagePath, string destinationPath, DateTime shutterUtc, BufferedFrame[] source, CancellationToken token)
        {
            ValidateSource(stillImagePath, shutterUtc, source);

            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            Directory.CreateDirectory(directory);
            var attemptId = Guid.NewGuid().ToString("N");
            var videoPath = Path.Combine(directory, attemptId + ".motion.mp4");
            var outputPath = Path.Combine(directory, attemptId + ".motion.tmp");
            try
            {
                var selected = Resample(source, shutterUtc);
                EncodeVideo(videoPath, selected, token);
                var videoLength = new FileInfo(videoPath).Length;
                if (videoLength <= 0) throw new IOException("Motion Photo video encoder produced an empty file.");
                var presentationUs = (selected.Count - 1L) * 1000000L / FramesPerSecond;
                WriteMotionPhoto(stillImagePath, videoPath, outputPath, videoLength, presentationUs, token);
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(outputPath, destinationPath);
            }
            finally
            {
                try { if (File.Exists(videoPath)) File.Delete(videoPath); } catch { }
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
        }

        static List<BufferedFrame> Resample(BufferedFrame[] source, DateTime shutterUtc)
        {
            var result = new List<BufferedFrame>(FramesPerSecond * 3);
            var start = shutterUtc - Preroll;
            for (var index = 0; index < FramesPerSecond * 3; index++)
            {
                var target = start + TimeSpan.FromTicks(FrameInterval.Ticks * index);
                result.Add(source.OrderBy(x => Math.Abs((x.TimestampUtc - target).Ticks)).First());
            }
            return result;
        }

        static void EncodeVideo(string path, IReadOnlyList<BufferedFrame> selected, CancellationToken token)
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
                var scale = Math.Min(1d, (double)maximumVideoDimension / Math.Max(first.Width, first.Height));
                var width = AlignVideoDimension((int)Math.Round(first.Width * scale));
                var height = AlignVideoDimension((int)Math.Round(first.Height * scale));
                writer.Open(path, width, height, FramesPerSecond, VideoCodec.H264, 2500000);
                foreach (var frame in selected)
                {
                    token.ThrowIfCancellationRequested();
                    using (var stream = new MemoryStream(frame.ImageData, false))
                    using (var original = new Bitmap(stream))
                    using (var encoded = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (var graphics = Graphics.FromImage(encoded)) graphics.DrawImage(original, 0, 0, width, height);
                        writer.WriteVideoFrame(encoded);
                    }
                }
            }
        }

        static void WriteMotionPhoto(string stillPath, string videoPath, string outputPath, long videoLength, long presentationUs, CancellationToken token)
        {
            var xmp = BuildXmp(videoLength, presentationUs);
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteJpegWithXmp(stillPath, output, xmp, token);
                using (var video = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read)) video.CopyTo(output);
                output.Flush(true);
            }
        }

        internal static string BuildXmp(long videoLength, long presentationUs)
        {
            return "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description " +
                "xmlns:Camera=\"http://ns.google.com/photos/1.0/camera/\" xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" " +
                "Camera:MotionPhoto=\"1\" Camera:MotionPhotoVersion=\"1\" Camera:MotionPhotoPresentationTimestampUs=\"" + presentationUs + "\">" +
                "<Container:Directory><rdf:Seq>" +
                "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\"/></rdf:li>" +
                "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"" + videoLength + "\"/></rdf:li>" +
                "</rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        }

        static void WriteJpegWithXmp(string stillPath, Stream output, string xmp, CancellationToken token)
        {
            var jpeg = File.ReadAllBytes(stillPath);
            if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8) throw new InvalidDataException("The primary Motion Photo image must be JPEG.");
            var header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
            var xml = Encoding.UTF8.GetBytes(xmp);
            var payloadLength = header.Length + xml.Length;
            if (payloadLength + 2 > ushort.MaxValue) throw new InvalidDataException("Motion Photo XMP is too large for a JPEG APP1 segment.");
            output.WriteByte(0xff); output.WriteByte(0xd8); output.WriteByte(0xff); output.WriteByte(0xe1);
            var segmentLength = payloadLength + 2;
            output.WriteByte((byte)(segmentLength >> 8)); output.WriteByte((byte)segmentLength);
            output.Write(header, 0, header.Length); output.Write(xml, 0, xml.Length);
            token.ThrowIfCancellationRequested();
            output.Write(jpeg, 2, jpeg.Length - 2);
        }

        sealed class BufferedFrame
        {
            public BufferedFrame(DateTime timestampUtc, byte[] imageData) { TimestampUtc = timestampUtc; ImageData = imageData; }
            public DateTime TimestampUtc { get; }
            public byte[] ImageData { get; }
        }

        sealed class SlotSpec
        {
            public SlotSpec(int index, int x, int y, int width, int height, string path) { Index = index; X = x; Y = y; Width = width; Height = height; Path = path; }
            public int Index { get; }
            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }
            public string Path { get; }
        }
    }
}
