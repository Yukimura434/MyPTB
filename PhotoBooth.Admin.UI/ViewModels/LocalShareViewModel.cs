using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class LocalShareViewModel : PageViewModel
    {
        readonly ISettingsService settings;
        readonly ILocalShareService localShare;
        readonly ILogger<LocalShareViewModel> log;

        bool localShareEnabled = true;
        string statusText = "Server Local Share chưa được khởi động.";

        public LocalShareViewModel(
            ISettingsService settings,
            ILocalShareService localShare,
            ILogger<LocalShareViewModel> log)
        {
            this.settings = settings;
            this.localShare = localShare;
            this.log = log;

            StartServerCommand = new AsyncCommand(_ => StartServerAsync(), _ => LocalShareEnabled);
            _ = LoadSettingsAsync();
        }

        public override string Title => "Local Share";

        public string StatusText
        {
            get => statusText;
            private set => Set(ref statusText, value);
        }

        public ICommand StartServerCommand { get; }

        public bool LocalShareEnabled
        {
            get => localShareEnabled;
            set
            {
                if (Set(ref localShareEnabled, value))
                {
                    _ = PersistSettingsAsync();
                }
            }
        }

        async Task LoadSettingsAsync()
        {
            try
            {
                var value = await settings.GetAsync(CancellationToken.None);
                localShareEnabled = value.LocalShareEnabled;
                Raise(nameof(LocalShareEnabled));
                StatusText = localShare.IsRunning
                    ? "Server đang chạy tại " + localShare.BaseUrl
                    : "Server Local Share chưa được khởi động.";
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not load local share settings.");
            }
        }

        async Task StartServerAsync()
        {
            try
            {
                StatusText = "Đang khởi động server Local Share...";
                await localShare.StartAsync(CancellationToken.None);
                StatusText = "Server đang chạy tại " + localShare.BaseUrl;
            }
            catch (Exception e)
            {
                StatusText = "Không thể khởi động server: " + e.Message;
                log.LogError(e, "Could not start Local Share server.");
            }
        }

        async Task PersistSettingsAsync()
        {
            try
            {
                var value = await settings.GetAsync(CancellationToken.None);
                value.LocalShareEnabled = localShareEnabled;
                await settings.SaveAsync(value, CancellationToken.None);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not save local share settings.");
            }
        }
    }
}
