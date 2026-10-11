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
        bool? reviewPortrait;

        public CaptureView()
        {
            InitializeComponent();
            LiveRotation.Changed += (s, e) => ApplyLiveViewLayout();
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
            if (e.PropertyName != nameof(CaptureViewModel.LiveViewRotation) && e.PropertyName != nameof(CaptureViewModel.IsModeSelection) && e.PropertyName != nameof(CaptureViewModel.IsPreview)) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new System.Action(ApplyOrientation)); return; }
            ApplyOrientation();
        }

        void ApplyOrientation()
        {
            var portrait = ActualHeight > ActualWidth;
            var availableWidth = System.Math.Max(1d, ActualWidth);
            var availableHeight = System.Math.Max(1d, ActualHeight);

            ApplyLiveViewLayout();
            LiveLandscapeBackground.Visibility = portrait ? Visibility.Collapsed : Visibility.Visible;
            LivePortraitBackground.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed;

            RecentCapturesPanel.VerticalAlignment = VerticalAlignment.Bottom;
            RecentCapturesPanel.HorizontalAlignment = HorizontalAlignment.Center;
            RecentCapturesPanel.Width = portrait
                ? System.Math.Max(1d, availableWidth - 48d)
                : System.Math.Min(1000d, System.Math.Max(1d, availableWidth - 80d));
            RecentCapturesPanel.Height = portrait ? 220d : double.NaN;
            RecentCapturesPanel.Margin = portrait
                ? new Thickness(24, 0, 24, 0)
                : new Thickness(40, 0, 40, 0);
            var capture = DataContext as CaptureViewModel;
            RecentCapturesPanel.Visibility = portrait && capture != null && !capture.IsModeSelection && !capture.IsPreview ? Visibility.Visible : Visibility.Collapsed;

            if (portrait)
            {
                ModeFirstColumn.Width = new GridLength(System.Math.Max(1d, availableWidth - 72d));
                ModeColumnGap.Width = new GridLength(0);
                ModeSecondColumn.Width = new GridLength(0);
                ModeRowGap.Height = new GridLength(16);
                Grid.SetRow(ManualModeButton, 2);
                Grid.SetColumn(ManualModeButton, 0);
                ModeSelectionGrid.Margin = new Thickness(0);

                ReviewImageColumn.Width = new GridLength(1, GridUnitType.Star);
                ReviewListColumn.Width = new GridLength(0);
                ReviewImageRow.Height = new GridLength(1, GridUnitType.Star);
                ReviewListRow.Height = new GridLength(300);
                Grid.SetRow(ReviewImagePanel, 0); Grid.SetColumn(ReviewImagePanel, 0); Grid.SetColumnSpan(ReviewImagePanel, 2);
                ReviewImagePanel.Margin = new Thickness(0, 0, 0, 14);
                Grid.SetRow(ReviewListPanel, 1); Grid.SetColumn(ReviewListPanel, 0); Grid.SetColumnSpan(ReviewListPanel, 2);
                ReviewOverlay.Margin = new Thickness(32, 112, 32, 250);
                PreviewActions.Orientation = Orientation.Vertical;
                RetakeButton.Width = System.Math.Max(1d, availableWidth - 72d);
                SelectFrameButton.Width = System.Math.Max(1d, availableWidth - 72d);
                if (reviewPortrait != true) SetItemsOrientation(ReviewPhotoList, Orientation.Horizontal);
                ScrollViewer.SetVerticalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Disabled);
                ScrollViewer.SetHorizontalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Auto);
                ScrollViewer.SetPanningMode(ReviewPhotoList, PanningMode.HorizontalOnly);
            }
            else
            {
                ModeFirstColumn.Width = new GridLength(System.Math.Min(520, System.Math.Max(1, (availableWidth - 96) / 2)));
                ModeColumnGap.Width = new GridLength(24);
                ModeSecondColumn.Width = new GridLength(System.Math.Min(520, System.Math.Max(1, (availableWidth - 96) / 2)));
                ModeRowGap.Height = new GridLength(0);
                Grid.SetRow(ManualModeButton, 0);
                Grid.SetColumn(ManualModeButton, 2);
                ModeSelectionGrid.Margin = new Thickness(0);

                ReviewImageColumn.Width = new GridLength(1, GridUnitType.Star);
                ReviewListColumn.Width = new GridLength(230);
                ReviewImageRow.Height = new GridLength(1, GridUnitType.Star);
                ReviewListRow.Height = new GridLength(0);
                Grid.SetRow(ReviewImagePanel, 0); Grid.SetColumn(ReviewImagePanel, 0); Grid.SetColumnSpan(ReviewImagePanel, 1);
                ReviewImagePanel.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetRow(ReviewListPanel, 0); Grid.SetColumn(ReviewListPanel, 1); Grid.SetColumnSpan(ReviewListPanel, 1);
                ReviewOverlay.Margin = new Thickness(28, 112, 28, 104);
                PreviewActions.Orientation = Orientation.Horizontal;
                RetakeButton.Width = double.NaN;
                SelectFrameButton.Width = double.NaN;
                if (reviewPortrait != false) SetItemsOrientation(ReviewPhotoList, Orientation.Vertical);
                ScrollViewer.SetVerticalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(ReviewPhotoList, ScrollBarVisibility.Disabled);
                ScrollViewer.SetPanningMode(ReviewPhotoList, PanningMode.VerticalOnly);
            }
            reviewPortrait = portrait;
        }

        void LiveViewArea_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLiveViewLayout();

        void ApplyLiveViewLayout()
        {
            if (LiveViewArea == null || LiveViewPanel == null || LiveRotation == null) return;
            var ratio = ActualHeight > ActualWidth ? 2d / 3d : 3d / 2d;
            var width = System.Math.Max(0d, System.Math.Min(LiveViewArea.ActualWidth, LiveViewArea.ActualHeight * ratio));
            LiveViewPanel.Width = width;
            LiveViewPanel.Height = width / ratio;
            LiveViewPanel.HorizontalAlignment = HorizontalAlignment.Center;
            LiveViewPanel.VerticalAlignment = VerticalAlignment.Center;
            LiveViewPanel.Margin = new Thickness(0);
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
