using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class BeautyViewModel : PageViewModel
    {
        readonly IBeautySettingsService service;
        readonly IBeautyRetouchService retouch;
        readonly ILogger<BeautyViewModel> log;
        bool enabled, busy, loading, dirty, previewBusy, showBefore, advanced;
        int smooth, brighten, tone, sharpen, blemish, eye, slim, level;
        string message, status;
        byte[] preview, originalPreview;
        CancellationTokenSource previewCts;

        public BeautyViewModel(IBeautySettingsService s, IBeautyRetouchService r, ILogger<BeautyViewModel> l)
        {
            service = s; retouch = r; log = l;
            SaveCommand = new AsyncCommand(_ => Save(), _ => !IsBusy && HasUnsavedChanges);
            ResetCommand = new AsyncCommand(_ => Reset(), _ => !IsBusy);
            ReloadCommand = new AsyncCommand(_ => Load(), _ => !IsBusy);
            ToggleAdvancedCommand = new RelayCommand(_ => IsAdvanced = !IsAdvanced);
            LoadOriginalPreview(); _ = Load();
        }
        public override string Title => "Làm đẹp";
        public bool Enabled { get => enabled; set { if (Set(ref enabled, value)) { Raise(nameof(IsEditorEnabled)); Changed(); Raise(nameof(DisplayedPreviewBytes)); } } }
        public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Raise(nameof(IsEditorEnabled)); Raise(nameof(CanEdit)); } } }
        public bool CanEdit => !IsBusy;
        public bool IsEditorEnabled => Enabled && !IsBusy;
        public bool HasUnsavedChanges { get => dirty; private set { if (Set(ref dirty, value)) CommandManager.InvalidateRequerySuggested(); } }
        public bool IsPreviewBusy { get => previewBusy; private set => Set(ref previewBusy, value); }
        public byte[] PreviewBytes { get => preview; private set { if (Set(ref preview, value)) Raise(nameof(DisplayedPreviewBytes)); } }
        public byte[] DisplayedPreviewBytes => showBefore || !Enabled ? originalPreview : preview ?? originalPreview;
        public bool ShowBefore { get => showBefore; private set { if (Set(ref showBefore, value)) Raise(nameof(DisplayedPreviewBytes)); } }
        public bool IsAdvanced { get => advanced; set { if (Set(ref advanced, value)) Raise(nameof(AdvancedButtonText)); } }
        public string AdvancedButtonText => IsAdvanced ? "Thu gọn" : "Nâng cao";
        public bool IsCustom => Current().Level == null;
        public string LevelText => IsCustom ? "Tùy chỉnh" : Level + " / 10";
        public string StrengthText => IsCustom ? "Đang dùng các mức riêng trong Nâng cao." : Level == 0 ? "Chưa áp dụng hiệu ứng" : "Mức " + Level + (Level <= 3 ? " · Nhẹ nhàng" : Level <= 6 ? " · Vừa phải" : " · Rõ hơn");
        public int Level
        {
            get => level;
            set
            {
                int bounded = Math.Max(0, Math.Min(10, value));
                if (bounded == level && !IsCustom) return;
                var settings = Current(); settings.SetLevel(bounded);
                bool wasLoading = loading; loading = true;
                try { Apply(settings); } finally { loading = wasLoading; }
                Changed();
            }
        }
        public int SmoothSkin { get => smooth; set { if (Set(ref smooth, Math.Min(BeautySettings.MaxSmoothSkin, Clamp(value)))) Changed(); } }
        public int BrightenSkin { get => brighten; set { if (Set(ref brighten, Math.Min(BeautySettings.MaxBrightenSkin, Clamp(value)))) Changed(); } }
        public int SkinTone { get => tone; set { if (Set(ref tone, Math.Min(BeautySettings.MaxSkinTone, Clamp(value)))) Changed(); } }
        public int Sharpen { get => sharpen; set { if (Set(ref sharpen, Clamp(value))) Changed(); } }
        public int BlemishRemoval { get => blemish; set { if (Set(ref blemish, Math.Min(BeautySettings.MaxBlemishRemoval, Clamp(value)))) Changed(); } }
        public int EyeSize { get => eye; set { if (Set(ref eye, Math.Min(BeautySettings.MaxGeometry, Clamp(value)))) Changed(); } }
        public int SlimFace { get => slim; set { if (Set(ref slim, Math.Min(BeautySettings.MaxGeometry, Clamp(value)))) Changed(); } }
        public string Message { get => message; private set => Set(ref message, value); }
        public string RuntimeStatus { get => status; private set => Set(ref status, value); }
        public ICommand SaveCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand ReloadCommand { get; }
        public ICommand ToggleAdvancedCommand { get; }
        public Task RefreshAsync() => Load();

        async Task Load()
        {
            IsBusy = true; loading = true;
            try
            {
                Apply(await service.GetAsync(CancellationToken.None)); HasUnsavedChanges = false;
                var a = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Beauty");
                RuntimeStatus = File.Exists(Path.Combine(a, "face_detection_yunet_2023mar.onnx")) && File.Exists(Path.Combine(a, "lbfmodel.yaml")) ? "Sẵn sàng làm đẹp" : "Chưa có dữ liệu làm đẹp";
                Message = null;
            }
            catch (Exception e) { Fail(e, "Không thể tải cấu hình làm đẹp."); }
            finally { loading = false; IsBusy = false; RefreshLevel(); QueuePreview(); }
        }
        async Task Save()
        {
            IsBusy = true;
            try { await service.SaveAsync(Current(), CancellationToken.None); HasUnsavedChanges = false; Message = Enabled ? "Đã lưu và áp dụng làm đẹp." : "Đã tắt làm đẹp."; }
            catch (Exception e) { Fail(e, "Không thể lưu cấu hình làm đẹp."); }
            finally { IsBusy = false; }
        }
        async Task Reset() { loading = true; Apply(new BeautySettings()); loading = false; Changed(); await Save(); }
        BeautySettings Current() => new BeautySettings { Enabled = Enabled, SmoothSkin = SmoothSkin, BrightenSkin = BrightenSkin, SkinTone = SkinTone, Sharpen = Sharpen, EyeSize = EyeSize, SlimFace = SlimFace, BlemishRemoval = BlemishRemoval };
        void Apply(BeautySettings x)
        {
            x = x?.Clone() ?? new BeautySettings(); x.ClampStrengths(); Enabled = x.Enabled; SmoothSkin = x.SmoothSkin; BrightenSkin = x.BrightenSkin; SkinTone = x.SkinTone;
            Sharpen = x.Sharpen; EyeSize = x.EyeSize; SlimFace = x.SlimFace; BlemishRemoval = x.BlemishRemoval;
            level = x.Level ?? Math.Max(0, Math.Min(10, x.SmoothSkin / 6)); RefreshLevel();
        }
        void RefreshLevel() { Raise(nameof(Level)); Raise(nameof(IsCustom)); Raise(nameof(LevelText)); Raise(nameof(StrengthText)); }
        void Changed() { if (loading) return; RefreshLevel(); HasUnsavedChanges = true; Message = "Có thay đổi chưa lưu."; QueuePreview(); }
        void LoadOriginalPreview()
        {
            try { var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "beauty-model.png"); if (File.Exists(path)) { originalPreview = File.ReadAllBytes(path); Raise(nameof(DisplayedPreviewBytes)); } }
            catch (Exception e) { log.LogWarning(e, "Unable to load Beauty sample."); }
        }
        public void SetBeforePreview(bool value) { ShowBefore = value; }
        async void QueuePreview()
        {
            var c = new CancellationTokenSource(); var previous = Interlocked.Exchange(ref previewCts, c);
            if (previous != null) try { previous.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                await Task.Delay(180, c.Token);
                var input = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "beauty-model.png");
                if (!Enabled || !File.Exists(input)) { PreviewBytes = originalPreview; return; }
                IsPreviewBusy = true;
                var output = Path.Combine(Path.GetTempPath(), "beauty-preview-" + Guid.NewGuid().ToString("N") + ".jpg");
                try { await retouch.ProcessAsync(input, output, Current(), c.Token); if (!c.IsCancellationRequested && ReferenceEquals(previewCts, c)) PreviewBytes = File.ReadAllBytes(output); }
                finally { try { if (File.Exists(output)) File.Delete(output); } catch { } }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.LogWarning(e, "Unable to render Beauty sample."); }
            finally { if (ReferenceEquals(Interlocked.CompareExchange(ref previewCts, null, c), c)) IsPreviewBusy = false; c.Dispose(); }
        }
        void Fail(Exception e, string text) { log.LogError(e, text); Message = text; }
        static int Clamp(int x) => Math.Max(0, Math.Min(100, x));
    }
}
