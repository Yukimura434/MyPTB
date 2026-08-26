using System;

namespace PhotoBooth.Core.Models
{
    /// <summary>Shared, renderer-independent Cover crop geometry for image and video slots.</summary>
    public static class MediaTransformGeometry
    {
        public const double MinimumZoom = 1d;
        public const double MaximumZoom = 2d;

        public static MediaLayout Calculate(double mediaWidth, double mediaHeight, double slotWidth, double slotHeight,
            double zoom, double centerX, double centerY)
        {
            if (mediaWidth <= 0 || mediaHeight <= 0 || slotWidth <= 0 || slotHeight <= 0)
                return new MediaLayout(1, 0, 0, 0, 0, 0.5, 0.5);

            zoom = Clamp(zoom, MinimumZoom, MaximumZoom);
            var minimumScale = Math.Max(slotWidth / mediaWidth, slotHeight / mediaHeight);
            var scale = minimumScale * zoom;
            var renderedWidth = mediaWidth * scale;
            var renderedHeight = mediaHeight * scale;
            var overflowX = Math.Max(0, renderedWidth - slotWidth);
            var overflowY = Math.Max(0, renderedHeight - slotHeight);
            centerX = ClampCenter(centerX, renderedWidth, slotWidth);
            centerY = ClampCenter(centerY, renderedHeight, slotHeight);
            var left = slotWidth / 2d - centerX * renderedWidth;
            var top = slotHeight / 2d - centerY * renderedHeight;
            left = Clamp(left, -overflowX, 0);
            top = Clamp(top, -overflowY, 0);
            return new MediaLayout(scale, left, top, renderedWidth, renderedHeight, centerX, centerY);
        }

        public static double ClampCenter(double center, double renderedLength, double slotLength)
        {
            if (renderedLength <= slotLength || renderedLength <= 0) return 0.5d;
            var halfVisible = slotLength / (2d * renderedLength);
            return Clamp(center, halfVisible, 1d - halfVisible);
        }

        public static double Clamp(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    public struct MediaLayout
    {
        public MediaLayout(double scale, double left, double top, double width, double height, double centerX, double centerY)
        { Scale = scale; Left = left; Top = top; Width = width; Height = height; CenterX = centerX; CenterY = centerY; }
        public double Scale { get; }
        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }
        public double CenterX { get; }
        public double CenterY { get; }
    }
}
