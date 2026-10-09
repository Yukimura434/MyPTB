using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI.Views
{
    public partial class InterfaceView : UserControl
    {
        public static readonly DependencyProperty BackgroundCropAnchorProperty = DependencyProperty.Register(nameof(BackgroundCropAnchor), typeof(double), typeof(InterfaceView), new PropertyMetadata(.5d));
        public double BackgroundCropAnchor { get => (double)GetValue(BackgroundCropAnchorProperty); private set => SetValue(BackgroundCropAnchorProperty, value); }
        Point dragStart;
        double dragPanX, dragPanY, dragLiveViewX, dragLiveViewY;
        DragTarget dragTarget;

        enum DragTarget
        {
            None,
            Background,
            LiveView
        }

        public InterfaceView() { InitializeComponent(); Loaded += (_, __) => Layout(); SizeChanged += (_, __) => Layout(); PreviewSurface.SizeChanged += (_, __) => SizePreviewText(); PreviewCanvas.SizeChanged += (_, __) => SizePreviewText(); }
        void Layout()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            bool portrait = ActualHeight > ActualWidth, small = portrait && ActualWidth <= 650, shortWide = !portrait && ActualHeight <= 850;
            FontSize = small ? 13 : 17; AssetList.Margin = new Thickness(0, 0, -12, 0); LiveHeading.Visibility = LivePositionLabel.Visibility = LiveHint.Visibility = AdjustHint.Visibility = portrait ? Visibility.Collapsed : Visibility.Visible; BackgroundCropAnchor = portrait ? .34 : .5; Tag = portrait ? (small ? 14d : 17d) : 15d;
            PortraitMenu.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed;
            RailColumn.Width = new GridLength(portrait ? 0 : 78);
            Header.Margin = new Thickness(portrait ? (small ? 18 : 24) : shortWide ? 24 : 30, portrait ? (small ? 18 : 24) : shortWide ? 18 : 24, portrait ? (small ? 18 : 24) : shortWide ? 24 : 30, portrait && !small ? 19 : 18);
            HeaderSubtitle.FontSize = small ? 12 : 13; HeaderSubtitle.LineHeight = HeaderSubtitle.FontSize * 1.6;
            Title.FontSize = portrait ? (small ? 25 : 30) : 28; Title.LineHeight = Title.FontSize * 1.12; PreviewBar.Margin = new Thickness(0, 0, 0, small ? 8 : 12); FullPreviewButton.MinHeight = small ? 40 : 44; PreviewHint.Margin = new Thickness(0, small ? 8 : 12, 0, 0); PreviewHint.FontSize = small ? 11 : 12; EditorTabsRow.Height = new GridLength(portrait && !small ? 67 : 65); BackgroundHeading.Margin = new Thickness(0, 0, 0, portrait ? 16 : 31); StatusLabel.FontSize = small ? 12 : 14; SaveButton.FontSize = DiscardButton.FontSize = small ? 13 : 17; SaveButton.Padding = DiscardButton.Padding = small ? new Thickness(13, 10, 13, 10) : new Thickness(19, 12, 19, 12);
            Grid.SetRow(Orientations, portrait ? 1 : 0); Grid.SetColumn(Orientations, portrait ? 0 : 1); Grid.SetColumnSpan(Orientations, portrait ? 2 : 1); Orientations.Margin = new Thickness(0, portrait ? 20 : 0, 0, 0);
            Workspace.Margin = new Thickness(portrait ? (small ? 18 : 24) : shortWide ? 24 : 30, portrait ? (small ? 16 : 20) : shortWide ? 18 : 24, portrait ? (small ? 18 : 24) : shortWide ? 24 : 30, portrait ? (small ? 16 : 20) : shortWide ? 18 : 24);
            ToolColumn.Width = new GridLength(portrait ? 0 : shortWide ? 320 : 340); ToolGap.Width = new GridLength(portrait ? 0 : shortWide ? 18 : 24); ToolRow.Height = new GridLength(portrait ? (small ? 270 : 300) : 0); ToolRowGap.Height = new GridLength(portrait ? (small ? 14 : 18) : 0); Grid.SetColumn(Tools, portrait ? 0 : 2); Grid.SetRow(Tools, portrait ? 2 : 0);
            ToolContent.Margin = new Thickness(portrait ? (small ? 16 : 20) : 20, portrait ? (small ? 12 : 14) : 20, portrait ? (small ? 16 : 20) : 20, portrait ? (small ? 12 : 14) : 20);
            AssetList.Tag = portrait ? (small ? 73d : 92d) : 135d;
            var itemStyle = new Style(typeof(ListBoxItem), (Style)FindResource("EditorItem"));
            double available = portrait ? ActualWidth - (small ? 70 : 90) : (shortWide ? 320 : 340) - 42;
            itemStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, System.Math.Max(90, System.Math.Floor((available - (portrait ? 24 : 12)) / (portrait ? 3 : 2)))));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
            itemStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, portrait ? (small ? 116d : 135d) : 178d));
            itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 12)));
            var uploadColors = new DataTrigger { Binding = new System.Windows.Data.Binding("FilePath"), Value = null }; uploadColors.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(41, 51, 68)))); itemStyle.Triggers.Add(uploadColors);
            if (!portrait) { var upload = new DataTrigger { Binding = new System.Windows.Data.Binding("FilePath"), Value = null }; upload.Setters.Add(new Setter(FrameworkElement.WidthProperty, available)); upload.Setters.Add(new Setter(FrameworkElement.HeightProperty, 52d)); itemStyle.Triggers.Add(upload); }
            AssetList.ItemContainerStyle = itemStyle;
            FooterRow.Height = new GridLength(portrait ? (small ? 80 : 94) : 88); Footer.Margin = new Thickness(small ? 18 : portrait ? 24 : 30, 12, small ? 18 : portrait ? 24 : 30, 12);
        }
        void SizePreviewText()
        {
            if (!(DataContext is InterfaceViewModel vm)) return;
            double width = System.Math.Min(PreviewSurface.ActualWidth - 42, (PreviewSurface.ActualHeight - 42) * vm.PreviewCanvasWidth / vm.PreviewCanvasHeight);
            double scale = width / vm.PreviewCanvasWidth; if (scale <= 0) return;
            bool portrait = ActualHeight > ActualWidth, small = portrait && ActualWidth <= 650;
            double font = portrait ? (small ? 16 : 24) : System.Math.Max(12, System.Math.Min(25, ActualWidth * .016));
            StandbyBrand.FontSize = (portrait ? (small ? 17 : 24) : System.Math.Max(12, System.Math.Min(24, ActualWidth * .016))) / scale;
            StandbyBrand.Margin = new Thickness(vm.PreviewCanvasWidth * .07, vm.PreviewCanvasHeight * .05, 0, 0);
            StandbyTitle.FontSize = font / scale; StandbyTitle.LineHeight = font * 1.3 / scale; StandbyTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            StandbyPrompt.Margin = new Thickness(vm.PreviewCanvasWidth * .08, 0, vm.PreviewCanvasWidth * .08, vm.PreviewCanvasHeight * .07);
            StandbyTitle.Margin = new Thickness(0, 0, 0, vm.PreviewCanvasWidth * .84 * .06);
        }
        void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (BackgroundPanel == null) return;
            var key = ((RadioButton)sender).Tag as string;
            BackgroundPanel.Visibility = key == "background" ? Visibility.Visible : Visibility.Collapsed;
            LivePanel.Visibility = key == "live" ? Visibility.Visible : Visibility.Collapsed;
            AdjustPanel.Visibility = key == "adjust" ? Visibility.Visible : Visibility.Collapsed;
        }
        void Move_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is InterfaceViewModel vm)) return;
            var key = ((Button)sender).Tag as string; bool live = key.StartsWith("live");
            if (key.EndsWith("center")) { if (live) { vm.LiveViewX = 50; vm.LiveViewY = 50; } else { vm.BackgroundPanX = 0; vm.BackgroundPanY = 0; } return; }
            double dx = key.EndsWith("left") ? -5 : key.EndsWith("right") ? 5 : 0;
            double dy = key.EndsWith("up") ? -5 : key.EndsWith("down") ? 5 : 0;
            if (live) { vm.LiveViewX += dx; vm.LiveViewY += dy; } else { vm.BackgroundPanX += dx; vm.BackgroundPanY += dy; }
        }
        void Navigate_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this)?.DataContext is MainViewModel main) main.NavigateCommand.Execute(((Button)sender).Tag);
        }
        void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (!(Window.GetWindow(this)?.DataContext is MainViewModel main)) return;
            var menu = new ContextMenu { Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black };
            foreach (var entry in new[] { new[] { "Tổng quan", "home" }, new[] { "Khung ảnh", "frames" }, new[] { "Preset màu", "presets" }, new[] { "Làm đẹp", "beauty" }, new[] { "Thiết bị", "device" }, new[] { "Máy in", "printers" }, new[] { "Ảnh & mã QR", "share" }, new[] { "Kênh chia sẻ", "share-settings" }, new[] { "Báo cáo", "data" }, new[] { "Giới thiệu", "about" } }) menu.Items.Add(new MenuItem { Header = entry[0], Command = main.NavigateCommand, CommandParameter = entry[1] });
            menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "Mở màn hình khách", Command = main.StartCustomerCommand }); menu.Items.Add(new MenuItem { Header = "Đăng xuất", Command = main.SignOutCommand });
            menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
        }
        void FullScreen_Click(object sender, RoutedEventArgs e)
        {
            // A visual snapshot never acquires or initializes a camera preview.
            PreviewCanvas.UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)PreviewCanvas.ActualWidth, (int)PreviewCanvas.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(PreviewCanvas);
            var grid = new Grid { Background = System.Windows.Media.Brushes.Black };
            grid.Children.Add(new Image { Source = image, Stretch = System.Windows.Media.Stretch.Uniform });
            var close = new Button { Content = "Đóng xem trước  ×", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20), Padding = new Thickness(20, 12, 20, 12), Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black };
            grid.Children.Add(close);
            var window = new Window { Owner = Window.GetWindow(this), WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, Content = grid, Title = "Xem trước màn hình chờ" };
            close.Click += (_, __) => window.Close(); window.PreviewKeyDown += (_, key) => { if (key.Key == Key.Escape) window.Close(); }; window.ShowDialog();
        }

        void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(DataContext is InterfaceViewModel vm)) return;

            dragStart = e.GetPosition(PreviewCanvas);
            if (vm.ShowLiveView && IsInsideLiveView(dragStart, vm))
            {
                dragLiveViewX = vm.LiveViewX;
                dragLiveViewY = vm.LiveViewY;
                dragTarget = DragTarget.LiveView;
            }
            else if (vm.BackgroundZoom > 100)
            {
                dragPanX = vm.BackgroundPanX;
                dragPanY = vm.BackgroundPanY;
                dragTarget = DragTarget.Background;
            }
            else
            {
                return;
            }

            PreviewCanvas.CaptureMouse();
            e.Handled = true;
        }

        void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragTarget == DragTarget.None || !(DataContext is InterfaceViewModel vm)) return;
            var point = e.GetPosition(PreviewCanvas);

            if (dragTarget == DragTarget.LiveView)
            {
                var availableX = vm.PreviewCanvasWidth - vm.PreviewLiveViewWidth;
                var availableY = vm.PreviewCanvasHeight - vm.PreviewLiveViewHeight;
                if (availableX > 0) vm.LiveViewX = dragLiveViewX + ((point.X - dragStart.X) * 100d / availableX);
                if (availableY > 0) vm.LiveViewY = dragLiveViewY + ((point.Y - dragStart.Y) * 100d / availableY);
                return;
            }

            var overflowX = vm.PreviewBackgroundWidth - vm.PreviewCanvasWidth;
            var overflowY = vm.PreviewBackgroundHeight - vm.PreviewCanvasHeight;
            if (overflowX > 0) vm.BackgroundPanX = dragPanX - ((point.X - dragStart.X) * 200d / overflowX);
            if (overflowY > 0) vm.BackgroundPanY = dragPanY - ((point.Y - dragStart.Y) * 200d / overflowY);
        }

        void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (dragTarget == DragTarget.None) return;
            dragTarget = DragTarget.None;
            PreviewCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        void PreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(DataContext is InterfaceViewModel vm)) return;
            vm.BackgroundZoom += e.Delta > 0 ? 10 : -10; e.Handled = true;
        }

        static bool IsInsideLiveView(Point point, InterfaceViewModel vm) =>
            point.X >= vm.PreviewLiveViewX &&
            point.X <= vm.PreviewLiveViewX + vm.PreviewLiveViewWidth &&
            point.Y >= vm.PreviewLiveViewY &&
            point.Y <= vm.PreviewLiveViewY + vm.PreviewLiveViewHeight;
    }
}
