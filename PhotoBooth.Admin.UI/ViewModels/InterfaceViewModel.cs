using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
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
        readonly Dictionary<bool, WaitingScreenProfile> drafts = new Dictionary<bool, WaitingScreenProfile>();
        readonly Dictionary<bool, WaitingScreenProfile> loadedProfiles = new Dictionary<bool, WaitingScreenProfile>();
        readonly Dictionary<bool, Guid?> previews = new Dictionary<bool, Guid?>();
        bool busy, applying, hasError;
        public bool HasError { get => hasError; private set => Set(ref hasError, value); }
        public bool IsReady => !busy && drafts.Count == 2;
        public Task Initialization { get; }
        InterfaceAsset selected; string message; bool showLiveView = true, portraitMode; double liveViewX = 10, liveViewY = 10, liveViewAreaPercent = 5, backgroundZoom = 100, backgroundPanX, backgroundPanY; int imageRotation;
        public InterfaceViewModel(IInterfaceAssetService assetService, IFileDialogService files, ISettingsService settingsService, ILogger<InterfaceViewModel> logger)
        {
            assets = assetService; dialog = files; settings = settingsService; log = logger;
            ImportCommand = new AsyncCommand(_ => Import()); SelectCommand = new AsyncCommand(_ => Select(), _ => SelectedAsset != null); UseDefaultCommand = new AsyncCommand(_ => UseDefault()); SaveLiveViewCommand = new AsyncCommand(_ => SaveLiveView(), _ => IsReady && HasChanges); ResetLiveViewCommand = new AsyncCommand(_ => ResetLiveView()); DiscardCommand = new RelayCommand(_ => Discard(), _ => IsReady && HasChanges);
            PropertyChanged += (_, e) => { if (!applying && new[] { nameof(SelectedAsset), nameof(ShowLiveView), nameof(LiveViewX), nameof(LiveViewY), nameof(LiveViewAreaPercent), nameof(BackgroundZoom), nameof(BackgroundPanX), nameof(BackgroundPanY) }.Contains(e.PropertyName)) { Raise(nameof(HasChanges)); Raise(nameof(SaveStatus)); CommandManager.InvalidateRequerySuggested(); } };
            Initialization = Load();
        }
        public override string Title => "Giao diện màn hình chờ";
        public ICommand DiscardCommand { get; }
        static readonly Guid UploadId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        public ObservableCollection<InterfaceAsset> BackgroundChoices { get; } = new ObservableCollection<InterfaceAsset>();
        public InterfaceAsset SelectedBackground { get => SelectedAsset ?? BackgroundChoices.FirstOrDefault(); set { if (value == null) return; if (value.Id == UploadId) { ImportCommand.Execute(null); Raise(nameof(SelectedBackground)); } else SelectedAsset = value?.Id == Guid.Empty ? null : value; } }
        void RefreshChoices() { BackgroundChoices.Clear(); BackgroundChoices.Add(new InterfaceAsset { Id = Guid.Empty, Name = "Miu mặc định", FilePath = DefaultBackgroundPath }); foreach (var item in Assets) BackgroundChoices.Add(item); BackgroundChoices.Add(new InterfaceAsset { Id = UploadId, Name = "＋ Thêm ảnh hoặc GIF" }); Raise(nameof(SelectedBackground)); }
        public string BackgroundTitle => "Ảnh nền " + (IsPortraitMode ? "dọc" : "ngang");
        public string SaveLabel => "Lưu giao diện " + (IsPortraitMode ? "dọc" : "ngang");
        public string SaveStatus => HasChanges ? "Có thay đổi chưa lưu" : "Đã lưu giao diện " + (IsPortraitMode ? "dọc" : "ngang");
        public bool HasChanges
        {
            get
            {
                if (applying || !loadedProfiles.TryGetValue(IsPortraitMode, out var saved)) return false;
                return saved.BackgroundAssetId != SelectedAsset?.Id || saved.ShowLiveView != ShowLiveView || saved.LiveViewX != LiveViewX || saved.LiveViewY != LiveViewY || saved.LiveViewAreaPercent != LiveViewAreaPercent || saved.BackgroundZoom != BackgroundZoom || saved.BackgroundPanX != BackgroundPanX || saved.BackgroundPanY != BackgroundPanY;
            }
        }
        void Discard() { if (!IsReady) return; drafts[IsPortraitMode] = loadedProfiles[IsPortraitMode].Copy(); previews[IsPortraitMode] = drafts[IsPortraitMode].BackgroundAssetId; ApplyDraft(); }

        public ObservableCollection<InterfaceAsset> Assets { get; } = new ObservableCollection<InterfaceAsset>();
        public InterfaceAsset SelectedAsset { get => selected; set { if (Set(ref selected, value)) { Raise(nameof(SelectedBackground)); CommandManager.InvalidateRequerySuggested(); } } }
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
        public bool IsPortraitMode
        {
            get => portraitMode;
            set
            {
                if (!IsReady || portraitMode == value) return;
                StoreDraft();
                portraitMode = value;
                ApplyDraft();
            }
        }
        public bool IsLandscapeMode { get => !IsPortraitMode; set { if (value) IsPortraitMode = false; } }
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
        public string DefaultBackgroundPath => IsPortraitMode ? "/PhotoBooth.Customer.UI;component/Assets/default-waiting-portrait.png" : "/PhotoBooth.Customer.UI;component/Assets/default-waiting-landscape.png";
        public string PreviewOrientationText => IsPortraitMode ? "Dọc 9:16" : "Ngang 16:9";
        public ICommand ImportCommand { get; }
        public ICommand SelectCommand { get; }
        public ICommand UseDefaultCommand { get; }
        public ICommand SaveLiveViewCommand { get; }
        public ICommand ResetLiveViewCommand { get; }
        public Task RefreshAsync()
        {
            if (!IsReady) return Initialization ?? Task.CompletedTask;
            StoreDraft();
            // Returning to the editor must not discard either orientation's unsaved work.
            foreach (var pair in drafts)
            {
                var a = pair.Value; var b = loadedProfiles[pair.Key];
                if (a.BackgroundAssetId != b.BackgroundAssetId || a.ShowLiveView != b.ShowLiveView || a.LiveViewX != b.LiveViewX || a.LiveViewY != b.LiveViewY || a.LiveViewAreaPercent != b.LiveViewAreaPercent || a.BackgroundZoom != b.BackgroundZoom || a.BackgroundPanX != b.BackgroundPanX || a.BackgroundPanY != b.BackgroundPanY) return Task.CompletedTask;
            }
            return Load();
        }
        async Task Load()
        {
            SetBusy(true);
            try
            {
                var list = await assets.GetAllAsync(CancellationToken.None);
                var saved = await settings.GetAsync(CancellationToken.None);
                var legacy = list.FirstOrDefault(x => x.IsSelected)?.Id;
                drafts[false] = saved.GetWaitingScreen(false, legacy);
                drafts[true] = saved.GetWaitingScreen(true, legacy);
                loadedProfiles[false] = drafts[false].Copy(); loadedProfiles[true] = drafts[true].Copy();
                previews[false] = drafts[false].BackgroundAssetId;
                previews[true] = drafts[true].BackgroundAssetId;
                Assets.Clear(); foreach (var asset in list) Assets.Add(asset);
                ImageRotation = saved.ImageRotationDegrees;
                ApplyDraft();
            }
            catch (Exception e) { Fail(e, "Không thể tải giao diện màn hình chờ."); }
            finally { SetBusy(false); }
        }
        void StoreDraft()
        {
            if (!drafts.TryGetValue(IsPortraitMode, out var draft)) return;
            draft.BackgroundAssetId = SelectedAsset?.Id;
            draft.ShowLiveView = ShowLiveView;
            draft.LiveViewX = LiveViewX; draft.LiveViewY = LiveViewY; draft.LiveViewAreaPercent = LiveViewAreaPercent;
            draft.BackgroundZoom = BackgroundZoom; draft.BackgroundPanX = BackgroundPanX; draft.BackgroundPanY = BackgroundPanY;
            previews[IsPortraitMode] = SelectedAsset?.Id;
        }
        void ApplyDraft()
        {
            if (!drafts.TryGetValue(IsPortraitMode, out var draft)) return;
            applying = true;
            ShowLiveView = draft.ShowLiveView; LiveViewX = draft.LiveViewX; LiveViewY = draft.LiveViewY;
            LiveViewAreaPercent = draft.LiveViewAreaPercent; BackgroundZoom = draft.BackgroundZoom;
            BackgroundPanX = draft.BackgroundPanX; BackgroundPanY = draft.BackgroundPanY;
            // Rebind rows so the active badge belongs to the selected orientation.
            var list = Assets.ToArray(); Assets.Clear();
            foreach (var asset in list) { asset.IsSelected = asset.Id == draft.BackgroundAssetId; Assets.Add(asset); }
            SelectedAsset = Find(previews[IsPortraitMode]);
            Raise(nameof(IsPortraitMode)); Raise(nameof(IsLandscapeMode)); Raise(nameof(IsQuarterTurn));
            Raise(nameof(PreviewCanvasWidth)); Raise(nameof(PreviewCanvasHeight)); Raise(nameof(PreviewOrientationText)); Raise(nameof(DefaultBackgroundPath));
            RaiseLiveViewLayout(); RaiseBackgroundLayout(); Message = null; HasError = false; applying = false; Raise(nameof(HasChanges)); Raise(nameof(SaveStatus)); Raise(nameof(SaveLabel)); Raise(nameof(BackgroundTitle)); RefreshChoices(); CommandManager.InvalidateRequerySuggested();
        }
        async Task PersistDraft()
        {
            var saved = await settings.GetAsync(CancellationToken.None);
            // Freeze both legacy fallbacks on the first edit; subsequent edits save only this orientation.
            if (saved.LandscapeWaitingScreen == null) saved.LandscapeWaitingScreen = loadedProfiles[false].Copy();
            if (saved.PortraitWaitingScreen == null) saved.PortraitWaitingScreen = loadedProfiles[true].Copy();
            if (IsPortraitMode) saved.PortraitWaitingScreen = drafts[true].Copy();
            else saved.LandscapeWaitingScreen = drafts[false].Copy();
            await settings.SaveAsync(saved, CancellationToken.None);
            loadedProfiles[IsPortraitMode] = drafts[IsPortraitMode].Copy(); Raise(nameof(HasChanges)); Raise(nameof(SaveStatus));
        }
        Task UseDefault()
        {
            if (IsReady) { SelectedAsset = null; BackgroundZoom = 100; BackgroundPanX = 0; BackgroundPanY = 0; }
            return Task.CompletedTask;
        }
        async Task Import()
        {
            if (!IsReady) return;
            var path = dialog.PickBackground(); if (path == null) return; SetBusy(true);
            try { var asset = await assets.ImportAsync(path, CancellationToken.None); Assets.Add(asset); RefreshChoices(); SelectedAsset = asset; Message = "Đã nhập ảnh nền. Bấm Lưu giao diện để áp dụng."; }
            catch (Exception e) { Fail(e, "Không thể nhập ảnh nền."); }
            finally { SetBusy(false); }
        }
        Task Select() { if (IsReady) StoreDraft(); return Task.CompletedTask; }
        async Task SaveLiveView()
        {
            if (!IsReady) return; SetBusy(true);
            try { HasError = false; StoreDraft(); await PersistDraft(); Message = "Đã lưu bố cục màn hình " + (IsPortraitMode ? "dọc." : "ngang."); }
            catch (Exception e) { Fail(e, "Không thể lưu bố cục màn hình chờ."); }
            finally { SetBusy(false); }
        }
        Task ResetLiveView() { if (IsReady) { LiveViewX = 50; LiveViewY = 50; LiveViewAreaPercent = 5; BackgroundZoom = 100; BackgroundPanX = 0; BackgroundPanY = 0; } return Task.CompletedTask; }
        void SetBusy(bool value) { busy = value; Raise(nameof(IsReady)); CommandManager.InvalidateRequerySuggested(); }
        InterfaceAsset Find(Guid? id) { if (!id.HasValue) return null; foreach (var asset in Assets) if (asset.Id == id.Value) return asset; return null; }
        static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
        void RaiseBackgroundLayout() { Raise(nameof(BackgroundZoomText)); Raise(nameof(PreviewBackgroundWidth)); Raise(nameof(PreviewBackgroundHeight)); Raise(nameof(PreviewBackgroundX)); Raise(nameof(PreviewBackgroundY)); }
        void RaiseLiveViewLayout() { Raise(nameof(LiveViewAreaText)); Raise(nameof(PreviewLiveViewWidth)); Raise(nameof(PreviewLiveViewHeight)); Raise(nameof(PreviewLiveViewX)); Raise(nameof(PreviewLiveViewY)); }
        void Fail(Exception e, string text) { log.LogError(e, text); Message = text; HasError = true; }
    }
}
