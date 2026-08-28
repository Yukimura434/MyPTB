using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Admin.UI.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class InterfaceViewModel : PageViewModel
    {
        readonly IInterfaceAssetService assets; readonly IFileDialogService dialog; readonly ISettingsService settings; readonly ILogger<InterfaceViewModel> log;
        InterfaceAsset selected; string message; bool showLiveView = true, portraitMode; double liveViewX = 10, liveViewY = 10, liveViewAreaPercent = 5, backgroundZoom = 100, backgroundPanX, backgroundPanY; int imageRotation;
        public InterfaceViewModel(IInterfaceAssetService assetService, IFileDialogService files, ISettingsService settingsService, ILogger<InterfaceViewModel> logger)
        {
            assets = assetService; dialog = files; settings = settingsService; log = logger;
            ImportCommand = new AsyncCommand(_ => Import()); SelectCommand = new AsyncCommand(_ => Select(), _ => SelectedAsset != null); RefreshCommand = new AsyncCommand(_ => Load()); SaveLiveViewCommand = new AsyncCommand(_ => SaveLiveView()); ResetLiveViewCommand = new AsyncCommand(_ => ResetLiveView()); _ = Load();
        }
        public override string Title => "Interface";
        public ObservableCollection<InterfaceAsset> Assets { get; } = new ObservableCollection<InterfaceAsset>();
        public InterfaceAsset SelectedAsset { get => selected; set { if (Set(ref selected, value)) CommandManager.InvalidateRequerySuggested(); } }
        public string Message { get => message; private set => Set(ref message, value); }
        public bool ShowLiveView { get => showLiveView; set => Set(ref showLiveView, value); }
        public double LiveViewX { get => liveViewX; set { if (Set(ref liveViewX, Clamp(value, 0, 100))) RaiseLiveViewLayout(); } }
        public double LiveViewY { get => liveViewY; set { if (Set(ref liveViewY, Clamp(value, 0, 100))) RaiseLiveViewLayout(); } }
        public double LiveViewAreaPercent { get => liveViewAreaPercent; set { if (Set(ref liveViewAreaPercent, Clamp(value, LiveViewLayoutGeometry.MinimumAreaPercent, LiveViewLayoutGeometry.MaximumAreaPercent))) RaiseLiveViewLayout(); } }
        public string LiveViewAreaText => $"{LiveViewAreaPercent:0}% màn hình";
        public double BackgroundZoom { get => backgroundZoom; set { if (Set(ref backgroundZoom, Clamp(value, 100, 300))) RaiseBackgroundLayout(); } }
        public double BackgroundPanX { get => backgroundPanX; set { if (Set(ref backgroundPanX, Clamp(value, -100, 100))) Raise(nameof(PreviewBackgroundX)); } }
        public double BackgroundPanY { get => backgroundPanY; set { if (Set(ref backgroundPanY, Clamp(value, -100, 100))) Raise(nameof(PreviewBackgroundY)); } }
        public string BackgroundZoomText => $"{BackgroundZoom:0}%";
        public bool IsPortraitMode { get => portraitMode; private set => Set(ref portraitMode, value); }
        public int ImageRotation { get => imageRotation; private set => Set(ref imageRotation, value); }
        public bool IsQuarterTurn => ImageRotation == 90 || ImageRotation == -90;
        public double PreviewCanvasWidth => IsPortraitMode ? 900d : 1600d;
        public double PreviewCanvasHeight => IsPortraitMode ? 1600d : 900d;
        LiveViewLayout PreviewLiveViewLayout => LiveViewLayoutGeometry.Calculate(PreviewCanvasWidth, PreviewCanvasHeight, ImageRotation, LiveViewAreaPercent, LiveViewX, LiveViewY);
        public double PreviewLiveViewWidth => PreviewLiveViewLayout.Width;
        public double PreviewLiveViewHeight => PreviewLiveViewLayout.Height;
        public double PreviewLiveViewX => PreviewLiveViewLayout.Left;
        public double PreviewLiveViewY => PreviewLiveViewLayout.Top;
        public double PreviewBackgroundWidth => PreviewCanvasWidth * BackgroundZoom / 100d;
        public double PreviewBackgroundHeight => PreviewCanvasHeight * BackgroundZoom / 100d;
        public double PreviewBackgroundX => -(PreviewBackgroundWidth - PreviewCanvasWidth) * (BackgroundPanX + 100d) / 200d;
        public double PreviewBackgroundY => -(PreviewBackgroundHeight - PreviewCanvasHeight) * (BackgroundPanY + 100d) / 200d;
        public string PreviewOrientationText => IsPortraitMode ? "MÀN HÌNH DỌC" : "MÀN HÌNH NGANG";
        public ICommand ImportCommand { get; } public ICommand SelectCommand { get; } public ICommand RefreshCommand { get; } public ICommand SaveLiveViewCommand { get; } public ICommand ResetLiveViewCommand { get; }
        public Task RefreshAsync() => Load();
        async Task Load()
        {
            try { Assets.Clear(); foreach (var asset in await assets.GetAllAsync(CancellationToken.None)) Assets.Add(asset); if (SelectedAsset == null) SelectedAsset = Assets.Count > 0 ? Assets[0] : null; var saved = await settings.GetAsync(CancellationToken.None); ShowLiveView = saved.ShowWaitingLiveView; IsPortraitMode = saved.CustomerLayoutMode == CustomerLayoutMode.Portrait; ImageRotation = saved.ImageRotationDegrees; LiveViewX = saved.WaitingLiveViewX; LiveViewY = saved.WaitingLiveViewY; LiveViewAreaPercent = saved.WaitingLiveViewAreaPercent; BackgroundZoom = saved.WaitingBackgroundZoom; BackgroundPanX = saved.WaitingBackgroundPanX; BackgroundPanY = saved.WaitingBackgroundPanY; Raise(nameof(IsQuarterTurn)); Raise(nameof(PreviewCanvasWidth)); Raise(nameof(PreviewCanvasHeight)); Raise(nameof(PreviewOrientationText)); RaiseLiveViewLayout(); RaiseBackgroundLayout(); }
            catch (Exception e) { Fail(e, "Could not load interface backgrounds"); }
        }
        async Task Import() { var path = dialog.PickBackground(); if (path == null) return; try { SelectedAsset = await assets.ImportAsync(path, CancellationToken.None); Message = "Background imported"; await Load(); SelectedAsset = Find(SelectedAsset?.Id); } catch (Exception e) { Fail(e, e.Message); } }
        async Task Select() { try { var id = SelectedAsset.Id; await assets.SelectAsync(id, CancellationToken.None); Message = "Selected background saved to SQLite"; await Load(); SelectedAsset = Find(id); } catch (Exception e) { Fail(e, "Could not select background"); } }
        async Task SaveLiveView() { try { var saved = await settings.GetAsync(CancellationToken.None); saved.ShowWaitingLiveView = ShowLiveView; saved.WaitingLiveViewX = LiveViewX; saved.WaitingLiveViewY = LiveViewY; saved.WaitingLiveViewAreaPercent = LiveViewAreaPercent; saved.WaitingBackgroundZoom = BackgroundZoom; saved.WaitingBackgroundPanX = BackgroundPanX; saved.WaitingBackgroundPanY = BackgroundPanY; await settings.SaveAsync(saved, CancellationToken.None); Message = "Đã lưu bố cục màn hình chờ"; } catch (Exception e) { Fail(e, "Không thể lưu bố cục màn hình chờ"); } }
        async Task ResetLiveView() { ShowLiveView = true; LiveViewX = 10; LiveViewY = 10; LiveViewAreaPercent = 5; BackgroundZoom = 100; BackgroundPanX = 0; BackgroundPanY = 0; await SaveLiveView(); }
        InterfaceAsset Find(Guid? id) { if (!id.HasValue) return null; foreach (var asset in Assets) if (asset.Id == id.Value) return asset; return null; }
        static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
        void RaiseBackgroundLayout() { Raise(nameof(BackgroundZoomText)); Raise(nameof(PreviewBackgroundWidth)); Raise(nameof(PreviewBackgroundHeight)); Raise(nameof(PreviewBackgroundX)); Raise(nameof(PreviewBackgroundY)); }
        void RaiseLiveViewLayout() { Raise(nameof(LiveViewAreaText)); Raise(nameof(PreviewLiveViewWidth)); Raise(nameof(PreviewLiveViewHeight)); Raise(nameof(PreviewLiveViewX)); Raise(nameof(PreviewLiveViewY)); }
        void Fail(Exception e, string text) { log.LogError(e, text); Message = text; }
    }
}
