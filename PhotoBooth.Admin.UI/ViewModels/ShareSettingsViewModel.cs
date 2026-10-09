using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class ShareSettingsViewModel : PageViewModel
    {
        readonly ISettingsService settings;
        readonly ILocalShareService localShare;
        readonly IRemoteStorageConfigurationService remoteStorage;
        readonly ILogger<ShareSettingsViewModel> log;
        string shareMode = ShareDeliveryModes.CloudWithLanFallback;
        string onlineProvider = RemoteStorageProviders.SystemLibrary;
        bool googleDriveConnected;
        bool storageBusy;
        bool localShareEnabled = true;
        string googleDriveEmail;
        string onlineStorageStatusText = "Đang kiểm tra nơi lưu ảnh trực tuyến...";
        string statusText = "Chia sẻ trong mạng nội bộ chưa được bật.";

        public ShareSettingsViewModel(
            ISettingsService settings,
            ILocalShareService localShare,
            IRemoteStorageConfigurationService remoteStorage,
            ILogger<ShareSettingsViewModel> log)
        {
            this.settings = settings;
            this.localShare = localShare;
            this.remoteStorage = remoteStorage;
            this.log = log;
            StartServerCommand = new AsyncCommand(_ => StartServerAsync(), _ => LocalShareEnabled);
            RefreshStorageCommand = new AsyncCommand(_ => RefreshRemoteStorageAsync(), _ => !StorageBusy);
            _ = LoadSettingsAsync();
        }

        public override string Title => "Kênh chia sẻ";
        public bool IsCloudOnly { get => shareMode == ShareDeliveryModes.CloudOnly; set { if (value) SetShareMode(ShareDeliveryModes.CloudOnly); } }
        public bool IsLanOnly { get => shareMode == ShareDeliveryModes.LanOnly; set { if (value) SetShareMode(ShareDeliveryModes.LanOnly); } }
        public bool IsCloudWithLanFallback { get => shareMode == ShareDeliveryModes.CloudWithLanFallback; set { if (value) SetShareMode(ShareDeliveryModes.CloudWithLanFallback); } }
        public bool IsSystemLibrary { get => onlineProvider == RemoteStorageProviders.SystemLibrary; set { if (value) _ = SetOnlineProviderAsync(RemoteStorageProviders.SystemLibrary); } }
        public bool IsGoogleDrive { get => onlineProvider == RemoteStorageProviders.GoogleDrive; set { if (value) _ = SetOnlineProviderAsync(RemoteStorageProviders.GoogleDrive); } }

        public bool GoogleDriveConnected
        {
            get => googleDriveConnected;
            private set
            {
                if (!Set(ref googleDriveConnected, value)) return;
                Raise(nameof(CanSelectGoogleDrive));
                Raise(nameof(GoogleDriveStateText));
            }
        }

        public bool CanSelectGoogleDrive => GoogleDriveConnected && !StorageBusy;
        public string GoogleDriveEmail { get => googleDriveEmail; private set => Set(ref googleDriveEmail, value); }
        public string GoogleDriveStateText => GoogleDriveConnected ? "Đã kết nối" : "Chưa kết nối";
        public string OnlineStorageStatusText { get => onlineStorageStatusText; private set => Set(ref onlineStorageStatusText, value); }

        public bool StorageBusy
        {
            get => storageBusy;
            private set
            {
                if (!Set(ref storageBusy, value)) return;
                Raise(nameof(CanSelectGoogleDrive));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string StatusText { get => statusText; private set => Set(ref statusText, value); }
        public ICommand StartServerCommand { get; }
        public ICommand RefreshStorageCommand { get; }

        public bool LocalShareEnabled
        {
            get => localShareEnabled;
            private set
            {
                if (!Set(ref localShareEnabled, value)) return;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        async Task LoadSettingsAsync()
        {
            try
            {
                var value = await settings.GetAsync(CancellationToken.None);
                LocalShareEnabled = value.LocalShareEnabled;
                shareMode = ShareDeliveryModes.Normalize(value.ShareDeliveryMode);
                RaiseModeProperties();
                StatusText = localShare.IsRunning
                    ? "Chia sẻ trong mạng nội bộ đang sẵn sàng."
                    : "Chia sẻ trong mạng nội bộ chưa được bật.";
                await RefreshRemoteStorageAsync();
            }
            catch (Exception exception)
            {
                StatusText = "Chưa thể tải thiết lập chia sẻ. Vui lòng thử lại.";
                log.LogWarning(exception, "Could not load share channel settings.");
            }
        }

        async Task RefreshRemoteStorageAsync()
        {
            if (StorageBusy) return;
            StorageBusy = true;
            try
            {
                var result = await remoteStorage.GetAsync(CancellationToken.None);
                if (!result.Succeeded)
                {
                    OnlineStorageStatusText = result.ErrorMessage;
                    return;
                }

                onlineProvider = RemoteStorageProviders.Normalize(result.Provider);
                GoogleDriveConnected = result.GoogleDriveConnected;
                GoogleDriveEmail = result.GoogleEmail;
                OnlineStorageStatusText = BuildGoogleDriveStatus(result.GoogleDriveStatus, result.GoogleEmail, result.GoogleDriveConnected);
                RaiseStorageProperties();
            }
            catch (Exception exception)
            {
                OnlineStorageStatusText = "Không thể kiểm tra nơi lưu ảnh. Vui lòng thử lại.";
                log.LogWarning(exception, "Could not refresh remote storage configuration.");
            }
            finally
            {
                StorageBusy = false;
            }
        }

        async Task SetOnlineProviderAsync(string provider)
        {
            provider = RemoteStorageProviders.Normalize(provider);
            if (StorageBusy || provider == onlineProvider) return;
            if (provider == RemoteStorageProviders.GoogleDrive && !GoogleDriveConnected)
            {
                OnlineStorageStatusText = "Google Drive chưa sẵn sàng. Chủ tài khoản cần kết nối trên Web Customer trước.";
                RaiseStorageProperties();
                return;
            }

            StorageBusy = true;
            try
            {
                var result = await remoteStorage.SetProviderAsync(provider, CancellationToken.None);
                if (!result.Succeeded)
                {
                    OnlineStorageStatusText = result.ErrorMessage;
                    RaiseStorageProperties();
                    return;
                }

                onlineProvider = RemoteStorageProviders.Normalize(result.Provider);
                GoogleDriveConnected = result.GoogleDriveConnected;
                GoogleDriveEmail = result.GoogleEmail;
                OnlineStorageStatusText = onlineProvider == RemoteStorageProviders.GoogleDrive
                    ? "Các lượt chụp mới sẽ được lưu vào Google Drive."
                    : "Các lượt chụp mới sẽ được lưu trong thư viện MiuPhotoBooth.";
                RaiseStorageProperties();
            }
            catch (Exception exception)
            {
                OnlineStorageStatusText = "Không thể đổi nơi lưu ảnh. Vui lòng thử lại.";
                log.LogWarning(exception, "Could not update remote storage provider.");
                RaiseStorageProperties();
            }
            finally
            {
                StorageBusy = false;
            }
        }

        async Task StartServerAsync()
        {
            try
            {
                StatusText = "Đang bật chia sẻ trong mạng nội bộ...";
                await localShare.StartAsync(CancellationToken.None);
                StatusText = "Chia sẻ trong mạng nội bộ đang sẵn sàng.";
            }
            catch (Exception exception)
            {
                StatusText = "Chưa thể bật chia sẻ trong mạng nội bộ. Hãy kiểm tra kết nối mạng của máy.";
                log.LogError(exception, "Could not start Local Share server.");
            }
        }

        async Task PersistSettingsAsync()
        {
            try
            {
                var value = await settings.GetAsync(CancellationToken.None);
                value.LocalShareEnabled = localShareEnabled;
                value.ShareDeliveryMode = shareMode;
                await settings.SaveAsync(value, CancellationToken.None);
            }
            catch (Exception exception)
            {
                StatusText = "Chưa thể lưu phương thức nhận ảnh. Vui lòng thử lại.";
                log.LogWarning(exception, "Could not save share channel settings.");
            }
        }

        void SetShareMode(string value)
        {
            if (shareMode == value) return;
            shareMode = value;
            LocalShareEnabled = value != ShareDeliveryModes.CloudOnly;
            RaiseModeProperties();
            _ = PersistSettingsAsync();
        }

        void RaiseModeProperties()
        {
            Raise(nameof(IsCloudOnly));
            Raise(nameof(IsLanOnly));
            Raise(nameof(IsCloudWithLanFallback));
            Raise(nameof(LocalShareEnabled));
        }

        void RaiseStorageProperties()
        {
            Raise(nameof(IsSystemLibrary));
            Raise(nameof(IsGoogleDrive));
        }

        static string BuildGoogleDriveStatus(string status, string email, bool connected)
        {
            if (connected)
            {
                return string.IsNullOrWhiteSpace(email)
                    ? "Đã kết nối Google Drive. Thay đổi chỉ áp dụng cho lượt chụp mới."
                    : "Đã kết nối: " + email + ". Thay đổi chỉ áp dụng cho lượt chụp mới.";
            }

            return status == "reconnect_required"
                ? "Kết nối Google Drive cần được xác minh lại trên Web Customer."
                : "Chưa kết nối Google Drive. Chủ tài khoản có thể thiết lập tại Web Customer.";
        }
    }
}
