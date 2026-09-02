using System;
using System.Collections.Generic;
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
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class PresetEventItem
    {
        public Guid? Id { get; set; }
        public string Name { get; set; }
        public int PresetCount { get; set; }
        public bool IsAll { get; set; }
        public bool IsUncategorized { get; set; }
    }

    public sealed class PresetManagerViewModel : PageViewModel
    {
        readonly IPresetService service;
        readonly IPresetEventService eventService;
        readonly IColorLutService color;
        readonly ISettingsService settingsService;
        readonly IFileDialogService dialogs;
        readonly ILogger<PresetManagerViewModel> log;
        readonly string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model.png");
        Preset selected;
        PresetEventItem selectedEvent;
        PresetEventItem assignmentEvent;
        string search = string.Empty;
        string sort = "Ghim trước";
        string editName = string.Empty;
        string newEventName = "Sự kiện mới";
        string editEventName = string.Empty;
        string message;
        double lutStrength = ColorLutData.DefaultStrength;
        byte[] previewBytes;
        bool isPreviewBusy;
        CancellationTokenSource previewCancellation;

        public PresetManagerViewModel(IPresetService presets, IPresetEventService events, IColorLutService colors,
            ISettingsService settings, IFileDialogService dialog, ILogger<PresetManagerViewModel> logger)
        {
            service = presets; eventService = events; color = colors; settingsService = settings; dialogs = dialog; log = logger;
            ImportLutCommand = new AsyncCommand(_ => ImportLut());
            DeleteCommand = new AsyncCommand(_ => Delete(), _ => SelectedPreset != null);
            RenameCommand = new AsyncCommand(_ => Rename(), _ => SelectedPreset != null && !string.IsNullOrWhiteSpace(EditName));
            PinCommand = new AsyncCommand(_ => Pin(), _ => SelectedPreset != null);
            CreateEventCommand = new AsyncCommand(_ => CreateEvent(), _ => !string.IsNullOrWhiteSpace(NewEventName));
            RenameEventCommand = new AsyncCommand(_ => RenameEvent(), _ => CanManageSelectedEvent && !string.IsNullOrWhiteSpace(EditEventName));
            DeleteEventCommand = new AsyncCommand(_ => DeleteEvent(), _ => CanManageSelectedEvent);
            AssignEventCommand = new AsyncCommand(_ => AssignEvent(), _ => SelectedPreset != null && AssignmentEvent != null && !AssignmentEvent.IsAll);
            SortOptions = new[] { "Ghim trước", "Mới nhất", "Tên A–Z" };
            LoadModelPreview();
            _ = Load();
        }

        public override string Title => "Quản lý preset";
        public ObservableCollection<Preset> Presets { get; } = new ObservableCollection<Preset>();
        public ObservableCollection<Preset> VisiblePresets { get; } = new ObservableCollection<Preset>();
        public ObservableCollection<PresetEventItem> EventItems { get; } = new ObservableCollection<PresetEventItem>();
        public ObservableCollection<PresetEventItem> AssignmentEvents { get; } = new ObservableCollection<PresetEventItem>();
        public string[] SortOptions { get; }

        public string Search { get => search; set { if (Set(ref search, value)) Apply(); } }
        public string Sort { get => sort; set { if (Set(ref sort, value)) Apply(); } }
        public string EditName { get => editName; set => Set(ref editName, value); }
        public string NewEventName { get => newEventName; set => Set(ref newEventName, value); }
        public string EditEventName { get => editEventName; set => Set(ref editEventName, value); }
        public string Message { get => message; set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public bool HasSelection => SelectedPreset != null;
        public bool CanManageSelectedEvent => SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized;
        public string PresetSummary => VisiblePresets.Count + " / " + Presets.Count + " preset";
        public string LutCubeSizeText => SelectedPreset == null ? "—" : SelectedPreset.LutCubeSize + "³";
        public string LutStatusText => SelectedPreset == null ? "Chưa chọn" : LocalizeStatus(SelectedPreset.LutStatus);
        public string LutStrengthPercent => Math.Round(LutStrength * 100) + "%";
        public string PinButtonText => SelectedPreset?.IsPinned == true ? "Bỏ ghim" : "Ghim preset";
        public byte[] PreviewBytes { get => previewBytes; private set => Set(ref previewBytes, value); }
        public bool IsPreviewBusy { get => isPreviewBusy; private set => Set(ref isPreviewBusy, value); }

        public double LutStrength
        {
            get => lutStrength;
            set
            {
                if (!Set(ref lutStrength, Math.Max(0, Math.Min(1, value)))) return;
                Raise(nameof(LutStrengthPercent));
                QueuePreview();
            }
        }

        public Preset SelectedPreset
        {
            get => selected;
            set
            {
                if (!Set(ref selected, value)) return;
                EditName = value?.Name ?? string.Empty;
                AssignmentEvent = FindAssignment(value?.EventId);
                lutStrength = ColorLutData.DefaultStrength;
                Raise(nameof(LutStrength)); Raise(nameof(LutStrengthPercent)); Raise(nameof(HasSelection));
                Raise(nameof(LutCubeSizeText)); Raise(nameof(LutStatusText)); Raise(nameof(PinButtonText));
                CommandManager.InvalidateRequerySuggested();
                QueuePreview();
            }
        }

        public PresetEventItem SelectedEvent
        {
            get => selectedEvent;
            set
            {
                if (!Set(ref selectedEvent, value)) return;
                EditEventName = CanManageSelectedEvent ? value.Name : string.Empty;
                Raise(nameof(CanManageSelectedEvent));
                Apply();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public PresetEventItem AssignmentEvent { get => assignmentEvent; set => Set(ref assignmentEvent, value); }

        public ICommand ImportLutCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand RenameCommand { get; }
        public ICommand PinCommand { get; }
        public ICommand CreateEventCommand { get; }
        public ICommand RenameEventCommand { get; }
        public ICommand DeleteEventCommand { get; }
        public ICommand AssignEventCommand { get; }

        public Task RefreshAsync() => Load();

        async Task Load(Guid? selectPresetId = null, Guid? selectEventId = null)
        {
            try
            {
                var oldEvent = selectEventId ?? (SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized ? SelectedEvent.Id : null);
                var oldAll = SelectedEvent?.IsAll == true;
                var oldUncategorized = SelectedEvent?.IsUncategorized == true;
                Presets.Clear();
                foreach (var preset in await service.GetAllAsync(CancellationToken.None)) Presets.Add(preset);
                BuildEvents(await eventService.GetAllAsync(CancellationToken.None));
                SelectedEvent = oldEvent.HasValue ? EventItems.FirstOrDefault(x => x.Id == oldEvent)
                    : oldUncategorized ? EventItems.First(x => x.IsUncategorized)
                    : oldAll ? EventItems.First(x => x.IsAll) : EventItems.First();
                Apply();
                SelectedPreset = selectPresetId.HasValue ? VisiblePresets.FirstOrDefault(x => x.Id == selectPresetId) : VisiblePresets.FirstOrDefault();
            }
            catch (Exception exception) { Fail(exception, "Không thể tải thư viện preset"); }
        }

        void BuildEvents(IReadOnlyList<PresetEvent> events)
        {
            EventItems.Clear(); AssignmentEvents.Clear();
            EventItems.Add(new PresetEventItem { Name = "Tất cả preset", PresetCount = Presets.Count, IsAll = true });
            var uncategorized = new PresetEventItem { Name = "Chưa phân loại", PresetCount = Presets.Count(x => !x.EventId.HasValue), IsUncategorized = true };
            EventItems.Add(uncategorized); AssignmentEvents.Add(uncategorized);
            foreach (var item in events.OrderBy(x => x.Name))
            {
                var view = new PresetEventItem { Id = item.Id, Name = item.Name, PresetCount = Presets.Count(x => x.EventId == item.Id) };
                EventItems.Add(view); AssignmentEvents.Add(view);
            }
        }

        void Apply()
        {
            var query = Presets.AsEnumerable();
            if (SelectedEvent?.IsUncategorized == true) query = query.Where(x => !x.EventId.HasValue);
            else if (SelectedEvent != null && !SelectedEvent.IsAll) query = query.Where(x => x.EventId == SelectedEvent.Id);
            if (!string.IsNullOrWhiteSpace(Search))
                query = query.Where(x => (x.Name ?? string.Empty).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0 || (x.LutDisplayName ?? string.Empty).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0);
            query = Sort == "Tên A–Z" ? query.OrderBy(x => x.Name)
                : Sort == "Mới nhất" ? query.OrderByDescending(x => x.CreatedAtUtc)
                : query.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.CreatedAtUtc);
            VisiblePresets.Clear(); foreach (var item in query) VisiblePresets.Add(item);
            Raise(nameof(PresetSummary));
        }

        PresetEventItem FindAssignment(Guid? id) => id.HasValue ? AssignmentEvents.FirstOrDefault(x => x.Id == id) : AssignmentEvents.FirstOrDefault(x => x.IsUncategorized);

        async Task ImportLut()
        {
            var file = dialogs.PickCube();
            if (string.IsNullOrWhiteSpace(file)) return;
            try
            {
                Message = "Đang kiểm tra và nhập LUT…";
                var imported = await color.ImportAsync(file, Path.GetFileNameWithoutExtension(file), CancellationToken.None);
                var existing = Presets.FirstOrDefault(x => x.LutAssetId == imported.Asset.Id);
                if (existing != null)
                {
                    Message = "LUT này đã có trong thư viện preset.";
                    await Load(existing.Id, existing.EventId);
                    return;
                }

                var now = DateTime.UtcNow;
                var eventId = SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized ? SelectedEvent.Id : null;
                var preset = new Preset { Id = Guid.NewGuid(), Name = NextDefaultName(), CreatedAtUtc = now, ModifiedAtUtc = now, CaptureCountdownSeconds = 3, EventId = eventId };
                await service.SaveAsync(preset, CancellationToken.None);
                try { await color.AttachAsync(preset.Id, imported.Asset.Id, CancellationToken.None); }
                catch { await service.DeleteAsync(preset.Id, CancellationToken.None); throw; }
                Message = eventId.HasValue ? "Đã nhập LUT vào sự kiện “" + SelectedEvent.Name + "”." : "Đã tạo preset mới từ LUT.";
                await Load(preset.Id, eventId);
            }
            catch (Exception exception) { Fail(exception, "Không thể nhập LUT"); }
        }

        string NextDefaultName()
        {
            const string root = "Preset mới";
            if (!Presets.Any(x => string.Equals(x.Name, root, StringComparison.OrdinalIgnoreCase))) return root;
            var number = 2;
            while (Presets.Any(x => string.Equals(x.Name, root + " (" + number + ")", StringComparison.OrdinalIgnoreCase))) number++;
            return root + " (" + number + ")";
        }

        async Task Rename()
        {
            var target = SelectedPreset;
            try
            {
                target.Name = EditName.Trim(); target.ModifiedAtUtc = DateTime.UtcNow;
                await service.SaveAsync(target, CancellationToken.None);
                Message = "Đã lưu tên hiển thị."; await Load(target.Id, SelectedEvent?.Id);
            }
            catch (Exception exception) { Fail(exception, "Không thể đổi tên preset"); }
        }

        async Task Pin()
        {
            var target = SelectedPreset;
            try
            {
                await service.SetPinnedAsync(target.Id, !target.IsPinned, CancellationToken.None);
                Message = target.IsPinned ? "Đã bỏ ghim preset." : "Đã ghim preset.";
                await Load(target.Id, SelectedEvent?.Id);
            }
            catch (Exception exception) { Fail(exception, "Không thể thay đổi trạng thái ghim"); }
        }

        async Task Delete()
        {
            var target = SelectedPreset;
            try
            {
                var workflow = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
                if (workflow.DefaultPresetId == target.Id) { workflow.DefaultPresetId = null; await settingsService.SaveAsync(workflow, CancellationToken.None); }
                await service.DeleteAsync(target.Id, CancellationToken.None);
                await color.DeleteAsync(target.LutAssetId, target.LutRowVersion, CancellationToken.None);
                Message = "Đã xóa preset và tệp LUT đi kèm."; await Load();
            }
            catch (Exception exception) { Fail(exception, "Không thể xóa preset"); }
        }

        async Task CreateEvent()
        {
            try { var value = await eventService.CreateAsync(NewEventName, CancellationToken.None); NewEventName = "Sự kiện mới"; Message = "Đã tạo sự kiện “" + value.Name + "”."; await Load(null, value.Id); }
            catch (Exception exception) { Fail(exception, "Không thể tạo sự kiện"); }
        }

        async Task RenameEvent()
        {
            try { var id = SelectedEvent.Id.Value; await eventService.RenameAsync(id, EditEventName, CancellationToken.None); Message = "Đã đổi tên sự kiện."; await Load(null, id); }
            catch (Exception exception) { Fail(exception, "Không thể đổi tên sự kiện"); }
        }

        async Task DeleteEvent()
        {
            try { var name = SelectedEvent.Name; await eventService.DeleteAsync(SelectedEvent.Id.Value, CancellationToken.None); Message = "Đã xóa sự kiện “" + name + "”. Các preset được chuyển về Chưa phân loại."; await Load(); }
            catch (Exception exception) { Fail(exception, "Không thể xóa sự kiện"); }
        }

        async Task AssignEvent()
        {
            try
            {
                var presetId = SelectedPreset.Id;
                var eventId = AssignmentEvent.IsUncategorized ? (Guid?)null : AssignmentEvent.Id;
                await eventService.AssignPresetAsync(presetId, eventId, CancellationToken.None);
                Message = eventId.HasValue ? "Đã chuyển preset vào “" + AssignmentEvent.Name + "”." : "Đã chuyển preset về Chưa phân loại.";
                await Load(presetId, SelectedEvent?.Id);
            }
            catch (Exception exception) { Fail(exception, "Không thể phân loại preset"); }
        }

        void LoadModelPreview()
        {
            try { if (File.Exists(modelPath)) PreviewBytes = File.ReadAllBytes(modelPath); }
            catch (Exception exception) { log.LogWarning(exception, "Unable to load preset model preview"); }
        }

        void QueuePreview()
        {
            previewCancellation?.Cancel(); previewCancellation?.Dispose();
            previewCancellation = new CancellationTokenSource();
            var token = previewCancellation.Token;
            var target = SelectedPreset;
            var strength = (float)LutStrength;
            if (target == null) { IsPreviewBusy = false; LoadModelPreview(); return; }
            _ = RenderPreview(target, strength, token);
        }

        async Task RenderPreview(Preset target, float strength, CancellationToken token)
        {
            try
            {
                IsPreviewBusy = true;
                await Task.Delay(120, token);
                var bytes = await color.RenderPreviewAsync(target.LutAssetId, modelPath, strength, token);
                if (!token.IsCancellationRequested && SelectedPreset?.Id == target.Id) PreviewBytes = bytes;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                log.LogWarning(exception, "Unable to render preset preview for {PresetId}", target.Id);
                if (!token.IsCancellationRequested) LoadModelPreview();
            }
            finally { if (!token.IsCancellationRequested) IsPreviewBusy = false; }
        }

        static string LocalizeStatus(string status)
        {
            switch (status)
            {
                case "Ready": return "Sẵn sàng";
                case "Missing": return "Thiếu tệp";
                case "Corrupt": return "Tệp lỗi";
                case "Staging": return "Đang nhập";
                case "PendingDelete": return "Chờ xóa";
                default: return status ?? "Không xác định";
            }
        }

        void Fail(Exception exception, string text)
        {
            log.LogError(exception, text);
            Message = text + ": " + exception.Message;
        }
    }
}
