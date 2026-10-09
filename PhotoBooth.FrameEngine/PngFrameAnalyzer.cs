using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.FrameEngine
{
    public sealed class PngFrameAnalyzer : IFrameAnalyzer
    {
        public Frame Analyze(string pngPath, FrameAnalysisOptions options)
        {
            using (var stream = File.OpenRead(pngPath)) return Analyze(stream, pngPath, options);
        }

        public Frame Analyze(Stream pngStream, string sourceName, FrameAnalysisOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            using (var bitmap = new Bitmap(pngStream))
            {
                if ((bitmap.PixelFormat & System.Drawing.Imaging.PixelFormat.Alpha) == 0)
                    throw new InvalidDataException("Frame must be a PNG image with an alpha channel.");
                var slots = Detect(bitmap, options);
                return new Frame { Id = Guid.NewGuid(), Name = Path.GetFileNameWithoutExtension(sourceName), SourcePath = sourceName, PixelWidth = bitmap.Width, PixelHeight = bitmap.Height, Slots = slots };
            }
        }

        private static IReadOnlyList<FrameSlot> Detect(Bitmap bitmap, FrameAnalysisOptions options)
        {
            using (var normalized = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(normalized))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(bitmap, 0, 0);
                }
                var rectangle = new Rectangle(0, 0, normalized.Width, normalized.Height);
                var data = normalized.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var stride = Math.Abs(data.Stride);
                    var pixels = new byte[stride * normalized.Height];
                    for (var y = 0; y < normalized.Height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
                    return Detect(pixels, normalized.Width, normalized.Height, stride, options);
                }
                finally { normalized.UnlockBits(data); }
            }
        }

        private static IReadOnlyList<FrameSlot> Detect(byte[] pixels, int width, int height, int stride, FrameAnalysisOptions options)
        {
            var visited = new bool[width * height];
            var queue = new int[width * height];
            var regions = new List<Region>();
            for (var pixel = 0; pixel < visited.Length; pixel++)
            {
                if (visited[pixel] || AlphaAt(pixels, pixel, width, stride) > options.AlphaThreshold) continue;
                regions.Add(FloodFill(pixels, pixel, width, height, stride, visited, queue, options.AlphaThreshold));
            }
            var maximum = Math.Max(0, Math.Min(8, options.MaximumSlots));
            return regions.Where(r => r.Area >= options.MinimumArea && r.Width >= options.MinimumWidth && r.Height >= options.MinimumHeight)
                .Where(r => !options.IgnoreBorderConnectedRegions || !r.TouchesBorder)
                .OrderByDescending(r => r.Area).Take(maximum)
                .OrderBy(r => r.Y).ThenBy(r => r.X)
                .Select((r, index) => new FrameSlot { Id = Guid.NewGuid(), Index = index, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height }).ToList();
        }

        private static Region FloodFill(byte[] pixels, int start, int width, int height, int stride, bool[] visited, int[] queue, byte threshold)
        {
            var startX = start % width; var startY = start / width;
            var minX = startX; var maxX = startX; var minY = startY; var maxY = startY; var area = 0; var border = false;
            var head = 0; var tail = 0; queue[tail++] = start; visited[start] = true;
            while (head < tail)
            {
                var current = queue[head++]; var x = current % width; var y = current / width;
                area++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                border |= x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (x > 0) Visit(current - 1, pixels, width, stride, visited, queue, ref tail, threshold);
                if (x + 1 < width) Visit(current + 1, pixels, width, stride, visited, queue, ref tail, threshold);
                if (y > 0) Visit(current - width, pixels, width, stride, visited, queue, ref tail, threshold);
                if (y + 1 < height) Visit(current + width, pixels, width, stride, visited, queue, ref tail, threshold);
            }
            return new Region { X = minX, Y = minY, Width = maxX - minX + 1, Height = maxY - minY + 1, Area = area, TouchesBorder = border };
        }

        private static void Visit(int pixel, byte[] pixels, int width, int stride, bool[] visited, int[] queue, ref int tail, byte threshold)
        {
            if (visited[pixel] || AlphaAt(pixels, pixel, width, stride) > threshold) return;
            visited[pixel] = true;
            queue[tail++] = pixel;
        }

        private static byte AlphaAt(byte[] pixels, int pixel, int width, int stride)
        {
            var x = pixel % width; var y = pixel / width;
            return pixels[y * stride + x * 4 + 3];
        }

        private sealed class Region { public int X; public int Y; public int Width; public int Height; public int Area; public bool TouchesBorder; }
    }
}
