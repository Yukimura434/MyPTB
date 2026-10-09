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
        public bool IsUsed { get; set; }
        public bool IsSelected { get; set; }
        public bool IsUncategorized { get; set; }
    }

    public sealed class PresetManagerViewModel : PageViewModel
    {
        readonly IPcSessionContext sessions;
        Settings librarySettings = new Settings();
        string libraryTab = "Tất cả";
        public bool CanEditLibrary => sessions.Current?.CanManageConfiguration == true;
        public string LibraryTab { get => libraryTab; set { if (Set(ref libraryTab, value)) { Raise(nameof(ShowCollections)); Raise(nameof(ShowAssets)); Apply(); } } }
        public bool ShowCollections => LibraryTab == "Bộ sưu tập";
        public bool ShowAssets => true;
        public bool IsLibraryEmpty => VisiblePresets.Count == 0;
        public string EmptyLibraryText => ShowCollections ? (CanManageSelectedEvent ? "Bộ sưu tập chưa có mục nào. Chọn một mục ở tab Tất cả để phân loại." : "Chọn bộ sưu tập ở menu phía trên hoặc tạo bộ sưu tập mới.") : "Chưa có mục nào trong danh sách này.";
        public string LibraryHeading => ShowCollections ? SelectedEvent?.Name ?? "Chọn bộ sưu tập" : LibraryTab == "Đang sử dụng" ? "Đang sử dụng" : "Tất cả preset";
        public string CollectionSummary => CanManageSelectedEvent ? SelectedEvent.Name + " · " + SelectedEvent.PresetCount + " mục" : "Chưa chọn bộ sưu tập";
        bool libraryBusy;
        public bool IsLibraryReady => CanEditLibrary && !libraryBusy;
        public ICommand UseCollectionCommand { get; }
        public ICommand SelectCollectionCommand { get; }
        public ObservableCollection<PresetEventItem> Collections { get; } = new ObservableCollection<PresetEventItem>();
        public string EffectiveSummary => BoothLibrarySelection.Presets(Presets, librarySettings).Count + " mục đang sử dụng";
        public ICommand SetLibraryTabCommand { get; }
        public ICommand UseLivePresetCommand { get; }
        public ICommand ClearLivePresetCommand { get; }
        public ICommand ViewLiveCommand { get; }
        public event EventHandler LiveViewRequested;
        public bool HasLivePreset => librarySettings.LiveViewPresetId.HasValue;
        public string LivePresetSummary
        {
            get
            {
                var preset = Presets.FirstOrDefault(x => x.Id == librarySettings.LiveViewPresetId);
                return !HasLivePreset ? "Màu gốc · Không dùng preset" : preset == null ? "Preset không còn trong thư viện · Dùng màu gốc"
                    : preset.Name + " · Cường độ " + librarySettings.LiveViewPresetStrengthPercent + "%";
            }
        }
        public bool IsSelectedLivePreset => SelectedPreset != null && SelectedPreset.Id == librarySettings.LiveViewPresetId && (int)Math.Round(LutStrength * 100) == librarySettings.LiveViewPresetStrengthPercent;
        public string UseLivePresetText => IsSelectedLivePreset ? "Đang dùng cho Live View" : "Áp dụng cho Live View";
        bool livePresetBusy;
        public string LivePresetHelp => SelectedPreset != null && (SelectedPreset.LutCubeSize < 2 || SelectedPreset.LutCubeSize > 65 || !string.Equals(SelectedPreset.LutStatus, "Ready", StringComparison.OrdinalIgnoreCase))
            ? "Preset này chưa hỗ trợ hình xem trước trực tiếp."
            : "Áp dụng khi mở lại Live View. Lượt chụp đang chạy giữ cài đặt hiện tại; preset ảnh chụp không thay đổi.";
        void RefreshLiveProperties()
        {
            Raise(nameof(HasLivePreset)); Raise(nameof(LivePresetSummary)); Raise(nameof(IsSelectedLivePreset)); Raise(nameof(UseLivePresetText)); Raise(nameof(LivePresetHelp));
            CommandManager.InvalidateRequerySuggested();
        }
        async Task SaveLivePreset(bool clear)
        {
            if (!CanEditLibrary || livePresetBusy) return;
            var target = SelectedPreset;
            var percent = (int)Math.Round(LutStrength * 100);
            if (!clear && (target == null || !string.Equals(target.LutStatus, "Ready", StringComparison.OrdinalIgnoreCase) || target.LutCubeSize < 2 || target.LutCubeSize > 65)) return;
            livePresetBusy = true; RefreshLiveProperties();
            try
            {
                if (!clear)
                {
                    using (var data = await color.GetLiveAsync(target.Id, CancellationToken.None))
                        if (data == null) throw new InvalidOperationException("Preset chưa sẵn sàng cho Live View.");
                }
                if (!CanEditLibrary) return;
                var current = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
                if (!CanEditLibrary) return;
                current.LiveViewPresetId = clear ? (Guid?)null : target.Id;
                current.LiveViewPresetStrengthPercent = percent;
                await settingsService.SaveAsync(current, CancellationToken.None);
                librarySettings = current;
                Message = clear ? "Live View sẽ dùng màu gốc khi mở lại." : "Đã chọn preset cho Live View. Áp dụng khi mở lại hình xem trước.";
            }
            catch (Exception error) { Fail(error, "Không thể lưu preset Live View"); }
            finally { livePresetBusy = false; RefreshLiveProperties(); }
        }
        async Task UseCollection()
        {
            var collection = SelectedEvent;
            if (!IsLibraryReady || collection?.Id == null || !ShowCollections) return;
            libraryBusy = true; Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested();
            try
            {
                if (!(await eventService.GetAllAsync(CancellationToken.None)).Any(x => x.Id == collection.Id))
                    throw new InvalidOperationException("Bộ sưu tập đã bị xóa.");
                var members = (await service.GetAllAsync(CancellationToken.None)).Where(x => x.EventId == collection.Id).Select(x => x.Id).ToArray();
                if (!CanEditLibrary) return;
                await service.ReplacePinnedAsync(members, CancellationToken.None);
                Message = "Đã sử dụng “" + collection.Name + "”. Thay thế toàn bộ danh sách đang dùng, áp dụng từ lượt chụp tiếp theo.";
                await Load(null, collection.Id);
            }
            catch (Exception error) { Fail(error, "Không thể sử dụng bộ sưu tập"); }
            finally { libraryBusy = false; Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested(); }
        }
        // Freeze legacy collection choices into the current used set before editing membership.
        async Task PreserveUsedSet()
        {
            var current = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
            if (!(current.SelectedPresetCollectionIds ?? new Guid[0]).Any()) return;
            var assets = await service.GetAllAsync(CancellationToken.None);
            await service.ReplacePinnedAsync(BoothLibrarySelection.Presets(assets, current).Select(x => x.Id).ToArray(), CancellationToken.None);
        }
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
        string editName = string.Empty;
        string newEventName = "Bộ sưu tập mới";
        string editEventName = string.Empty;
        string message;
        double lutStrength = ColorLutData.DefaultStrength;
        byte[] previewBytes;
        bool isPreviewBusy;
        CancellationTokenSource previewCancellation;

        public PresetManagerViewModel(IPresetService presets, IPresetEventService events, IColorLutService colors,
            ISettingsService settings, IFileDialogService dialog, ILogger<PresetManagerViewModel> logger, IPcSessionContext sessions)
        {
            this.sessions = sessions;
            service = presets; eventService = events; color = colors; settingsService = settings; dialogs = dialog; log = logger;
            SaveStrengthCommand = new AsyncCommand(_ => SaveStrength(), _ => IsLibraryReady && SelectedPreset != null);
            UseLivePresetCommand = new AsyncCommand(_ => SaveLivePreset(false), _ => CanEditLibrary && !livePresetBusy && !IsSelectedLivePreset && string.Equals(SelectedPreset?.LutStatus, "Ready", StringComparison.OrdinalIgnoreCase) && SelectedPreset.LutCubeSize >= 2 && SelectedPreset.LutCubeSize <= 65);
            ClearLivePresetCommand = new AsyncCommand(_ => SaveLivePreset(true), _ => CanEditLibrary && !livePresetBusy && HasLivePreset);
            ViewLiveCommand = new RelayCommand(_ => { if (CanEditLibrary) LiveViewRequested?.Invoke(this, EventArgs.Empty); }, _ => IsLibraryReady);
            ImportLutCommand = new AsyncCommand(_ => ImportLut(), _ => IsLibraryReady);
            DeleteCommand = new AsyncCommand(_ => Delete(), _ => IsLibraryReady && SelectedPreset != null);
            RenameCommand = new AsyncCommand(_ => Rename(), _ => IsLibraryReady && SelectedPreset != null && !string.IsNullOrWhiteSpace(EditName));
            PinCommand = new AsyncCommand(value => Pin(value as Preset ?? SelectedPreset), value => IsLibraryReady && (value is Preset || SelectedPreset != null));
            CreateEventCommand = new AsyncCommand(_ => CreateEvent(), _ => IsLibraryReady && !string.IsNullOrWhiteSpace(NewEventName));
            RenameEventCommand = new AsyncCommand(_ => RenameEvent(), _ => IsLibraryReady && CanManageSelectedEvent && !string.IsNullOrWhiteSpace(EditEventName));
            DeleteEventCommand = new AsyncCommand(_ => DeleteEvent(), _ => IsLibraryReady && CanManageSelectedEvent);
            AssignEventCommand = new AsyncCommand(_ => AssignEvent(), _ => IsLibraryReady && SelectedPreset != null && AssignmentEvent != null && !AssignmentEvent.IsAll);
            SetLibraryTabCommand = new RelayCommand(value => LibraryTab = value?.ToString() ?? "Tất cả");
            SelectCollectionCommand = new RelayCommand(value => { SelectedEvent = value as PresetEventItem; LibraryTab = "Bộ sưu tập"; });
            UseCollectionCommand = new AsyncCommand(_ => UseCollection(), _ => IsLibraryReady && ShowCollections && CanManageSelectedEvent);
            sessions.Changed += (_, __) => { Raise(nameof(CanEditLibrary)); Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested(); };
            LoadModelPreview();
            _ = Load();
        }

        public override string Title => "Quản lý preset";
        public ObservableCollection<Preset> Presets { get; } = new ObservableCollection<Preset>();
        public ObservableCollection<Preset> VisiblePresets { get; } = new ObservableCollection<Preset>();
        public ObservableCollection<PresetEventItem> EventItems { get; } = new ObservableCollection<PresetEventItem>();
        public ObservableCollection<PresetEventItem> AssignmentEvents { get; } = new ObservableCollection<PresetEventItem>();

        public string EditName { get => editName; set => Set(ref editName, value); }
        public string NewEventName { get => newEventName; set => Set(ref newEventName, value); }
        public string EditEventName { get => editEventName; set => Set(ref editEventName, value); }
        public string Message { get => message; set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public bool HasSelection => SelectedPreset != null;
        public bool CanManageSelectedEvent => SelectedEvent != null && SelectedEvent.Id.HasValue && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized;
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
                RefreshLiveProperties();
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
                lutStrength = Math.Max(0, Math.Min(100, value?.StrengthPercent ?? 50)) / 100d;
                Raise(nameof(LutStrength)); Raise(nameof(LutStrengthPercent)); Raise(nameof(HasSelection));
                Raise(nameof(LutCubeSizeText)); Raise(nameof(LutStatusText)); Raise(nameof(PinButtonText));
                RefreshLiveProperties();
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

        public ICommand SaveStrengthCommand { get; }
        async Task SaveStrength()
        {
            var target = SelectedPreset;
            var percent = (int)Math.Round(LutStrength * 100);
            if (!CanEditLibrary || target == null) return;
            try
            {
                // Re-read so a strength edit cannot overwrite a newer pin/name assignment.
                var latest = (await service.GetAllAsync(CancellationToken.None)).FirstOrDefault(x => x.Id == target.Id);
                if (latest == null) throw new InvalidOperationException("Preset đã bị xóa.");
                latest.StrengthPercent = percent;
                latest.ModifiedAtUtc = DateTime.UtcNow;
                await service.SaveAsync(latest, CancellationToken.None);
                Message = "Đã lưu cường độ " + percent + "%. Áp dụng từ lượt chụp tiếp theo.";
                await Load(target.Id, SelectedEvent?.Id);
            }
            catch (Exception error) { Fail(error, "Không thể lưu cường độ preset"); }
        }
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
                var oldEvent = selectEventId ?? SelectedEvent?.Id;
                librarySettings = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
                Presets.Clear();
                foreach (var preset in await service.GetAllAsync(CancellationToken.None)) Presets.Add(preset);
                var effective = new System.Collections.Generic.HashSet<Guid>(BoothLibrarySelection.Presets(Presets, librarySettings).Select(x => x.Id));
                foreach (var item in Presets) item.IsPinned = effective.Contains(item.Id);
                BuildEvents(await eventService.GetAllAsync(CancellationToken.None));
                SelectedEvent = Collections.FirstOrDefault(x => x.Id == oldEvent);
                Apply();
                SelectedPreset = selectPresetId.HasValue ? VisiblePresets.FirstOrDefault(x => x.Id == selectPresetId) ?? VisiblePresets.FirstOrDefault() : VisiblePresets.FirstOrDefault();
                RefreshLiveProperties();
            }
            catch (Exception exception) { Fail(exception, "Không thể tải thư viện preset"); }
        }

        void BuildEvents(IReadOnlyList<PresetEvent> events)
        {
            EventItems.Clear(); AssignmentEvents.Clear(); Collections.Clear();
            EventItems.Add(new PresetEventItem { Name = "Tất cả preset", PresetCount = Presets.Count, IsAll = true });
            var uncategorized = new PresetEventItem { Name = "Chưa phân loại", PresetCount = Presets.Count(x => !x.EventId.HasValue), IsUncategorized = true };
            EventItems.Add(uncategorized); AssignmentEvents.Add(uncategorized);
            foreach (var item in events.OrderBy(x => x.Name))
            {
                var view = new PresetEventItem { Id = item.Id, Name = item.Name, PresetCount = Presets.Count(x => x.EventId == item.Id) };
                view.IsSelected = (librarySettings.SelectedPresetCollectionIds ?? new Guid[0]).Contains(item.Id); Collections.Add(view); EventItems.Add(view); AssignmentEvents.Add(view);
            }
        }

        void Apply()
        {
            var query = Presets.AsEnumerable();
            if (LibraryTab == "Đang sử dụng") query = query.Where(x => x.IsPinned);
            else if (ShowCollections) query = query.Where(x => SelectedEvent?.Id != null && x.EventId == SelectedEvent.Id);
            var items = query.ToList();
            var selectedId = SelectedPreset?.Id;
            VisiblePresets.Clear(); foreach (var item in items) VisiblePresets.Add(item);
            SelectedPreset = items.FirstOrDefault(x => x.Id == selectedId) ?? items.FirstOrDefault();
            Raise(nameof(PresetSummary)); Raise(nameof(EffectiveSummary)); Raise(nameof(IsLibraryEmpty)); Raise(nameof(EmptyLibraryText)); Raise(nameof(LibraryHeading)); Raise(nameof(CollectionSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        PresetEventItem FindAssignment(Guid? id) => id.HasValue ? AssignmentEvents.FirstOrDefault(x => x.Id == id) : AssignmentEvents.FirstOrDefault(x => x.IsUncategorized);

        async Task ImportLut()
        {
            var file = dialogs.PickCube();
            if (string.IsNullOrWhiteSpace(file)) return;
            try
            {
                await PreserveUsedSet();
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
                var eventId = ShowCollections && SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized ? SelectedEvent.Id : null;
                var preset = new Preset { Id = Guid.NewGuid(), Name = NextDefaultName(), CreatedAtUtc = now, ModifiedAtUtc = now, CaptureCountdownSeconds = 3, EventId = eventId };
                await service.SaveAsync(preset, CancellationToken.None);
                try { await color.AttachAsync(preset.Id, imported.Asset.Id, CancellationToken.None); }
                catch { await service.DeleteAsync(preset.Id, CancellationToken.None); throw; }
                Message = eventId.HasValue ? "Đã nhập LUT vào bộ sưu tập “" + SelectedEvent.Name + "”." : "Đã tạo preset mới từ LUT.";
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

        async Task Pin(Preset target)
        {
            if (!IsLibraryReady || target == null) return;
            libraryBusy = true; Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested();
            try
            {
                var current = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
                var assets = await service.GetAllAsync(CancellationToken.None);
                var ids = new System.Collections.Generic.HashSet<Guid>(BoothLibrarySelection.Presets(assets, current).Select(x => x.Id));
                if (!ids.Remove(target.Id)) ids.Add(target.Id);
                if (!CanEditLibrary) return;
                await service.ReplacePinnedAsync(ids, CancellationToken.None);
                await Load(target.Id, SelectedEvent?.Id);
            }
            catch (Exception error) { Fail(error, "Không thể thay đổi trạng thái ghim"); }
            finally { libraryBusy = false; Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested(); }
        }
        async Task Delete()
        {
            var target = SelectedPreset;
            try
            {
                var workflow = await settingsService.GetAsync(CancellationToken.None) ?? new Settings();
                if (workflow.DefaultPresetId == target.Id) { workflow.DefaultPresetId = null; await settingsService.SaveAsync(workflow, CancellationToken.None); }
                if (workflow.LiveViewPresetId == target.Id) { workflow.LiveViewPresetId = null; await settingsService.SaveAsync(workflow, CancellationToken.None); }
                await service.DeleteAsync(target.Id, CancellationToken.None);
                await color.DeleteAsync(target.LutAssetId, target.LutRowVersion, CancellationToken.None);
                Message = "Đã xóa preset và tệp LUT đi kèm."; await Load();
            }
            catch (Exception exception) { Fail(exception, "Không thể xóa preset"); }
        }

        async Task CreateEvent()
        {
            try { var value = await eventService.CreateAsync(NewEventName, CancellationToken.None); NewEventName = "Bộ sưu tập mới"; Message = "Đã tạo bộ sưu tập “" + value.Name + "”."; await Load(null, value.Id); }
            catch (Exception exception) { Fail(exception, "Không thể tạo bộ sưu tập"); }
        }

        async Task RenameEvent()
        {
            try { var id = SelectedEvent.Id.Value; await eventService.RenameAsync(id, EditEventName, CancellationToken.None); Message = "Đã đổi tên bộ sưu tập."; await Load(null, id); }
            catch (Exception exception) { Fail(exception, "Không thể đổi tên bộ sưu tập"); }
        }

        async Task DeleteEvent()
        {
            try { var name = SelectedEvent.Name; await PreserveUsedSet(); await eventService.DeleteAsync(SelectedEvent.Id.Value, CancellationToken.None); Message = "Đã xóa bộ sưu tập “" + name + "”. Các preset được chuyển về Chưa phân loại."; await Load(); }
            catch (Exception exception) { Fail(exception, "Không thể xóa bộ sưu tập"); }
        }

        async Task AssignEvent()
        {
            try
            {
                var presetId = SelectedPreset.Id;
                var eventId = AssignmentEvent.IsUncategorized ? (Guid?)null : AssignmentEvent.Id;
                await PreserveUsedSet(); await eventService.AssignPresetAsync(presetId, eventId, CancellationToken.None);
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
