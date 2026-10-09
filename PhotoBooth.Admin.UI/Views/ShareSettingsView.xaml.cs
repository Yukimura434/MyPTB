using System.Windows;
using System.Windows.Controls;

namespace PhotoBooth.Admin.UI.Views
{
    public partial class ShareSettingsView : UserControl
    {
        const double NarrowLayoutWidth = 980;

        public ShareSettingsView()
        {
            InitializeComponent();
            Loaded += (_, __) => UpdateResponsiveLayout(ActualWidth);
            SizeChanged += (_, args) => UpdateResponsiveLayout(args.NewSize.Width);
        }

        void UpdateResponsiveLayout(double width)
        {
            var narrow = width < NarrowLayoutWidth;
            DeliveryColumn.Width = new GridLength(1, GridUnitType.Star);
            ColumnGap.Width = narrow ? new GridLength(0) : new GridLength(16);
            StorageColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            RowGap.Height = narrow ? new GridLength(16) : new GridLength(0);
            StorageRow.Height = narrow ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(StorageCard, narrow ? 0 : 2);
            Grid.SetRow(StorageCard, narrow ? 2 : 0);
        }
    }
}
