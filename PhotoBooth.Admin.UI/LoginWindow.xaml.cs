using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI
{
    public partial class LoginWindow : Window
    {
        readonly IPcAuthenticationService authentication;
        readonly DeviceEnrollmentViewModel deviceEnrollment;

        public LoginWindow(IPcAuthenticationService authentication, DeviceEnrollmentViewModel deviceEnrollment)
        {
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.deviceEnrollment = deviceEnrollment ?? throw new ArgumentNullException(nameof(deviceEnrollment));
            InitializeComponent();
            SizeChanged += (_, __) => ArrangeLogin();
            Loaded += LoginWindow_Loaded;
        }

        void ArrangeLogin()
        {
            var compact = ActualWidth < 900;
            StoryPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            LoginCard.MaxWidth = compact ? 550 : 1040;
            FormBorder.CornerRadius = compact ? new CornerRadius(17) : new CornerRadius(0, 17, 17, 0);
            CompactBrand.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            LoginLayout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 0.9, GridUnitType.Star);
            FormPanel.Margin = new Thickness(compact ? 28 : 36, 30, compact ? 28 : 36, 30);
        }

        public PcSession Session { get; private set; }

        public void ConfigureForAdminAccess(Window owner)
        {
            Owner = owner;
            Title = "MiuPhotoBooth · Đăng nhập nhân viên";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            HeadingText.Text = "Đăng nhập nhân viên";
            SubtitleText.Text = "Đăng nhập để mở trang quản lý booth.";
            DeviceSetupPanel.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Quay lại màn hình chụp";
        }

        async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshDeviceStatusAsync();
            EmailBox.Focus();
        }

        async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();
            if (string.IsNullOrWhiteSpace(EmailBox.Text) || string.IsNullOrEmpty(CurrentPassword()))
            {
                ShowError("Vui lòng nhập tên đăng nhập và mật khẩu.");
                (string.IsNullOrWhiteSpace(EmailBox.Text) ? (System.Windows.Controls.Control)EmailBox : PasswordBox).Focus();
                return;
            }

            SetBusy(true);
            try
            {
                var result = await authentication.AuthenticateAsync(
                    EmailBox.Text,
                    CurrentPassword(),
                    false,
                    CancellationToken.None);
                if (!result.Succeeded)
                {
                    ShowError(result.Error ?? "Đăng nhập thất bại.");
                    PasswordBox.Clear();
                    VisiblePasswordBox.Clear();
                    PasswordBox.Focus();
                    return;
                }

                Session = result.Session;
                PasswordBox.Clear();
                VisiblePasswordBox.Clear();
                DialogResult = true;
            }
            catch (Exception)
            {
                ShowError("Không thể hoàn tất đăng nhập. Vui lòng thử lại.");
            }
            finally { SetBusy(false); }
        }

        async void OpenEnrollment_Click(object sender, RoutedEventArgs e)
        {
            new DeviceEnrollmentWindow(deviceEnrollment) { Owner = this }.ShowDialog();
            await RefreshDeviceStatusAsync();
            EmailBox.Focus();
        }

        void ShowPasswordBox_Checked(object sender, RoutedEventArgs e)
        {
            VisiblePasswordBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            VisiblePasswordBox.Visibility = Visibility.Visible;
            UpdatePasswordPlaceholder();
            VisiblePasswordBox.Focus();
            VisiblePasswordBox.CaretIndex = VisiblePasswordBox.Text.Length;
        }

        void ShowPasswordBox_Unchecked(object sender, RoutedEventArgs e)
        {
            PasswordBox.Password = VisiblePasswordBox.Text;
            VisiblePasswordBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            UpdatePasswordPlaceholder();
            PasswordBox.Focus();
        }

        void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UpdatePasswordPlaceholder();
        }

        void VisiblePasswordBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (VisiblePasswordBox.Visibility == Visibility.Visible) PasswordBox.Password = VisiblePasswordBox.Text;
            UpdatePasswordPlaceholder();
        }

        void UpdatePasswordPlaceholder()
        {
            if (PasswordPlaceholder == null) return;
            PasswordPlaceholder.Visibility = string.IsNullOrEmpty(CurrentPassword())
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        string CurrentPassword() => ShowPasswordBox.IsChecked == true ? VisiblePasswordBox.Text : PasswordBox.Password;

        async System.Threading.Tasks.Task RefreshDeviceStatusAsync()
        {
            await deviceEnrollment.RefreshAsync();
            DeviceStatusTitle.Text = deviceEnrollment.DeviceStatusTitle;
            DeviceStatusDetail.Text = deviceEnrollment.DeviceStatusDetail;
            var ready = deviceEnrollment.HasActiveProfile;
            DeviceStatusIcon.Data = (Geometry)FindResource(ready ? "Icon.Check" : "Icon.Warning");
            DeviceStatusIcon.Stroke = (Brush)FindResource(ready ? "SuccessBrush" : "WarningBrush");
            DeviceStatusIconBorder.Background = (Brush)FindResource(ready ? "SuccessSubtleBrush" : "WarningSubtleBrush");
            LoginButton.IsEnabled = ready || IsDevelopmentBuild();
        }

        static bool IsDevelopmentBuild()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorBorder.Visibility = Visibility.Visible;
        }

        void HideError()
        {
            ErrorText.Text = string.Empty;
            ErrorBorder.Visibility = Visibility.Collapsed;
        }

        void SetBusy(bool busy)
        {
            LoginButton.IsEnabled = !busy;
            EnrollmentButton.IsEnabled = !busy;
            CancelButton.IsEnabled = !busy;
            ShowPasswordBox.IsEnabled = !busy;
            EmailBox.IsEnabled = !busy;
            PasswordBox.IsEnabled = !busy;
            VisiblePasswordBox.IsEnabled = !busy;
            BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy && !deviceEnrollment.HasActiveProfile && !IsDevelopmentBuild()) LoginButton.IsEnabled = false;
        }
    }
}
