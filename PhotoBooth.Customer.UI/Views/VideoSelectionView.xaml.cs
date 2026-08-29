using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBooth.Customer.UI.ViewModels;

namespace PhotoBooth.Customer.UI.Views
{
    public partial class VideoSelectionView : UserControl
    {
        bool videoScrollHooked;
        public VideoSelectionView()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyOrientation();
        }

        void ApplyOrientation()
        {
            var shell = Window.GetWindow(this)?.DataContext as CustomerShellViewModel;
            var portrait = shell != null && shell.IsPortraitMode;
            var framePanel = (FrameworkElement)WorkspaceGrid.Children[0];
            var previewPanel = (FrameworkElement)WorkspaceGrid.Children[1];
            var videoPanel = (FrameworkElement)WorkspaceGrid.Children[2];
            var videoItems = FindVisualChild<ItemsControl>(videoPanel);

            if (portrait)
            {
                FrameRow.Height = new GridLength(240);
                PreviewRow.Height = new GridLength(1, GridUnitType.Star);
                VideoRow.Height = new GridLength(300);
                SetPortraitColumns();
                Place(framePanel, 0, 0, 5);
                Place(previewPanel, 1, 0, 5);
                Place(videoPanel, 2, 0, 5);
                framePanel.Margin = new Thickness(0, 0, 0, 12);
                previewPanel.Margin = new Thickness(0, 0, 0, 12);
                if (videoItems != null)
                {
                    SetItemsOrientation(videoItems, Orientation.Horizontal);
                    var scroll = FindVisualChild<ScrollViewer>(videoPanel);
                    if (scroll != null)
                    {
                        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        scroll.PanningMode = PanningMode.HorizontalOnly;
                        if (!videoScrollHooked) { scroll.PreviewMouseWheel += ScrollHorizontally; videoScrollHooked = true; }
                    }
                }
            }
            else
            {
                FrameRow.Height = new GridLength(1, GridUnitType.Star);
                PreviewRow.Height = new GridLength(0);
                VideoRow.Height = new GridLength(0);
                WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(280);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(18);
                WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                WorkspaceGrid.ColumnDefinitions[3].Width = new GridLength(18);
                WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(280);
                Place(framePanel, 0, 0, 1);
                Place(previewPanel, 0, 2, 1);
                Place(videoPanel, 0, 4, 1);
                framePanel.Margin = new Thickness(0);
                previewPanel.Margin = new Thickness(0);
                if (videoItems != null)
                {
                    SetItemsOrientation(videoItems, Orientation.Vertical);
                    var scroll = FindVisualChild<ScrollViewer>(videoPanel);
                    if (scroll != null)
                    {
                        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        scroll.PanningMode = PanningMode.VerticalOnly;
                    }
                }
            }
        }

        static void SetItemsOrientation(ItemsControl control, Orientation orientation)
        {
            var factory = new FrameworkElementFactory(typeof(StackPanel));
            factory.SetValue(StackPanel.OrientationProperty, orientation);
            control.ItemsPanel = new ItemsPanelTemplate(factory);
        }

        static void ScrollHorizontally(object sender, MouseWheelEventArgs e)
        {
            var viewer = sender as ScrollViewer;
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

        void SetPortraitColumns()
        {
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            for (var i = 1; i < WorkspaceGrid.ColumnDefinitions.Count; i++)
                WorkspaceGrid.ColumnDefinitions[i].Width = new GridLength(0);
        }

        static void Place(UIElement element, int row, int column, int columnSpan)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, columnSpan);
        }

    }
}
