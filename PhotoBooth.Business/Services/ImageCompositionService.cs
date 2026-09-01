using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Services
{
    public sealed class ImageCompositionService : IImageCompositionService
    {
        public Task<string> ComposeAsync(Session session, Frame frame, Preset preset, bool final,
            IReadOnlyDictionary<int, string> slotAssignments, CancellationToken token)
        {
            return Task.Run(() =>
            {
                if (token.IsCancellationRequested) return null;
                if (session == null || frame == null)
                    throw new InvalidOperationException("Session or frame unavailable.");

                Directory.CreateDirectory(session.OutputDirectory);
                var next = session.FrameIndex + 1;
                var finalAssetId = final ? Guid.NewGuid().ToString("N") : null;
                var output = final
                    ? Path.Combine(session.OutputDirectory, finalAssetId + ".png")
                    : Path.Combine(session.OutputDirectory, "preview-" + Guid.NewGuid().ToString("N") + ".png");

                using (var canvas = new Bitmap(frame.PixelWidth, frame.PixelHeight, PixelFormat.Format32bppArgb))
                {
                    canvas.SetResolution(300, 300);
                    using (var graphics = Graphics.FromImage(canvas))
                    {
                        graphics.Clear(Color.White);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        foreach (var slot in (frame.Slots ?? new FrameSlot[0]).OrderBy(x => x.Index))
                        {
                            string path;
                            if (slotAssignments == null || !slotAssignments.TryGetValue(slot.Index, out path) || !File.Exists(path))
                                continue;
                            if (!IsRasterImage(path))
                                throw new InvalidDataException("Frame slots require a raster image, but received: " + Path.GetFileName(path));
                            if (token.IsCancellationRequested) return null;
                            using (var image = Image.FromFile(path))
                                DrawCrop(graphics, image, slot);
                        }
                        if (File.Exists(frame.SourcePath))
                            using (var overlay = Image.FromFile(frame.SourcePath))
                                graphics.DrawImage(overlay, new Rectangle(0, 0, canvas.Width, canvas.Height));
                    }
                    if (token.IsCancellationRequested) return null;
                    canvas.Save(output, ImageFormat.Png);
                }

                if (final)
                {
                    session.FrameIndex = next;
                    session.FinalImageId = finalAssetId;
                }
                return output;
            });
        }

        static bool IsRasterImage(string path)
        {
            switch ((Path.GetExtension(path) ?? string.Empty).ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": case ".png": case ".bmp": case ".gif": return true;
                default: return false;
            }
        }

        static void DrawCrop(Graphics graphics, Image image, FrameSlot slot)
        {
            var target = (double)slot.Width / slot.Height;
            var source = (double)image.Width / image.Height;
            double coverWidth, coverHeight;
            if (source > target)
            {
                coverWidth = image.Height * target; coverHeight = image.Height;
            }
            else
            {
                coverWidth = image.Width; coverHeight = image.Width / target;
            }
            var zoom=MediaTransformGeometry.Clamp(slot.MediaZoom,1,2);
            var width=coverWidth/zoom;var height=coverHeight/zoom;
            var centerX=MediaTransformGeometry.Clamp(slot.MediaCenterX, width/(2*image.Width), 1-width/(2*image.Width));
            var centerY=MediaTransformGeometry.Clamp(slot.MediaCenterY, height/(2*image.Height), 1-height/(2*image.Height));
            var crop=new RectangleF((float)(centerX*image.Width-width/2),(float)(centerY*image.Height-height/2),(float)width,(float)height);
            graphics.DrawImage(image, new Rectangle(slot.X, slot.Y, slot.Width, slot.Height), crop, GraphicsUnit.Pixel);
        }
    }
}
