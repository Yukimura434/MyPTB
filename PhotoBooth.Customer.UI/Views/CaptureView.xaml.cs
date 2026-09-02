using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBooth.Color.D3D11;
using PhotoBooth.Customer.UI.ViewModels;

namespace PhotoBooth.Customer.UI.Views
{
    public partial class CaptureView : UserControl
    {
        INotifyPropertyChanged subscribedViewModel;
        bool viewLoaded;

        public CaptureView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += (s, e) => ApplyOrientation();
            DataContextChanged += OnDataContextChanged;
            ReviewPhotoList.PreviewMouseWheel += ScrollHorizontally;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            viewLoaded = true;
            SubscribeToViewModel(DataContext as INotifyPropertyChanged);
            ApplyOrientation();
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            viewLoaded = false;
            SubscribeToViewModel(null);
        }

        void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            SubscribeToViewModel(viewLoaded ? e.NewValue as INotifyPropertyChanged : null);
            ApplyOrientation();
        }

        void SubscribeToViewModel(INotifyPropertyChanged value)
        {
            if (ReferenceEquals(subscribedViewModel, value)) return;
            if (subscribedViewModel != null) subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = value;
            if (subscribedViewModel != null) subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(CaptureViewModel.LiveViewRotation)) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new System.Action(ApplyOrientation)); return; }
            ApplyOrientation();
        }

        void ApplyOrientation()
        {
            var portrait = ActualHeight > ActualWidth * 1.08;
            var viewModel = DataContext as CaptureViewModel;
            var rotation = NormalizeRotation(viewModel?.LiveViewRotation ?? 0d);
            var quarterTurn = rotation == 90d || rotation == -90d;
            // Fit a fixed 4:3 landscape or 3:4 portrait viewport inside the
            // screen and keep its centre locked to the screen centre.
            var aspect = quarterTurn ? 3d / 4d : 4d / 3d;
            var availableWidth = System.Math.Max(1d, ActualWidth);
            var availableHeight = System.Math.Max(1d, ActualHeight);
            var width = availableWidth;
            var height = width / aspect;
            if (height > availableHeight) { height = availableHeight; width = height * aspect; }

            if (portrait)
            {
                const double sideInset = 40d;
                const double minimumTop = 82d;
                const double liveToRecentGap = 20d;
                const double bottomActionReserve = 112d;
                var recentHeight = quarterTurn ? 356d : 220d;
                var contentBottom = System.Math.Max(minimumTop + 1d, availableHeight - bottomActionReserve);
                var maximumWidth = System.Math.Max(1d, availableWidth - sideInset * 2d);
                width = maximumWidth;
                height = width / aspect;
                var maximumLiveHeight = System.Math.Max(1d, contentBottom - minimumTop - liveToRecentGap - recentHeight);
                if (height > maximumLiveHeight) { height = maximumLiveHeight; width = height * aspect; }
                var groupHeight = height + liveToRecentGap + recentHeight;
                var top = System.Math.Max(minimumTop, (contentBottom - groupHeight) / 2d);

                LiveViewPanel.Width = width;
                LiveViewPanel.Height = height;
                LiveViewPanel.VerticalAlignment = VerticalAlignment.Top;
                LiveViewPanel.HorizontalAlignment = HorizontalAlignment.Center;
                LiveViewPanel.Margin = new Thickness(0, top, 0, 0);
                RecentCapturesPanel.Width = maximumWidth;
                RecentCapturesPanel.Height = recentHeight;
                RecentCapturesPanel.Margin = new Thickness(0, top + height + liveToRecentGap, 0, 0);
            }
            else
            {
                LiveViewPanel.Width = width;
                LiveViewPanel.Height = height;
                LiveViewPanel.VerticalAlignment = VerticalAlignment.Center;
                LiveViewPanel.HorizontalAlignment = HorizontalAlignment.Center;
                LiveViewPanel.Margin = new Thickness(0);
                RecentCapturesPanel.ClearValue(FrameworkElement.HeightProperty);
                RecentCapturesPanel.Width = 1000d;
                RecentCapturesPanel.Margin = new Thickness(0, 870, 0, 0);
            }
            RecentCapturesPanel.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed;

            if (portrait)
            {
                ReviewImageColumn.Width = new GridLength(1, GridUnitType.Star);
                ReviewListColumn.Width = new GridLength(0);
                ReviewImageRow.Height = new GridLength(1, GridUnitType.Star);
                ReviewListRow.Height = new GridLength(300);
                Grid.SetRow(ReviewImagePanel, 0); Grid.SetColumn(ReviewImagePanel, 0); Grid.SetColumnSpan(ReviewImagePanel, 2);
                ReviewImagePanel.Margin = new Thickness(0, 0, 0, 14);
                Grid.SetRow(ReviewListPanel, 1); Grid.SetColumn(ReviewListPanel, 0); Grid.SetColumnSpan(ReviewListPanel, 2);
                ReviewOverlay.Margin = new Thickness(32, 84, 32, 112);
                SetItemsOrientation(ReviewPhotoList, Orientation.Horizontal);
                ScrollViewer.SetVerticalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Disabled);
                ScrollViewer.SetHorizontalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Auto);
                ScrollViewer.SetPanningMode(ReviewPhotoList, PanningMode.HorizontalOnly);
            }
            else
            {
                ReviewImageColumn.Width = new GridLength(1, GridUnitType.Star);
                ReviewListColumn.Width = new GridLength(230);
                ReviewImageRow.Height = new GridLength(1, GridUnitType.Star);
                ReviewListRow.Height = new GridLength(0);
                Grid.SetRow(ReviewImagePanel, 0); Grid.SetColumn(ReviewImagePanel, 0); Grid.SetColumnSpan(ReviewImagePanel, 1);
                ReviewImagePanel.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetRow(ReviewListPanel, 0); Grid.SetColumn(ReviewListPanel, 1); Grid.SetColumnSpan(ReviewListPanel, 1);
                ReviewOverlay.Margin = new Thickness(28, 28, 28, 104);
                SetItemsOrientation(ReviewPhotoList, Orientation.Vertical);
                ScrollViewer.SetVerticalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Disabled);
                ScrollViewer.SetPanningMode(ReviewPhotoList, PanningMode.VerticalOnly);
            }
        }

        static double NormalizeRotation(double value)
        {
            if (value == 90d || value == -90d || value == 180d) return value;
            return 0d;
        }

        static void SetItemsOrientation(ItemsControl control, Orientation orientation)
        {
            var factory = new FrameworkElementFactory(typeof(StackPanel));
            factory.SetValue(StackPanel.OrientationProperty, orientation);
            control.ItemsPanel = new ItemsPanelTemplate(factory);
        }

        static void ScrollHorizontally(object sender, MouseWheelEventArgs e)
        {
            var viewer = FindVisualChild<ScrollViewer>((DependencyObject)sender);
            if (viewer == null || viewer.ScrollableWidth <= 0) return;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var match = child as T ?? FindVisualChild<T>(child);
                if (match != null) return match;
            }
            return null;
        }

    }
}
