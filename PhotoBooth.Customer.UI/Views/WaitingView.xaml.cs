using System.Windows;
using System.Windows.Controls;
using PhotoBooth.Customer.UI.ViewModels;

namespace PhotoBooth.Customer.UI.Views
{
    public partial class WaitingView : UserControl
    {
        public WaitingView()
        {
            InitializeComponent();
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
        }
    }
}
