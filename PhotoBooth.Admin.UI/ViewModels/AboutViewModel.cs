using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class AboutViewModel : PageViewModel
    {
        readonly IUpdateService updates;
        bool busy;
        string status = "Bạn chủ động kiểm tra và chọn cài bản mới. Ứng dụng không tự cập nhật.";
        string availableVersion;
        string installerPath;
        public AboutViewModel(IUpdateService updates)
        {
            this.updates = updates;
            var assembly = typeof(AboutViewModel).Assembly;
            Version = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion?.Split('+')[0] ?? assembly.GetName().Version.ToString();
            CheckCommand = new RelayCommand(async _ => await CheckAsync(), _ => !busy);
            DownloadCommand = new RelayCommand(async _ => await DownloadAsync(), _ => !busy && availableVersion != null && installerPath == null);
            InstallCommand = new RelayCommand(_ => Install(), _ => !busy && installerPath != null);
        }
        public override string Title => "About";
        public string ProductName => "MiuPhotoBooth";
        public string Version { get; }
        public string Status { get => status; private set => Set(ref status, value); }
        public string AvailableVersion => availableVersion;
        public bool HasUpdate => availableVersion != null && installerPath == null;
        public bool IsReadyToInstall => installerPath != null;
        public RelayCommand CheckCommand { get; }
        public RelayCommand DownloadCommand { get; }
        public RelayCommand InstallCommand { get; }
        void Refresh()
        {
            Raise(nameof(AvailableVersion)); Raise(nameof(HasUpdate)); Raise(nameof(IsReadyToInstall));
            CheckCommand.RaiseCanExecuteChanged(); DownloadCommand.RaiseCanExecuteChanged(); InstallCommand.RaiseCanExecuteChanged();
        }
        async Task CheckAsync()
        {
            busy = true; availableVersion = null; installerPath = null; Status = "Đang kiểm tra bản cập nhật…"; Refresh();
            try
            {
                if (await updates.IsUpdateAvailableAsync(CancellationToken.None))
                { availableVersion = await updates.GetAvailableVersionAsync(CancellationToken.None); Status = "Đã có phiên bản " + availableVersion + ". Bạn có thể tải về khi sẵn sàng."; }
                else Status = "Bạn đang dùng phiên bản mới nhất.";
            }
            catch { Status = "Chưa kiểm tra được bản cập nhật. Hãy kiểm tra kết nối mạng và thử lại."; }
            finally { busy = false; Refresh(); }
        }
        async Task DownloadAsync()
        {
            busy = true; Status = "Đang tải và xác minh bộ cài. Bạn có thể tiếp tục chờ tại đây…"; Refresh();
            try { installerPath = await updates.DownloadUpdateAsync(CancellationToken.None); Status = "Bộ cài đã sẵn sàng. Chọn cài đặt để đóng ứng dụng và bắt đầu cập nhật."; }
            catch { Status = "Không thể tải hoặc xác minh bộ cài. Chưa có thay đổi nào được cài đặt. Vui lòng thử lại."; }
            finally { busy = false; Refresh(); }
        }
        void Install()
        {
            if (installerPath == null || busy) return;
            if (MessageBox.Show("Ứng dụng sẽ đóng để mở bộ cài. Ảnh và cấu hình được giữ lại; sau khi cập nhật bạn cần đăng nhập lại.", "Cài bản cập nhật", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            try
            {
                Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
            catch { Status = "Chưa mở được bộ cài. Vui lòng thử lại."; }
        }
    }
}
