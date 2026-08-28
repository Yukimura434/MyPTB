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
        bool returnInProgress;
        WindowState stateBeforeFullScreen = WindowState.Normal;
        public bool ReturnedToAdmin { get; private set; }
        public Func<bool> RequestAdminAccess { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            StateChanged += (s, e) => RestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (fullScreen) ToggleFullScreen();
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        async void Admin_Click(object sender, RoutedEventArgs e)
        {
            if (!TryAuthorizeAdmin()) return;
            var button = sender as System.Windows.Controls.Button;
            if (button != null) button.IsEnabled = false;
            await ReturnToAdminAsync();
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            // When Customer was opened from Admin, every path back to the hidden
            // Admin window (including Alt+F4 and the title-bar close button) returns to Admin.
            if (Owner == null || ReturnedToAdmin) return;
            e.Cancel = true;
            if (returnInProgress) return;
            if (!TryAuthorizeAdmin()) return;
            _ = ReturnToAdminAsync();
        }

        async Task ReturnToAdminAsync()
        {
            if (returnInProgress) return;
            returnInProgress = true;
            try
            {
                var shell = DataContext as ViewModels.CustomerShellViewModel;
                if (shell != null) await shell.PrepareReturnToAdminAsync();
            }
            catch (OperationCanceledException) { }
            finally
            {
                ReturnedToAdmin = true;
                returnInProgress = false;
                Close();
            }
        }

        bool TryAuthorizeAdmin() => RequestAdminAccess == null || RequestAdminAccess();

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

        void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) => (DataContext as ViewModels.CustomerShellViewModel)?.RegisterInteraction();
        void Window_PreviewTouchDown(object sender, TouchEventArgs e) => (DataContext as ViewModels.CustomerShellViewModel)?.RegisterInteraction();

        void ToggleFullScreen()
        {
            if (!fullScreen)
            {
                stateBeforeFullScreen = WindowState;
                Chrome.Visibility = Visibility.Collapsed;
                ChromeRow.Height = new GridLength(0);
                WindowState = WindowState.Maximized;
                fullScreen = true;
            }
            else
            {
                Chrome.Visibility = Visibility.Visible;
                ChromeRow.Height = new GridLength(42);
                WindowState = stateBeforeFullScreen;
                fullScreen = false;
            }
        }
    }
}
