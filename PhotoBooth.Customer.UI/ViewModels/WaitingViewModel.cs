using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class WaitingViewModel : ObservableObject
    {
        private readonly IInterfaceAssetService _assets;
        private readonly CaptureViewModel _capture;
        private readonly ISettingsService _settings;

        private string _backgroundPath;

        public WaitingViewModel(
            IInterfaceAssetService assetService,
            CaptureViewModel captureViewModel,
            ISettingsService settingsService)
        {
            _assets = assetService;
            _capture = captureViewModel;
            _settings = settingsService;

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
        public double LiveViewRotation { get; private set; }
        public double LiveViewAreaPercent { get; private set; } = LiveViewLayoutGeometry.MinimumAreaPercent;
        public bool IsPortraitMode { get; private set; }
        public double CanvasWidth => IsPortraitMode ? 900d : 1600d;
        public double CanvasHeight => IsPortraitMode ? 1600d : 900d;
        public double BackgroundZoom { get; private set; } = 100;
        public double BackgroundPanX { get; private set; }
        public double BackgroundPanY { get; private set; }
        public double BackgroundDisplayWidth => CanvasWidth * BackgroundZoom / 100d;
        public double BackgroundDisplayHeight => CanvasHeight * BackgroundZoom / 100d;
        public double BackgroundCanvasX => -(BackgroundDisplayWidth - CanvasWidth) * (BackgroundPanX + 100d) / 200d;
        public double BackgroundCanvasY => -(BackgroundDisplayHeight - CanvasHeight) * (BackgroundPanY + 100d) / 200d;
        LiveViewLayout LiveViewLayout => LiveViewLayoutGeometry.Calculate(CanvasWidth, CanvasHeight, (int)LiveViewRotation, LiveViewAreaPercent, LiveViewPositionX, LiveViewPositionY);
        double LiveViewPositionX { get; set; } = 10;
        double LiveViewPositionY { get; set; } = 10;
        public double LiveViewDisplayWidth => LiveViewLayout.Width;
        public double LiveViewDisplayHeight => LiveViewLayout.Height;

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
            IsPortraitMode = configured.CustomerLayoutMode == PhotoBooth.Core.Models.CustomerLayoutMode.Portrait;
            LiveViewPositionX = Clamp(configured.WaitingLiveViewX, 0, 100);
            LiveViewPositionY = Clamp(configured.WaitingLiveViewY, 0, 100);
            LiveViewScaleX = configured.AutoFlip ? -1d : 1d;
            LiveViewRotation = configured.ImageRotationDegrees;
            LiveViewAreaPercent = Clamp(configured.WaitingLiveViewAreaPercent, LiveViewLayoutGeometry.MinimumAreaPercent, LiveViewLayoutGeometry.MaximumAreaPercent);
            LiveViewCanvasX = LiveViewLayout.Left;
            LiveViewCanvasY = LiveViewLayout.Top;
            BackgroundZoom = Clamp(configured.WaitingBackgroundZoom, 100, 300);
            BackgroundPanX = Clamp(configured.WaitingBackgroundPanX, -100, 100);
            BackgroundPanY = Clamp(configured.WaitingBackgroundPanY, -100, 100);
            Raise(nameof(ShowLiveView));
            Raise(nameof(IsPortraitMode));
            Raise(nameof(CanvasWidth));
            Raise(nameof(CanvasHeight));
            Raise(nameof(LiveViewCanvasX));
            Raise(nameof(LiveViewCanvasY));
            Raise(nameof(LiveViewScaleX));
            Raise(nameof(LiveViewRotation));
            Raise(nameof(LiveViewAreaPercent));
            Raise(nameof(LiveViewDisplayWidth));
            Raise(nameof(LiveViewDisplayHeight));
            Raise(nameof(BackgroundZoom));
            Raise(nameof(BackgroundPanX));
            Raise(nameof(BackgroundPanY));
            Raise(nameof(BackgroundDisplayWidth));
            Raise(nameof(BackgroundDisplayHeight));
            Raise(nameof(BackgroundCanvasX));
            Raise(nameof(BackgroundCanvasY));

            Raise(nameof(LiveImage));
        }

        private void OnEnter()
        {
            EnterRequested?.Invoke(this, EventArgs.Empty);
        }

        private static double Clamp(double value, double minimum, double maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));

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
