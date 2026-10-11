using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoBooth.Customer.UI.ViewModels;

namespace PhotoBooth.Customer.UI.Views
{
    public partial class WaitingView : UserControl
    {
        public WaitingView()
        {
            InitializeComponent();
            LiveRotation.Changed += (sender, args) => UpdateLiveViewLayout();
            StartChrome.SizeChanged += (sender, args) => UpdateLiveViewLayout();
            LiveViewPlacement.LayoutUpdated += (sender, args) => UpdateLiveViewLayout();
            Loaded += (sender, args) => ApplyOrientation();
            SizeChanged += (sender, args) => ApplyOrientation();
            DataContextChanged += (sender, args) => ApplyOrientation();
        }

        void ApplyOrientation()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            (DataContext as WaitingViewModel)?.UpdateViewport(ActualWidth, ActualHeight);
            var portrait = ActualHeight > ActualWidth;
            StartChrome.MaxWidth = System.Math.Max(1, ActualWidth - 60);
            DefaultLandscapeBackground.Visibility = portrait ? Visibility.Collapsed : Visibility.Visible;
            DefaultPortraitBackground.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed;
            UpdateLiveViewLayout();
        }

        void LiveViewPlacement_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLiveViewLayout();
        }

        void UpdateLiveViewLayout()
        {
            if (LiveViewViewport == null || LiveViewPlacement == null || LiveRotation == null || StartChrome == null) return;
            if (ActualWidth <= 0 || ActualHeight <= 0 || LiveViewPlacement.ActualWidth <= 0 || LiveViewPlacement.ActualHeight <= 0) return;
            var ratio = ActualHeight > ActualWidth ? 2d / 3d : 3d / 2d;
            var availableWidth = System.Math.Max(0d, ActualWidth - 48d);
            var availableHeight = System.Math.Max(0d, ActualHeight - StartChrome.ActualHeight - StartChrome.Margin.Bottom - 48d);
            var width = System.Math.Min(System.Math.Min(LiveViewPlacement.ActualWidth, availableWidth),
                System.Math.Min(LiveViewPlacement.ActualHeight, availableHeight) * ratio);
            LiveViewViewport.Width = width;
            LiveViewViewport.Height = width / ratio;
            var left = Canvas.GetLeft(LiveViewPlacement) + (LiveViewPlacement.ActualWidth - width) / 2d;
            var top = Canvas.GetTop(LiveViewPlacement) + (LiveViewPlacement.ActualHeight - width / ratio) / 2d;
            if (double.IsNaN(left) || double.IsNaN(top)) return;
            var targetLeft = System.Math.Max(24d, System.Math.Min(left, ActualWidth - 24d - width));
            var targetTop = System.Math.Max(24d, System.Math.Min(top, 24d + availableHeight - width / ratio));
            var current = LiveViewViewport.RenderTransform as TranslateTransform;
            if (current == null || System.Math.Abs(current.X - (targetLeft - left)) > .01d || System.Math.Abs(current.Y - (targetTop - top)) > .01d)
                LiveViewViewport.RenderTransform = new TranslateTransform(targetLeft - left, targetTop - top);
        }
    }
}
