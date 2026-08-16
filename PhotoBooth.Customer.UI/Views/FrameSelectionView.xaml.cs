using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PhotoBooth.Customer.UI.Views
{
    public partial class FrameSelectionView : UserControl
    {
        const double MinimumZoom = 1.0;
        const double MaximumZoom = 3.0;
        const double ZoomStep = 0.25;

        double zoom = MinimumZoom;

        public FrameSelectionView()
        {
            InitializeComponent();
        }

        void ZoomIn_OnClick(object sender, RoutedEventArgs e)
        {
            SetZoom(zoom + ZoomStep);
        }

        void ZoomOut_OnClick(object sender, RoutedEventArgs e)
        {
            SetZoom(zoom - ZoomStep);
        }

        void PreviewScroller_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                PreviewScroller.ScrollToHorizontalOffset(PreviewScroller.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;

            SetZoom(zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
            e.Handled = true;
        }

        void PreviewImage_OnTargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
        {
            SetZoom(MinimumZoom);
        }

        void SetZoom(double value)
        {
            zoom = Math.Max(MinimumZoom, Math.Min(MaximumZoom, value));
            PreviewScale.ScaleX = zoom;
            PreviewScale.ScaleY = zoom;
            ZoomPercentText.Text = string.Format("{0:0}%", zoom * 100);

            // LayoutTransform updates ScrollViewer extent asynchronously. Center only after layout is complete.
            PreviewScroller.Dispatcher.BeginInvoke(new Action(CenterPreview), DispatcherPriority.Loaded);
        }

        void PreviewScroller_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            PreviewScroller.Dispatcher.BeginInvoke(new Action(CenterPreview), DispatcherPriority.Loaded);
        }

        void CenterPreview()
        {
            PreviewScroller.UpdateLayout();
            PreviewScroller.ScrollToHorizontalOffset(Math.Max(0,(PreviewScroller.ExtentWidth-PreviewScroller.ViewportWidth)/2));
            PreviewScroller.ScrollToVerticalOffset(Math.Max(0,(PreviewScroller.ExtentHeight-PreviewScroller.ViewportHeight)/2));
        }
    }
}
