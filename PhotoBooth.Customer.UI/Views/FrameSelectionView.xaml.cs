using System.Windows;
using System.Windows.Controls;

namespace PhotoBooth.Customer.UI.Views
{
    public partial class FrameSelectionView : UserControl
    {
        public FrameSelectionView()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyOrientation();
            SizeChanged += (s, e) => ApplyOrientation();
        }

        void ApplyOrientation()
        {
            var portrait = ActualHeight > ActualWidth * 1.08;
            if (portrait)
            {
                WorkspaceTopRow.Height = new GridLength(1, GridUnitType.Star);
                WorkspaceBottomRow.Height = new GridLength(300);
                WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(12);
                WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(0);
                WorkspaceGrid.ColumnDefinitions[3].Width = new GridLength(12);
                WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(PreviewPanel, 0); Grid.SetColumn(PreviewPanel, 0); Grid.SetColumnSpan(PreviewPanel, 5);
                Grid.SetRow(FramesPanel, 1); Grid.SetColumn(FramesPanel, 0); Grid.SetColumnSpan(FramesPanel, 2);
                Grid.SetRow(PhotosPanel, 1); Grid.SetColumn(PhotosPanel, 3); Grid.SetColumnSpan(PhotosPanel, 2);
            }
            else
            {
                WorkspaceTopRow.Height = new GridLength(1, GridUnitType.Star);
                WorkspaceBottomRow.Height = new GridLength(0);
                WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(280);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(18);
                WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                WorkspaceGrid.ColumnDefinitions[3].Width = new GridLength(18);
                WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(260);
                Grid.SetRow(FramesPanel, 0); Grid.SetColumn(FramesPanel, 0); Grid.SetColumnSpan(FramesPanel, 1);
                Grid.SetRow(PreviewPanel, 0); Grid.SetColumn(PreviewPanel, 2); Grid.SetColumnSpan(PreviewPanel, 1);
                Grid.SetRow(PhotosPanel, 0); Grid.SetColumn(PhotosPanel, 4); Grid.SetColumnSpan(PhotosPanel, 1);
            }
        }
    }
}
