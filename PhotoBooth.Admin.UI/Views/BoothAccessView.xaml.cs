using System.Windows;
using System.Windows.Controls;

namespace PhotoBooth.Admin.UI.Views
{
    public partial class BoothAccessView : UserControl
    {
        const double NarrowLayoutWidth = 900;

        public BoothAccessView()
        {
            InitializeComponent();
            Loaded += (_, __) => UpdateResponsiveLayout(ActualWidth);
            SizeChanged += (_, args) => UpdateResponsiveLayout(args.NewSize.Width);
        }

        void UpdateResponsiveLayout(double width)
        {
            var narrow = width < NarrowLayoutWidth;
            FormColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(5, GridUnitType.Star);
            GapColumn.Width = narrow ? new GridLength(0) : new GridLength(18);
            CodeColumn.Width = narrow ? new GridLength(0) : new GridLength(4, GridUnitType.Star);
            StackGapRow.Height = narrow ? new GridLength(18) : new GridLength(0);
            CodeRow.Height = narrow ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(CodeCard, narrow ? 0 : 2);
            Grid.SetRow(CodeCard, narrow ? 2 : 0);
        }
    }
}
