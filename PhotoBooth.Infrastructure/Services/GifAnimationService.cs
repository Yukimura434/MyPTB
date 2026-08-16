using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class GifAnimationService : IGifAnimationService
    {
        public Task<string> CreateAsync(
            IReadOnlyList<string> imagePaths,
            string outputPath,
            int frameDurationMilliseconds,
            CancellationToken token)
        {
            var files = (imagePaths ?? new string[0])
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .ToList();
            if (files.Count == 0)
            {
                throw new InvalidOperationException("No images are available to create the GIF.");
            }

            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var temporaryPath = outputPath + ".tmp";
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

                try
                {
                    var encoder = new GifBitmapEncoder();
                    var delay = (ushort)Math.Max(1, Math.Min(ushort.MaxValue, frameDurationMilliseconds / 10));
                    foreach (var file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        var source = LoadFrame(file);
                        var frame = ScaleFrame(source, 960);
                        var metadata = new BitmapMetadata("gif");
                        metadata.SetQuery("/grctlext/Delay", delay);
                        metadata.SetQuery("/grctlext/Disposal", (byte)2);
                        encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
                    }

                    using (var encoded = new MemoryStream())
                    {
                        encoder.Save(encoded);
                        WriteAnimatedGif(encoded.ToArray(), temporaryPath, delay);
                    }
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Move(temporaryPath, outputPath);
                    return outputPath;
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }, token);
        }

        private static BitmapSource LoadFrame(string path)
        {
            using (var input = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
        }

        private static BitmapSource ScaleFrame(BitmapSource source, int maximumWidth)
        {
            if (source.PixelWidth <= maximumWidth) return source;
            var scale = maximumWidth / (double)source.PixelWidth;
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            return transformed;
        }

        // WPF preserves multiple GIF frames, but its GIF encoder does not preserve
        // frame-level metadata. Add the GIF89a control blocks explicitly so normal
        // browsers and System.Drawing recognize the result as an animation.
        private static void WriteAnimatedGif(byte[] encodedGif, string outputPath, ushort delay)
        {
            if (encodedGif == null || encodedGif.Length < 14 || encodedGif[0] != 'G' ||
                encodedGif[1] != 'I' || encodedGif[2] != 'F')
            {
                throw new InvalidDataException("The GIF encoder returned invalid data.");
            }

            using (var output = File.Create(outputPath))
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
                writer.Write(encodedGif, 6, 7);

                var offset = 13;
                if ((encodedGif[10] & 0x80) != 0)
                {
                    var globalColorTableLength = 3 * (1 << ((encodedGif[10] & 0x07) + 1));
                    EnsureAvailable(encodedGif, offset, globalColorTableLength);
                    writer.Write(encodedGif, offset, globalColorTableLength);
                    offset += globalColorTableLength;
                }

                // NETSCAPE2.0: loop forever.
                writer.Write(new byte[] { 0x21, 0xFF, 0x0B });
                writer.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                writer.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

                while (offset < encodedGif.Length)
                {
                    var marker = encodedGif[offset];
                    if (marker == 0x3B)
                    {
                        writer.Write(marker);
                        break;
                    }

                    if (marker == 0x2C)
                    {
                        // Graphic Control Extension: restore delay and replace the
                        // previous frame before displaying the next one.
                        writer.Write(new byte[]
                        {
                            0x21, 0xF9, 0x04, 0x08,
                            (byte)(delay & 0xFF), (byte)(delay >> 8), 0x00, 0x00
                        });

                        EnsureAvailable(encodedGif, offset, 10);
                        writer.Write(encodedGif, offset, 10);
                        var packed = encodedGif[offset + 9];
                        offset += 10;
                        if ((packed & 0x80) != 0)
                        {
                            var localColorTableLength = 3 * (1 << ((packed & 0x07) + 1));
                            EnsureAvailable(encodedGif, offset, localColorTableLength);
                            writer.Write(encodedGif, offset, localColorTableLength);
                            offset += localColorTableLength;
                        }

                        EnsureAvailable(encodedGif, offset, 1);
                        writer.Write(encodedGif[offset++]); // LZW minimum code size
                        CopySubBlocks(encodedGif, ref offset, writer);
                        continue;
                    }

                    if (marker == 0x21)
                    {
                        EnsureAvailable(encodedGif, offset, 2);
                        var label = encodedGif[offset + 1];
                        if (label == 0xF9)
                        {
                            EnsureAvailable(encodedGif, offset, 8);
                            offset += 8; // Replace any encoder-supplied GCE.
                            continue;
                        }

                        writer.Write(encodedGif, offset, 2);
                        offset += 2;
                        CopySubBlocks(encodedGif, ref offset, writer);
                        continue;
                    }

                    throw new InvalidDataException("Unexpected block in encoded GIF.");
                }
            }
        }

        private static void CopySubBlocks(byte[] source, ref int offset, BinaryWriter writer)
        {
            while (true)
            {
                EnsureAvailable(source, offset, 1);
                var length = source[offset++];
                writer.Write(length);
                if (length == 0) return;
                EnsureAvailable(source, offset, length);
                writer.Write(source, offset, length);
                offset += length;
            }
        }

        private static void EnsureAvailable(byte[] source, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > source.Length - count)
            {
                throw new InvalidDataException("The encoded GIF is truncated.");
            }
        }
    }
}
