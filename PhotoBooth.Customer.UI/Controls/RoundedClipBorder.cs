using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoBooth.Customer.UI.Controls
{
    /// <summary>
    /// A Border whose child is actually clipped to CornerRadius. WPF's regular
    /// Border only rounds its chrome; ClipToBounds still uses a rectangular clip.
    /// </summary>
    public sealed class RoundedClipBorder : Border
    {
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateClip();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == CornerRadiusProperty)
                UpdateClip();
        }

        private void UpdateClip()
        {
            var radius = CornerRadius.TopLeft;
            Clip = ActualWidth > 0 && ActualHeight > 0
                ? new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius)
                : null;
        }
    }
}
