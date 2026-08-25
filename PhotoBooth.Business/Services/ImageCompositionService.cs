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
                token.ThrowIfCancellationRequested();
                if (session == null || frame == null)
                    throw new InvalidOperationException("Session or frame unavailable.");

                Directory.CreateDirectory(session.OutputDirectory);
                var next = session.FrameIndex + 1;
                var output = final
                    ? Path.Combine(session.OutputDirectory, "frm" + next.ToString("D3") + ".png")
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
                            token.ThrowIfCancellationRequested();
                            using (var image = Image.FromFile(path))
                                DrawCrop(graphics, image, slot);
                        }
                        if (File.Exists(frame.SourcePath))
                            using (var overlay = Image.FromFile(frame.SourcePath))
                                graphics.DrawImage(overlay, new Rectangle(0, 0, canvas.Width, canvas.Height));
                    }
                    canvas.Save(output, ImageFormat.Png);
                }

                if (final)
                {
                    session.FrameIndex = next;
                    session.FinalImageId = "00" + session.StartedAtUtc.ToLocalTime().ToString("MMdd") +
                                           session.SessionNumber.ToString("D2") + next.ToString("D4");
                }
                return output;
            }, token);
        }

        static void DrawCrop(Graphics graphics, Image image, FrameSlot slot)
        {
            var target = (double)slot.Width / slot.Height;
            var source = (double)image.Width / image.Height;
            RectangleF crop;
            if (source > target)
            {
                var width = (float)(image.Height * target);
                crop = new RectangleF((image.Width - width) / 2, 0, width, image.Height);
            }
            else
            {
                var height = (float)(image.Width / target);
                crop = new RectangleF(0, (image.Height - height) / 2, image.Width, height);
            }
            graphics.DrawImage(image, new Rectangle(slot.X, slot.Y, slot.Width, slot.Height), crop, GraphicsUnit.Pixel);
        }
    }
}
