using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
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
        Settings configured = new Settings();
        IReadOnlyList<InterfaceAsset> availableAssets = new InterfaceAsset[0];
        double viewportWidth = 1600, viewportHeight = 900;

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

        public object LiveImage => _capture.LiveImage;
        public PhotoBooth.Color.D3D11.LivePreviewLut LivePreset => _capture.LivePreset;
        public int LiveFrameWidth => _capture.LiveFrameWidth;
        public int LiveFrameHeight => _capture.LiveFrameHeight;
        public bool ShowLiveView { get; private set; } = true;
        public double LiveViewCanvasX { get; private set; } = 160;
        public double LiveViewCanvasY { get; private set; } = 90;
        public double LiveViewScaleX { get; private set; } = 1;
        public double LiveViewRotation { get; private set; }
        public double LiveViewAreaPercent { get; private set; } = LiveViewLayoutGeometry.MinimumAreaPercent;
        public bool IsPortraitMode { get; private set; }
        public double CanvasWidth => viewportWidth;
        public double CanvasHeight => viewportHeight;
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
            availableAssets = await _assets.GetAllAsync(CancellationToken.None);
            configured = await _settings.GetAsync(CancellationToken.None) ?? new Settings();
            ApplyProfile();
        }

        // Size changes are presentation-only: never activate, reconnect or restart capture.
        public void UpdateViewport(double width, double height)
        {
            if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height) || double.IsInfinity(width) || double.IsInfinity(height)) return;
            if (Math.Abs(viewportWidth - width) < .1 && Math.Abs(viewportHeight - height) < .1) return;
            viewportWidth = width; viewportHeight = height;
            IsPortraitMode = height > width;
            ApplyProfile();
        }

        void ApplyProfile()
        {
            var profile = configured.GetWaitingScreen(IsPortraitMode, availableAssets.FirstOrDefault(x => x.IsSelected)?.Id);
            BackgroundPath = availableAssets.FirstOrDefault(x => x.Id == profile.BackgroundAssetId)?.FilePath;
            ShowLiveView = profile.ShowLiveView;
            LiveViewPositionX = Clamp(profile.LiveViewX, 0, 100);
            LiveViewPositionY = Clamp(profile.LiveViewY, 0, 100);
            LiveViewScaleX = configured.AutoFlip ? -1d : 1d;
            LiveViewRotation = configured.ImageRotationDegrees;
            LiveViewAreaPercent = Clamp(profile.LiveViewAreaPercent, LiveViewLayoutGeometry.MinimumAreaPercent, LiveViewLayoutGeometry.MaximumAreaPercent);
            LiveViewCanvasX = LiveViewLayout.Left;
            LiveViewCanvasY = LiveViewLayout.Top;
            BackgroundZoom = Clamp(profile.BackgroundZoom, 100, 300);
            BackgroundPanX = Clamp(profile.BackgroundPanX, -100, 100);
            BackgroundPanY = Clamp(profile.BackgroundPanY, -100, 100);
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
            Raise(nameof(LivePreset));
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
            if (e.PropertyName == nameof(CaptureViewModel.LivePreset)) Raise(nameof(LivePreset));
            if (e.PropertyName == nameof(CaptureViewModel.LiveImage))
            {
                Raise(nameof(LiveImage));
                Raise(nameof(LiveFrameWidth));
                Raise(nameof(LiveFrameHeight));
            }
        }
    }
}
