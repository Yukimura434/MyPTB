using System;

namespace PhotoBooth.Core.Models
{
    public static class LiveViewLayoutGeometry
    {
        public const double MinimumAreaPercent = 5d;
        public const double MaximumAreaPercent = 40d;

        public static LiveViewLayout Calculate(
            double canvasWidth,
            double canvasHeight,
            int rotationDegrees,
            double areaPercent,
            double horizontalPositionPercent,
            double verticalPositionPercent)
        {
            canvasWidth = Math.Max(1d, canvasWidth);
            canvasHeight = Math.Max(1d, canvasHeight);
            areaPercent = Clamp(areaPercent, MinimumAreaPercent, MaximumAreaPercent);

            var quarterTurn = rotationDegrees == 90 || rotationDegrees == -90;
            // Display ratio is width:height. A horizontal camera view is 4:3
            // (3:4 when expressed as height:width); a quarter turn is 3:4.
            var aspectRatio = quarterTurn ? 3d / 4d : 4d / 3d;
            var targetArea = canvasWidth * canvasHeight * areaPercent / 100d;
            var width = Math.Sqrt(targetArea * aspectRatio);
            var height = width / aspectRatio;

            // A portrait Live View can be placed on a landscape customer canvas (and vice versa).
            // Preserve its aspect ratio and cap it inside the canvas instead of clipping it.
            var fitScale = Math.Min(1d, Math.Min(canvasWidth / width, canvasHeight / height));
            width *= fitScale;
            height *= fitScale;

            // Live View is intentionally fixed at the exact canvas centre.
            // Position arguments remain in the contract for settings compatibility.
            var left = (canvasWidth - width) / 2d;
            var top = (canvasHeight - height) / 2d;
            return new LiveViewLayout(width, height, left, top);
        }

        static double Clamp(double value, double minimum, double maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));
    }

    public sealed class LiveViewLayout
    {
        public LiveViewLayout(double width, double height, double left, double top)
        { Width = width; Height = height; Left = left; Top = top; }

        public double Width { get; }
        public double Height { get; }
        public double Left { get; }
        public double Top { get; }
    }
}
