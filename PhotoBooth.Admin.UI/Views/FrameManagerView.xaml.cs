using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PhotoBooth.Admin.UI.ViewModels;
namespace PhotoBooth.Admin.UI.Views
{
    public partial class FrameManagerView : UserControl
    {
        public FrameManagerView()
        {
            InitializeComponent();
            SizeChanged += (_, __) => FramePreview.Height = System.Math.Max(170, System.Math.Min(300, ActualHeight - 480));
            AssetList.SizeChanged += (_, __) => ArrangeFrameCards();
            Loaded += (_, __) => ArrangeFrameCards();
        }
        public static readonly DependencyProperty FrameCardWidthProperty = DependencyProperty.Register(nameof(FrameCardWidth), typeof(double), typeof(FrameManagerView), new PropertyMetadata(246d));
        public static readonly DependencyProperty FrameCardHeightProperty = DependencyProperty.Register(nameof(FrameCardHeight), typeof(double), typeof(FrameManagerView), new PropertyMetadata(360d));
        public static readonly DependencyProperty FrameThumbnailWidthProperty = DependencyProperty.Register(nameof(FrameThumbnailWidth), typeof(double), typeof(FrameManagerView), new PropertyMetadata(200d));
        public double FrameCardWidth => (double)GetValue(FrameCardWidthProperty);
        public double FrameCardHeight => (double)GetValue(FrameCardHeightProperty);
        public double FrameThumbnailWidth => (double)GetValue(FrameThumbnailWidthProperty);

        void ArrangeFrameCards()
        {
            if (AssetList.ActualWidth <= 20 || AssetList.ActualHeight <= 0) return;
            // Reserve the scrollbar and list padding, then fit up to four columns.
            var available = AssetList.ActualWidth - 20;
            var columns = System.Math.Max(1, System.Math.Min(4, (int)(available / 240)));
            var width = System.Math.Floor(available / columns) - 12;
            // Two rows at the target desktop size; retain readable cards on short windows.
            var height = System.Math.Max(286, System.Math.Min(460, System.Math.Floor((AssetList.ActualHeight - 4) / 2) - 12));
            SetValue(FrameCardWidthProperty, width);
            SetValue(FrameCardHeightProperty, height);
            // The preview includes 14px of button padding/border around a 2:3 image area.
            var thumbnailHeight = height - 26 - 50;
            SetValue(FrameThumbnailWidthProperty, System.Math.Min(width - 26, (thumbnailHeight - 14) * 2 / 3 + 14));
        }

        void PrintedQr_Click(object sender,RoutedEventArgs e) { (DataContext as FrameManagerViewModel)?.EditPrintedQr(Window.GetWindow(this)); }

        void Collections_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is FrameManagerViewModel model)) return;
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
