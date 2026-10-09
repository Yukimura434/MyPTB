using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace PhotoBooth.Customer.UI.Controls
{
    public partial class CustomerHeader : UserControl
    {
        public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(int), typeof(CustomerHeader), new PropertyMetadata(1));
        public static readonly DependencyProperty IsManagerEntranceEnabledProperty = DependencyProperty.Register(nameof(IsManagerEntranceEnabled), typeof(bool), typeof(CustomerHeader), new PropertyMetadata(false));
        public bool IsManagerEntranceEnabled { get => (bool)GetValue(IsManagerEntranceEnabledProperty); set => SetValue(IsManagerEntranceEnabledProperty, value); }
        public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }
        public CustomerHeader() { InitializeComponent(); SizeChanged += (s, e) => { BrandTitle.FontSize = ActualWidth < 650 ? 19 : 21; Slogan.Visibility = ActualWidth < 1000 ? Visibility.Collapsed : Visibility.Visible; Bars.Visibility = ActualWidth < 650 ? Visibility.Collapsed : Visibility.Visible; HeaderGrid.Margin = new Thickness(ActualWidth < 650 ? 20 : ActualWidth < 1000 ? 28 : 34, 0, ActualWidth < 650 ? 20 : ActualWidth < 1000 ? 28 : 34, 0); Second.Background = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(Step >= 2 ? "#FF72AA" : "#57495B")); }; }
        async void Brand_Click(object sender, RoutedEventArgs e) { if (IsManagerEntranceEnabled && Window.GetWindow(this) is MainWindow window) await window.RegisterStaffPressAsync(); }
    }
}
