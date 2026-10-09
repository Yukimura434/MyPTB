using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Threading.Tasks;

namespace PhotoBooth.Customer.UI
{
    public partial class MainWindow : Window
    {
        bool fullScreen;
        bool managerRequestInProgress;
        int staffClickCount;
        DateTime lastStaffClickUtc;
        WindowState stateBeforeFullScreen = WindowState.Normal;
        public Func<Task> RequestManagerAccessAsync { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            SizeChanged += (s, e) => UpdateViewport();
            DataContextChanged += (s, e) => UpdateViewport();
            Loaded += (s, e) => UpdateViewport();
            Activated += async (s, e) => { if (DataContext is ViewModels.CustomerShellViewModel shell) await shell.ValidateForegroundAsync(); };
            StateChanged += (s, e) => RestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        void UpdateViewport()
        {
            (DataContext as ViewModels.CustomerShellViewModel)?.UpdateViewport(ActualWidth, ActualHeight);
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (fullScreen) ToggleFullScreen();
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        public async Task RegisterStaffPressAsync()
        {
            if (managerRequestInProgress || !(DataContext is ViewModels.CustomerShellViewModel shell) ||
                !(shell.CurrentPage is ViewModels.BoothLoginViewModel login) || login.IsBusy)
            {
                staffClickCount = 0;
                return;
            }
            var now = DateTime.UtcNow;
            if ((now - lastStaffClickUtc).TotalSeconds > 2) staffClickCount = 0;
            lastStaffClickUtc = now;
            if (++staffClickCount < 5) return;
            staffClickCount = 0;
            await RequestManagerAsync();
        }

        async Task RequestManagerAsync()
        {
            if (managerRequestInProgress || RequestManagerAccessAsync == null) return;
            managerRequestInProgress = true;
            try { await RequestManagerAccessAsync(); }
            finally { managerRequestInProgress = false; staffClickCount = 0; }
        }

        async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F5) { staffClickCount = 0; return; }
            e.Handled = true;
            if (!e.IsRepeat) await RequestManagerAsync();
        }

        void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) Restore_Click(sender, e);
            else if (e.LeftButton == MouseButtonState.Pressed && WindowState == WindowState.Normal) DragMove();
        }

        void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
            else if (e.Key == Key.Escape && fullScreen) { ToggleFullScreen(); e.Handled = true; }
            else if (e.Key == Key.Space)
            {
                var shell = DataContext as ViewModels.CustomerShellViewModel;
                var capture = shell?.CurrentPage as ViewModels.CaptureViewModel;
                if (capture != null && capture.RequestManualCapture()) e.Handled = true;
            }
        }

        void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsStaffInput(e.OriginalSource)) staffClickCount = 0;
            (DataContext as ViewModels.CustomerShellViewModel)?.RegisterInteraction();
        }
        bool IsStaffInput(object source)
        {
            var node = source as DependencyObject;
            while (node != null)
            {
                if (node is FrameworkElement element && Equals(element.Tag, "CustomerStaffEntrance")) return true;
                node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        void Window_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            if (!IsStaffInput(e.OriginalSource)) staffClickCount = 0;
            (DataContext as ViewModels.CustomerShellViewModel)?.RegisterInteraction();
        }

        void ToggleFullScreen()
        {
            if (!fullScreen)
            {
                stateBeforeFullScreen = WindowState;
                Chrome.Visibility = Visibility.Collapsed;
                WindowState = WindowState.Maximized;
                fullScreen = true;
            }
            else
            {
                Chrome.ClearValue(VisibilityProperty);
                WindowState = stateBeforeFullScreen;
                fullScreen = false;
            }
        }
    }
}
