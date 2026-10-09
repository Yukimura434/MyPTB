using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PhotoBooth.Admin.UI.ViewModels;
namespace PhotoBooth.Admin.UI.Views
{
    public partial class PresetManagerView : UserControl
    {
        public PresetManagerView()
        {
            InitializeComponent();
            DetailPanel.SizeChanged += (_, __) => ArrangePresetPreview();
            Loaded += (_, __) => ArrangePresetPreview();
        }
        void ArrangePresetPreview()
        {
            var compact = DetailPanel.ActualWidth < 480;
            PreviewImageColumn.Width = new GridLength(compact ? 1 : 0.9, GridUnitType.Star);
            PreviewGapColumn.Width = new GridLength(compact ? 0 : 18);
            PreviewControlColumn.Width = new GridLength(compact ? 0 : 1.3, compact ? GridUnitType.Pixel : GridUnitType.Star);
            PreviewGapRow.Height = new GridLength(compact ? 16 : 0);
            Grid.SetColumn(PresetControls, compact ? 0 : 2);
            Grid.SetRow(PresetControls, compact ? 2 : 0);
            PresetImage.MaxHeight = compact ? System.Math.Max(160, System.Math.Min(220, DetailPanel.ActualHeight - 440)) : 300;
        }
        void Collections_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is PresetManagerViewModel model)) return;
            model.LibraryTab = "Bộ sưu tập";
            if (model.Collections.Count == 0) return;
            CollectionsButton.ContextMenu.PlacementTarget = CollectionsButton;
            CollectionsButton.ContextMenu.Placement = PlacementMode.Bottom;
            CollectionsButton.ContextMenu.IsOpen = true;
        }
        void ManageCollections_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CollectionManagerWindow { DataContext = DataContext, Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            ManageCollectionsButton.Focus();
        }
    }
}
