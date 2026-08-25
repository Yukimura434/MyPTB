using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class WaitingViewModel : ObservableObject
    {
        private readonly IInterfaceAssetService _assets;
        private readonly CaptureViewModel _capture;
        private readonly ISettingsService _settings;
        private readonly LiveColorState _liveColor;

        private string _backgroundPath;

        public WaitingViewModel(
            IInterfaceAssetService assetService,
            CaptureViewModel captureViewModel,
            ISettingsService settingsService,
            LiveColorState liveColor)
        {
            _assets = assetService;
            _capture = captureViewModel;
            _settings = settingsService;
            _liveColor = liveColor;

            EnterCommand = new RelayCommand(OnEnter);

            _capture.PropertyChanged += OnCapturePropertyChanged;
        }

        public event EventHandler EnterRequested;

        public byte[] LiveImage => _capture.LiveImage;
        public int LiveFrameWidth => _capture.LiveFrameWidth;
        public int LiveFrameHeight => _capture.LiveFrameHeight;
        public bool ShowLiveView { get; private set; } = true;
        public double LiveViewCanvasX { get; private set; } = 160;
        public double LiveViewCanvasY { get; private set; } = 90;
        public double LiveViewScaleX { get; private set; } = 1;
        public LiveColorState LiveColor => _liveColor;

        public string BackgroundPath
        {
            get => _backgroundPath;
            private set => Set(ref _backgroundPath, value);
        }

        public ICommand EnterCommand { get; }

        public async Task ActivateAsync()
        {
            var selectedAsset =
                await _assets.GetSelectedAsync(CancellationToken.None);

            BackgroundPath = selectedAsset?.FilePath;
            var configured = await _settings.GetAsync(CancellationToken.None);
            ShowLiveView = configured.ShowWaitingLiveView;
            LiveViewCanvasX = configured.WaitingLiveViewX * 16;
            LiveViewCanvasY = configured.WaitingLiveViewY * 9;
            LiveViewScaleX = configured.AutoFlip ? -1d : 1d;
            await _liveColor.RefreshAsync(configured,CancellationToken.None);
            Raise(nameof(ShowLiveView));
            Raise(nameof(LiveViewCanvasX));
            Raise(nameof(LiveViewCanvasY));
            Raise(nameof(LiveViewScaleX));

            Raise(nameof(LiveImage));
        }

        public void DisableGpuLiveColor(Exception error)
        {
            _liveColor.Disable(error);
        }

        private void OnEnter()
        {
            EnterRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnCapturePropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CaptureViewModel.LiveImage))
            {
                Raise(nameof(LiveImage));
                Raise(nameof(LiveFrameWidth));
                Raise(nameof(LiveFrameHeight));
            }
        }
    }
}
