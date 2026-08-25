using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Admin.UI.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class PresetManagerViewModel : PageViewModel
    {
        readonly IPresetService service;
        readonly IColorLutService color;
        readonly IPresetColorRepository presetColors;
        readonly ISettingsService settingsService;
        readonly IFileDialogService dialogs;
        readonly ILogger<PresetManagerViewModel> log;
        Preset selected;
        ColorLutAsset selectedLut;
        string createName = "Preset mới";
        string editName = string.Empty;
        string message;
        double lutStrength = 1;

        public PresetManagerViewModel(IPresetService s, IColorLutService c, IPresetColorRepository pc,
            ISettingsService settings, IFileDialogService d, ILogger<PresetManagerViewModel> l)
        {
            service = s; color = c; presetColors = pc; settingsService = settings; dialogs = d; log = l;
            CreateCommand = new AsyncCommand(_ => Create());
            DeleteCommand = new AsyncCommand(_ => Delete(), _ => SelectedPreset != null);
            DuplicateCommand = new AsyncCommand(_ => Duplicate(), _ => SelectedPreset != null);
            RenameCommand = new AsyncCommand(_ => Rename(), _ => SelectedPreset != null && !string.IsNullOrWhiteSpace(EditName));
            DefaultCommand = new AsyncCommand(_ => SetDefault(), _ => SelectedPreset != null && !SelectedPreset.IsDefault);
            ClearDefaultCommand = new AsyncCommand(_ => ClearDefault(), _ => Presets.Any(x => x.IsDefault));
            ImportLutCommand = new AsyncCommand(_ => ImportLut());
            AttachLutCommand = new AsyncCommand(_ => AttachLut(), _ => SelectedPreset != null && SelectedLut != null);
            DetachLutCommand = new AsyncCommand(_ => DetachLut(), _ => SelectedPreset != null && SelectedLut != null);
            DeleteLutCommand = new AsyncCommand(_ => DeleteLut(), _ => SelectedLut != null);
            _ = Load();
        }

        public override string Title => "Preset Manager";
        public ObservableCollection<Preset> Presets { get; } = new ObservableCollection<Preset>();
        public ObservableCollection<ColorLutAsset> Luts { get; } = new ObservableCollection<ColorLutAsset>();

        public Preset SelectedPreset
        {
            get => selected;
            set
            {
                if (!Set(ref selected, value)) return;
                EditName = value?.Name ?? string.Empty;
                Message = null;
                Raise(nameof(HasSelection));
                _ = LoadPresetColor();
            }
        }

        public ColorLutAsset SelectedLut
        {
            get => selectedLut;
            set
            {
                if (!Set(ref selectedLut, value)) return;
                Raise(nameof(LutCubeSizeText));
                Raise(nameof(LutStatusText));
                Raise(nameof(LutLiveViewText));
            }
        }

        public string CreateName { get => createName; set => Set(ref createName, value); }
        public string EditName { get => editName; set => Set(ref editName, value); }
        public string Message { get => message; set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public bool HasSelection => SelectedPreset != null;
        public string PresetSummary => Presets.Count == 0 ? "Chưa có preset" : Presets.Count + " preset";
        public string LutCubeSizeText => SelectedLut == null ? "—" : SelectedLut.CubeSize + "³";
        public string LutStatusText => SelectedLut == null ? "Chưa chọn" : SelectedLut.Status.ToString();
        public string LutLiveViewText => SelectedLut == null ? "—" : (SelectedLut.SupportsLiveView ? "Hỗ trợ" : "Chỉ ảnh chụp");
        public string LutStrengthPercent => Math.Round(LutStrength * 100) + "%";
        public double LutStrength
        {
            get => lutStrength;
            set { if (Set(ref lutStrength, Math.Max(0, Math.Min(1, value)))) Raise(nameof(LutStrengthPercent)); }
        }

        public ICommand CreateCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand RenameCommand { get; }
        public ICommand DefaultCommand { get; }
        public ICommand ClearDefaultCommand { get; }
        public ICommand ImportLutCommand { get; }
        public ICommand AttachLutCommand { get; }
        public ICommand DetachLutCommand { get; }
        public ICommand DeleteLutCommand { get; }

        async Task Load(Guid? selectId = null)
        {
            var id = selectId ?? SelectedPreset?.Id;
            Presets.Clear();
            foreach (var x in (await service.GetAllAsync(CancellationToken.None)).OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name))
                Presets.Add(x);
            Raise(nameof(PresetSummary));
            await LoadLuts();
            SelectedPreset = id.HasValue ? Presets.FirstOrDefault(x => x.Id == id.Value) : Presets.FirstOrDefault();
        }

        async Task LoadLuts()
        {
            var selectedId = SelectedLut?.Id;
            Luts.Clear();
            foreach (var x in (await color.GetAllAsync(CancellationToken.None)).OrderBy(x => x.DisplayName)) Luts.Add(x);
            SelectedLut = selectedId.HasValue ? Luts.FirstOrDefault(x => x.Id == selectedId) : null;
        }

        async Task LoadPresetColor()
        {
            if (SelectedPreset == null) { SelectedLut = null; LutStrength = 1; return; }
            var presetId = SelectedPreset.Id;
            var settings = await presetColors.GetAsync(presetId, CancellationToken.None);
            if (SelectedPreset?.Id != presetId) return;
            SelectedLut = settings?.LutAssetId.HasValue == true ? Luts.FirstOrDefault(x => x.Id == settings.LutAssetId.Value) : null;
            LutStrength = settings?.Strength ?? 1;
        }

        async Task ImportLut()
        {
            var file = dialogs.PickCube();
            if (string.IsNullOrWhiteSpace(file)) return;
            await Guard(async () =>
            {
                Message = "Đang kiểm tra và nhập LUT…";
                var result = await color.ImportAsync(file, Path.GetFileNameWithoutExtension(file), CancellationToken.None);
                await LoadLuts();
                SelectedLut = Luts.FirstOrDefault(x => x.Id == result.Asset.Id);
                Message = result.WasDuplicate ? "LUT đã tồn tại; hệ thống đã chọn bản có sẵn." : (result.Warnings.FirstOrDefault() ?? "Đã nhập LUT thành công.");
            }, "Không thể nhập LUT");
        }

        async Task AttachLut() => await Guard(async () =>
        {
            await color.AttachAsync(SelectedPreset.Id, SelectedLut.Id, (float)LutStrength, CancellationToken.None);
            Message = SelectedLut.SupportsLiveView ? "Đã áp dụng LUT cho preset và Live View." : "Đã áp dụng LUT cho ảnh chụp; kích thước LUT này không hỗ trợ Live View.";
        }, "Không thể áp dụng LUT");

        async Task DetachLut() => await Guard(async () =>
        {
            await color.DetachAsync(SelectedPreset.Id, CancellationToken.None);
            SelectedLut = null;
            Message = "Đã gỡ LUT khỏi preset.";
        }, "Không thể gỡ LUT");

        async Task DeleteLut()
        {
            var target = SelectedLut;
            await Guard(async () =>
            {
                await color.DeleteAsync(target.Id, target.RowVersion, CancellationToken.None);
                SelectedLut = null;
                await LoadLuts();
                Message = "Đã xóa LUT khỏi thư viện.";
            }, "Không thể xóa LUT");
        }

        async Task Create()
        {
            var now = DateTime.UtcNow;
            var item = new Preset { Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(CreateName) ? "Preset mới" : CreateName.Trim(), CreatedAtUtc = now, ModifiedAtUtc = now, CaptureCountdownSeconds = 3 };
            await Guard(async () => { await service.SaveAsync(item, CancellationToken.None); await Load(item.Id); CreateName = "Preset mới"; Message = "Đã tạo preset."; }, "Không thể tạo preset");
        }

        async Task Delete()
        {
            var target = SelectedPreset;
            await Guard(async () => { await service.DeleteAsync(target.Id, CancellationToken.None); await Load(); Message = "Đã xóa preset “" + target.Name + "”."; }, "Không thể xóa preset");
        }

        async Task Duplicate()
        {
            var now = DateTime.UtcNow; var x = SelectedPreset;
            var copy = new Preset { Id = Guid.NewGuid(), Name = x.Name + " - Bản sao", CreatedAtUtc = now, ModifiedAtUtc = now, FrameId = x.FrameId, PrinterProfileId = x.PrinterProfileId, CaptureCountdownSeconds = x.CaptureCountdownSeconds, SettingsJson = x.SettingsJson };
            await Guard(async () => { await service.SaveAsync(copy, CancellationToken.None); await Load(copy.Id); Message = "Đã tạo bản sao preset."; }, "Không thể nhân bản preset");
        }

        async Task Rename()
        {
            var target = SelectedPreset; var name = EditName.Trim();
            await Guard(async () => { target.Name = name; target.ModifiedAtUtc = DateTime.UtcNow; await service.SaveAsync(target, CancellationToken.None); await Load(target.Id); Message = "Đã lưu tên preset."; }, "Không thể đổi tên preset");
        }

        async Task SetDefault()
        {
            var target = SelectedPreset;
            await Guard(async () =>
            {
                foreach (var x in Presets.Where(x => x.IsDefault && x.Id != target.Id)) { x.IsDefault = false; await service.SaveAsync(x, CancellationToken.None); }
                target.IsDefault = true; target.ModifiedAtUtc = DateTime.UtcNow; await service.SaveAsync(target, CancellationToken.None);
                var workflow = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
                workflow.DefaultPresetId = target.Id; await settingsService.SaveAsync(workflow, CancellationToken.None);
                await Load(target.Id); Message = "Preset này hiện là mặc định cho Live View và phiên chụp.";
            }, "Không thể đặt preset mặc định");
        }

        async Task ClearDefault()
        {
            await Guard(async () =>
            {
                var selectedId = SelectedPreset?.Id;
                foreach (var preset in Presets.Where(x => x.IsDefault).ToList())
                {
                    preset.IsDefault = false;
                    preset.ModifiedAtUtc = DateTime.UtcNow;
                    await service.SaveAsync(preset, CancellationToken.None);
                }

                var workflow = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
                workflow.DefaultPresetId = null;
                await settingsService.SaveAsync(workflow, CancellationToken.None);
                await Load(selectedId);
                Message = "Đã tắt preset mặc định. Live View và ảnh chụp sẽ không tự động áp dụng preset.";
            }, "Không thể tắt preset mặc định");
        }

        async Task Guard(Func<Task> action, string text)
        {
            try { await action(); }
            catch (Exception e) { log.LogError(e, text); Message = text + ": " + e.Message; }
        }
    }
}
