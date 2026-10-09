using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using PhotoBooth.Admin.UI.ViewModels;
namespace PhotoBooth.Admin.UI.Views
{
    public partial class PrinterManagerView : UserControl { public PrinterManagerView() { InitializeComponent(); } }
    public sealed class PrinterConnectionConverter : IValueConverter
    {
        public object Convert(object value, Type type, object parameter, CultureInfo culture) => PrinterManagerViewModel.ConnectionLabel(value as string);
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
