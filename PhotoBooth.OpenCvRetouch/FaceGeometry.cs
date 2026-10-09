using System;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using PhotoBooth.Core.Models;

namespace PhotoBooth.OpenCvRetouch
{
    internal static class FaceGeometry
    {
        internal static void Apply(Mat image, Point2f[][] faces, double scale, BeautySettings settings, CancellationToken token)
        {
            double eyes = Math.Max(0, Math.Min(BeautySettings.MaxGeometry, settings.EyeSize)) / 100d * .16;
            double slim = Math.Max(0, Math.Min(BeautySettings.MaxGeometry, settings.SlimFace)) / 100d * .13;
            if ((eyes == 0 && slim == 0) || scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return;
            foreach (var face in faces)
            {
                token.ThrowIfCancellationRequested();
                if (face == null || face.Length != 68) continue;
                var p = face.Select(v => new Point2f((float)(v.X / scale), (float)(v.Y / scale))).ToArray();
                if (p.Any(v => float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsInfinity(v.X) || float.IsInfinity(v.Y))) continue;
                var leftEye = Center(p, 36, 42); var rightEye = Center(p, 42, 48);
                double dx = rightEye.X - leftEye.X, dy = rightEye.Y - leftEye.Y, distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < 4) continue;
                double ux = dx / distance, uy = dy / distance, vx = -uy, vy = ux;
                var brow = new Point2f((p[19].X + p[24].X) / 2, (p[19].Y + p[24].Y) / 2);
                double rx = Math.Abs((p[16].X - p[0].X) * ux + (p[16].Y - p[0].Y) * uy) / 2;
                double ry = (p[8].X - brow.X) * vx + (p[8].Y - brow.Y) * vy;
                if (rx < 4 || ry < 4) continue;
                double radius = (Distance(p[36], p[39]) + Distance(p[42], p[45])) * .65 * 2.2;
                int x0 = Math.Max(0, (int)Math.Floor(p.Min(v => v.X) - radius));
                int y0 = Math.Max(0, (int)Math.Floor(p.Min(v => v.Y) - radius));
                int x1 = Math.Min(image.Width, (int)Math.Ceiling(p.Max(v => v.X) + radius));
                int y1 = Math.Min(image.Height, (int)Math.Ceiling(p.Max(v => v.Y) + radius));
                if (x1 <= x0 || y1 <= y0) continue;
                var region = new Rect(x0, y0, x1 - x0, y1 - y0);
                using (var view = new Mat(image, region))
                using (var source = view.Clone())
                {
                    // Tile the inverse map to keep native memory bounded for full-size photos.
                    for (int top = 0; top < region.Height; top += 256)
                    for (int left = 0; left < region.Width; left += 256)
                    {
                        token.ThrowIfCancellationRequested();
                        int w = Math.Min(256, region.Width - left), h = Math.Min(256, region.Height - top);
                        using (var mx = new Mat(h, w, MatType.CV_32FC1))
                        using (var my = new Mat(h, w, MatType.CV_32FC1))
                        using (var warped = new Mat())
                        using (var target = new Mat(view, new Rect(left, top, w, h)))
                        {
                            FillMaps(mx, my, x0 + left, y0 + top, region, brow, ux, uy, vx, vy, rx, ry, leftEye, rightEye, radius, eyes, slim);
                            Cv2.Remap(source, warped, mx, my, InterpolationFlags.Linear, BorderTypes.Replicate);
                            warped.CopyTo(target);
                        }
                    }
                }
            }
        }

        static unsafe void FillMaps(Mat mx, Mat my, int x0, int y0, Rect region, Point2f brow, double ux, double uy, double vx, double vy,
            double rx, double ry, Point2f leftEye, Point2f rightEye, double radius, double eyes, double slim)
        {
            for (int y = 0; y < mx.Height; y++)
            {
                float* mapX = (float*)mx.Ptr(y), mapY = (float*)my.Ptr(y);
                for (int x = 0; x < mx.Width; x++)
                {
                    double gx = x0 + x, gy = y0 + y, sx = gx, sy = gy;
                    double localX = (gx - brow.X) * ux + (gy - brow.Y) * uy;
                    double localY = (gx - brow.X) * vx + (gy - brow.Y) * vy;
                    double nx = localX / rx, ny = localY / ry, d = nx * nx + ny * ny;
                    if (ny > 0 && d < 1.25)
                    {
                        double fall = Math.Pow(1 - d / 1.25, 2) * Math.Min(1, ny / .18);
                        sx += ux * localX * slim * fall; sy += uy * localX * slim * fall;
                    }
                    EyeOffset(ref sx, ref sy, gx, gy, leftEye, radius, eyes);
                    EyeOffset(ref sx, ref sy, gx, gy, rightEye, radius, eyes);
                    // Fade to identity at image/ROI edges. Never resize or reflect a cropped face.
                    double edge = Math.Min(Math.Min(gx - region.X, region.Right - 1 - gx), Math.Min(gy - region.Y, region.Bottom - 1 - gy));
                    double fade = Math.Min(1, Math.Max(0, edge / 4));
                    mapX[x] = (float)(gx + (sx - gx) * fade - region.X);
                    mapY[x] = (float)(gy + (sy - gy) * fade - region.Y);
                }
            }
        }
        static void EyeOffset(ref double sx, ref double sy, double x, double y, Point2f center, double radius, double amount)
        {
            double dx = x - center.X, dy = y - center.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (radius <= 0 || d >= radius) return;
            double fall = amount * Math.Pow(1 - d / radius, 2);
            sx -= dx * fall; sy -= dy * fall;
        }
        static Point2f Center(Point2f[] p, int start, int end) => new Point2f(p.Skip(start).Take(end - start).Average(v => v.X), p.Skip(start).Take(end - start).Average(v => v.Y));
        static double Distance(Point2f a, Point2f b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }
}
