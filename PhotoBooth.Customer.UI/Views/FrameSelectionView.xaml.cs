using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBooth.Customer.UI.ViewModels;

namespace PhotoBooth.Customer.UI.Views
{
    public partial class FrameSelectionView : UserControl
    {
        bool? appliedPortrait;
        public FrameSelectionView()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyOrientation();
            SizeChanged += (s, e) => ApplyOrientation();
            FrameList.PreviewMouseWheel += ScrollHorizontally;
            CapturedPhotoScroll.PreviewMouseWheel += ScrollHorizontally;
        }

        void ApplyOrientation()
        {
            var shell = Window.GetWindow(this)?.DataContext as CustomerShellViewModel;
            var portrait = shell != null && shell.IsPortraitMode;
            if (appliedPortrait == portrait) return;
            appliedPortrait = portrait;
            if (portrait)
            {
                WorkspaceTopRow.Height = new GridLength(320);
                WorkspaceBottomRow.Height = new GridLength(1, GridUnitType.Star);
                WorkspacePhotoRow.Height = new GridLength(300);
                WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
                WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(0);
                WorkspaceGrid.ColumnDefinitions[3].Width = new GridLength(0);
                WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(0);
                Grid.SetRow(FramesPanel, 0); Grid.SetColumn(FramesPanel, 0); Grid.SetColumnSpan(FramesPanel, 5);
                Grid.SetRow(PreviewPanel, 1); Grid.SetColumn(PreviewPanel, 0); Grid.SetColumnSpan(PreviewPanel, 5);
                Grid.SetRow(PhotosPanel, 2); Grid.SetColumn(PhotosPanel, 0); Grid.SetColumnSpan(PhotosPanel, 5);
                FramesPanel.Margin = new Thickness(0, 0, 0, 12);
                PreviewPanel.Margin = new Thickness(0, 0, 0, 12);
                SetItemsOrientation(FrameList, Orientation.Horizontal);
                SetItemsOrientation(CapturedPhotoList, Orientation.Horizontal);
                SetHorizontalScrolling(FrameList, true);
                CapturedPhotoScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                CapturedPhotoScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                CapturedPhotoScroll.PanningMode = PanningMode.HorizontalOnly;
            }
            else
            {
                WorkspaceTopRow.Height = new GridLength(1, GridUnitType.Star);
                WorkspaceBottomRow.Height = new GridLength(0);
                WorkspacePhotoRow.Height = new GridLength(0);
                WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(280);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(18);
                WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                WorkspaceGrid.ColumnDefinitions[3].Width = new GridLength(18);
                WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(260);
                Grid.SetRow(FramesPanel, 0); Grid.SetColumn(FramesPanel, 0); Grid.SetColumnSpan(FramesPanel, 1);
                Grid.SetRow(PreviewPanel, 0); Grid.SetColumn(PreviewPanel, 2); Grid.SetColumnSpan(PreviewPanel, 1);
                Grid.SetRow(PhotosPanel, 0); Grid.SetColumn(PhotosPanel, 4); Grid.SetColumnSpan(PhotosPanel, 1);
                FramesPanel.Margin = new Thickness(0);
                PreviewPanel.Margin = new Thickness(0);
                SetItemsOrientation(FrameList, Orientation.Vertical);
                SetItemsOrientation(CapturedPhotoList, Orientation.Vertical);
                SetHorizontalScrolling(FrameList, false);
                CapturedPhotoScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                CapturedPhotoScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                CapturedPhotoScroll.PanningMode = PanningMode.VerticalOnly;
            }
        }

        static void SetItemsOrientation(ItemsControl control, Orientation orientation)
        {
            var factory = new FrameworkElementFactory(typeof(StackPanel));
            factory.SetValue(StackPanel.OrientationProperty, orientation);
            control.ItemsPanel = new ItemsPanelTemplate(factory);
        }

        static void SetHorizontalScrolling(DependencyObject control, bool horizontal)
        {
            ScrollViewer.SetHorizontalScrollBarVisibility(control, horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(control, horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            ScrollViewer.SetPanningMode(control, horizontal ? PanningMode.HorizontalOnly : PanningMode.VerticalOnly);
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
