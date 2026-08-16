using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
            var visited = new bool[bitmap.Width, bitmap.Height];
            var regions = new List<Region>();
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (visited[x, y] || bitmap.GetPixel(x, y).A > options.AlphaThreshold) continue;
                regions.Add(FloodFill(bitmap, x, y, visited, options.AlphaThreshold));
            }
            var maximum = Math.Max(0, Math.Min(8, options.MaximumSlots));
            return regions.Where(r => r.Area >= options.MinimumArea && r.Width >= options.MinimumWidth && r.Height >= options.MinimumHeight)
                .Where(r => !options.IgnoreBorderConnectedRegions || !r.TouchesBorder)
                .OrderByDescending(r => r.Area).ThenBy(r => r.Y).ThenBy(r => r.X).Take(maximum)
                .Select((r, index) => new FrameSlot { Id = Guid.NewGuid(), Index = index, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height }).ToList();
        }

        private static Region FloodFill(Bitmap bitmap, int startX, int startY, bool[,] visited, byte threshold)
        {
            var queue = new Queue<Point>(); queue.Enqueue(new Point(startX, startY)); visited[startX, startY] = true;
            var minX = startX; var maxX = startX; var minY = startY; var maxY = startY; var area = 0; var border = false;
            var dx = new[] { -1, 1, 0, 0 }; var dy = new[] { 0, 0, -1, 1 };
            while (queue.Count > 0)
            {
                var p = queue.Dequeue(); area++; minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                border |= p.X == 0 || p.Y == 0 || p.X == bitmap.Width - 1 || p.Y == bitmap.Height - 1;
                for (var i = 0; i < 4; i++)
                {
                    var x = p.X + dx[i]; var y = p.Y + dy[i];
                    if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height || visited[x, y]) continue;
                    if (bitmap.GetPixel(x, y).A > threshold) continue;
                    visited[x, y] = true; queue.Enqueue(new Point(x, y));
                }
            }
            return new Region { X = minX, Y = minY, Width = maxX - minX + 1, Height = maxY - minY + 1, Area = area, TouchesBorder = border };
        }

        private sealed class Region { public int X; public int Y; public int Width; public int Height; public int Area; public bool TouchesBorder; }
    }
}
