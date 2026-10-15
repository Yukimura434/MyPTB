using System;
using System.Collections.ObjectModel;
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
    public sealed class FrameEventItem
    {
        public Guid? Id { get; set; }
        public string Name { get; set; }
        public int FrameCount { get; set; }
        public bool IsAll { get; set; }
        public bool IsUsed { get; set; }
        public bool IsSelected { get; set; }
        public bool IsUncategorized { get; set; }
        public string CountText => FrameCount + " frame";
    }

    public sealed class FrameManagerViewModel : PageViewModel
    {
        readonly IPcSessionContext sessions;
        Settings librarySettings = new Settings();
        string libraryTab = "Tất cả";
        public bool CanEditLibrary => sessions.Current?.CanManageConfiguration == true;
        public string LibraryTab { get => libraryTab; set { if (Set(ref libraryTab, value)) { Raise(nameof(ShowCollections)); Raise(nameof(ShowAssets)); Apply(); } } }
        public bool ShowCollections => LibraryTab == "Bộ sưu tập";
        public bool ShowAssets => true;
        public bool IsLibraryEmpty => VisibleFrames.Count == 0;
        public string EmptyLibraryText => ShowCollections ? (CanManageSelectedEvent ? "Bộ sưu tập chưa có mục nào. Chọn một mục ở tab Tất cả để phân loại." : "Chọn bộ sưu tập ở menu phía trên hoặc tạo bộ sưu tập mới.") : "Chưa có mục nào trong danh sách này.";
        public string LibraryHeading => ShowCollections ? SelectedEvent?.Name ?? "Chọn bộ sưu tập" : LibraryTab == "Đang sử dụng" ? "Đang sử dụng" : "Tất cả khung ảnh";
        public string CollectionSummary => CanManageSelectedEvent ? SelectedEvent.Name + " · " + SelectedEvent.FrameCount + " mục" : "Chưa chọn bộ sưu tập";
        bool libraryBusy;
        public bool IsLibraryReady => CanEditLibrary && !libraryBusy;
        public ICommand UseCollectionCommand { get; }
        public ICommand SelectCollectionCommand { get; }
        public ObservableCollection<FrameEventItem> Collections { get; } = new ObservableCollection<FrameEventItem>();
        public string EffectiveSummary => BoothLibrarySelection.Frames(Frames, librarySettings).Count + " mục đang sử dụng";
        public ICommand SetLibraryTabCommand { get; }
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
            var current = await settings.GetAsync(CancellationToken.None) ?? new Settings();
            if (!(current.SelectedFrameCollectionIds ?? new Guid[0]).Any()) return;
            var assets = await service.GetAllAsync(CancellationToken.None);
            await service.ReplacePinnedAsync(BoothLibrarySelection.Frames(assets, current).Select(x => x.Id).ToArray(), CancellationToken.None);
        }
        readonly IFrameService service;
        readonly IQrCodeService qrService;
        readonly IFrameEventService eventService;
        readonly ISettingsService settings;
        readonly IFileDialogService dialog;
        readonly INavigationService navigation;
        readonly FrameSlotOrderViewModel slotOrder;
        readonly ILogger<FrameManagerViewModel> log;
        string message, newEventName = "Bộ sưu tập mới", editEventName = string.Empty;
        Frame selected;
        FrameEventItem selectedEvent, assignmentEvent;
        int pinnedCount;
        Guid? pendingSelectedFrameId;

        public FrameManagerViewModel(IFrameService frames, IFrameEventService events, ISettingsService st, IFileDialogService d,
            INavigationService navigationService, FrameSlotOrderViewModel slotOrderViewModel, ILogger<FrameManagerViewModel> l, IPcSessionContext sessions, IQrCodeService qrService = null)
        {
            this.sessions = sessions; this.qrService=qrService;
            service = frames; eventService = events; settings = st; dialog = d; navigation = navigationService; slotOrder = slotOrderViewModel; log = l;
            ImportCommand = new AsyncCommand(_ => Import(), _ => IsLibraryReady); DeleteCommand = new AsyncCommand(_ => Delete(), _ => IsLibraryReady && SelectedFrame != null);
            PinCommand = new AsyncCommand(value => Pin(value as Frame ?? SelectedFrame), value => IsLibraryReady && (value is Frame || SelectedFrame != null)); RefreshCommand = new AsyncCommand(_ => Load());
            EditSlotOrderCommand = new RelayCommand(value => { if (value is Frame target) SelectedFrame = target; EditSlotOrder(); }, value => CanEditLibrary && (value is Frame || SelectedFrame != null));
            CreateEventCommand = new AsyncCommand(_ => CreateEvent(), _ => IsLibraryReady && !string.IsNullOrWhiteSpace(NewEventName));
            RenameEventCommand = new AsyncCommand(_ => RenameEvent(), _ => IsLibraryReady && CanManageSelectedEvent && !string.IsNullOrWhiteSpace(EditEventName));
            DeleteEventCommand = new AsyncCommand(_ => DeleteEvent(), _ => IsLibraryReady && CanManageSelectedEvent);
            AssignEventCommand = new AsyncCommand(_ => AssignEvent(), _ => IsLibraryReady && SelectedFrame != null && AssignmentEvent != null && !AssignmentEvent.IsAll);
            SetLibraryTabCommand = new RelayCommand(value => LibraryTab = value?.ToString() ?? "Tất cả");
            SelectCollectionCommand = new RelayCommand(value => { SelectedEvent = value as FrameEventItem; LibraryTab = "Bộ sưu tập"; });
            UseCollectionCommand = new AsyncCommand(_ => UseCollection(), _ => IsLibraryReady && ShowCollections && CanManageSelectedEvent);
            sessions.Changed += (_, __) => { Raise(nameof(CanEditLibrary)); Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested(); };
            _ = Load();
        }

        public override string Title => "Quản lý frame";
        public ObservableCollection<Frame> Frames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<Frame> VisibleFrames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<NumberedFrameSlot> DetailSlots { get; } = new ObservableCollection<NumberedFrameSlot>();
        public ObservableCollection<FrameEventItem> EventItems { get; } = new ObservableCollection<FrameEventItem>();
        public ObservableCollection<FrameEventItem> AssignmentEvents { get; } = new ObservableCollection<FrameEventItem>();
        public string NewEventName { get => newEventName; set => Set(ref newEventName, value); }
        public string EditEventName { get => editEventName; set => Set(ref editEventName, value); }
        public string Message { get => message; set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public int PinnedCount { get => pinnedCount; private set => Set(ref pinnedCount, value); }
        public string FrameSummary => VisibleFrames.Count + " / " + Frames.Count + " khung";
        public bool CanManageSelectedEvent => SelectedEvent != null && SelectedEvent.Id.HasValue && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized;

        public Frame SelectedFrame { get => selected; set { if (Set(ref selected, value)) { AssignmentEvent = FindAssignment(value?.EventId); BuildDetailSlots(); Raise(nameof(HasSelectedFrame)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } } }
        public bool HasSelectedFrame => SelectedFrame != null;
        public FrameEventItem SelectedEvent
        {
            get => selectedEvent;
            set { if (!Set(ref selectedEvent, value)) return; EditEventName = CanManageSelectedEvent ? value.Name : string.Empty; Raise(nameof(CanManageSelectedEvent)); Apply(); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
        }
        public FrameEventItem AssignmentEvent { get => assignmentEvent; set => Set(ref assignmentEvent, value); }

        public ICommand ImportCommand { get; } public ICommand DeleteCommand { get; } public ICommand PinCommand { get; } public ICommand RefreshCommand { get; }
        public ICommand EditSlotOrderCommand { get; }
        public ICommand CreateEventCommand { get; } public ICommand RenameEventCommand { get; } public ICommand DeleteEventCommand { get; } public ICommand AssignEventCommand { get; }

        public async void EditPrintedQr(System.Windows.Window owner)
        {
            if(!IsLibraryReady || qrService==null)return;
            try {
                var current=await settings.GetAsync(CancellationToken.None) ?? new Settings();
                var latest=Frames.OrderByDescending(f=>f.CreatedAtUtc).FirstOrDefault();
                var preview=new Frame { Name="Xem trước", PixelWidth=latest?.PixelWidth ?? 1500, PixelHeight=latest?.PixelHeight ?? 1000,
                    SourcePath=System.IO.File.Exists(current.QrPreviewImagePath) ? current.QrPreviewImagePath : latest?.SourcePath,
                    PrintedQr=current.PrintedQr ?? new FrameQrSettings() };
                PhotoBooth.Admin.UI.Views.FrameQrEditorWindow editor=null;
                editor=new PhotoBooth.Admin.UI.Views.FrameQrEditorWindow(preview,async value=>{
                    if(!CanEditLibrary)throw new InvalidOperationException();
                    var fresh=await settings.GetAsync(CancellationToken.None) ?? new Settings();
                    fresh.PrintedQr=value;
                    fresh.QrPreviewImagePath=editor.PersistPreviewImage(fresh.QrPreviewImagePath);
                    await settings.SaveAsync(fresh,CancellationToken.None);
                    Message="Đã lưu QR cho tất cả khung. Áp dụng từ lượt chụp tiếp theo.";
                },qrService){Owner=owner};editor.ShowDialog();
            } catch(Exception error) { Fail(error,"Không thể mở cài đặt QR"); }
        }

        public Task RefreshAsync()
        {
            var frameId = pendingSelectedFrameId;
            pendingSelectedFrameId = null;
            return Load(frameId);
        }

        void BuildDetailSlots()
        {
            DetailSlots.Clear();
            foreach (var slot in (SelectedFrame?.Slots ?? new FrameSlot[0]).OrderBy(x => x.Index))
                DetailSlots.Add(new NumberedFrameSlot(slot, slot.Index + 1));
        }

        void EditSlotOrder()
        {
            var target = SelectedFrame;
            if (target == null || !CanEditLibrary) return;
            slotOrder.Open(target, (id, saved) =>
            {
                pendingSelectedFrameId = id;
                if (saved) Message = "Đã lưu thứ tự ô ảnh.";
            });
            navigation.Navigate("frame-slot-order");
        }

        async Task Load(Guid? selectFrameId = null, Guid? selectEventId = null)
        {
            try
            {
                var oldEvent = selectEventId ?? SelectedEvent?.Id;
                librarySettings = await settings.GetAsync(CancellationToken.None) ?? new Settings();
                Frames.Clear(); foreach (var x in await service.GetAllAsync(CancellationToken.None)) Frames.Add(x);
                var effective = new System.Collections.Generic.HashSet<Guid>(BoothLibrarySelection.Frames(Frames, librarySettings).Select(x => x.Id));
                foreach (var item in Frames) item.IsPinned = effective.Contains(item.Id);
                var events = await eventService.GetAllAsync(CancellationToken.None);
                BuildEvents(events); PinnedCount = Frames.Count(x => x.IsPinned);
                SelectedEvent = Collections.FirstOrDefault(x => x.Id == oldEvent);
                Apply(); SelectedFrame = selectFrameId.HasValue ? VisibleFrames.FirstOrDefault(x => x.Id == selectFrameId) ?? VisibleFrames.FirstOrDefault() : VisibleFrames.FirstOrDefault();
            }
            catch (Exception e) { Fail(e, "Không thể tải danh sách frame"); }
        }

        void BuildEvents(System.Collections.Generic.IReadOnlyList<FrameEvent> events)
        {
            EventItems.Clear(); AssignmentEvents.Clear(); Collections.Clear();
            EventItems.Add(new FrameEventItem { Name = "Tất cả frame", FrameCount = Frames.Count, IsAll = true });
            var uncategorized = new FrameEventItem { Name = "Chưa phân loại", FrameCount = Frames.Count(x => !x.EventId.HasValue), IsUncategorized = true };
            EventItems.Add(uncategorized); AssignmentEvents.Add(uncategorized);
            foreach (var item in events.OrderBy(x => x.Name)) { var view = new FrameEventItem { Id = item.Id, Name = item.Name, FrameCount = Frames.Count(x => x.EventId == item.Id) }; view.IsSelected = (librarySettings.SelectedFrameCollectionIds ?? new Guid[0]).Contains(item.Id); Collections.Add(view); EventItems.Add(view); AssignmentEvents.Add(view); }
        }

        void Apply()
        {
            var query = Frames.AsEnumerable();
            if (LibraryTab == "Đang sử dụng") query = query.Where(x => x.IsPinned);
            else if (ShowCollections) query = query.Where(x => SelectedEvent?.Id != null && x.EventId == SelectedEvent.Id);
            var items = query.ToList();
            var selectedId = SelectedFrame?.Id;
            VisibleFrames.Clear(); foreach (var item in items) VisibleFrames.Add(item);
            SelectedFrame = items.FirstOrDefault(x => x.Id == selectedId) ?? items.FirstOrDefault();
            Raise(nameof(FrameSummary)); Raise(nameof(EffectiveSummary)); Raise(nameof(IsLibraryEmpty)); Raise(nameof(EmptyLibraryText)); Raise(nameof(LibraryHeading)); Raise(nameof(CollectionSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        FrameEventItem FindAssignment(Guid? id) => id.HasValue ? AssignmentEvents.FirstOrDefault(x => x.Id == id) : AssignmentEvents.FirstOrDefault(x => x.IsUncategorized);

        async Task Import()
        {
            var path = dialog.PickPng(); if (path == null) return;
            try
            {
                await PreserveUsedSet();
                var s = await settings.GetAsync(CancellationToken.None) ?? new Settings();
                var frame = await service.ImportAsync(path, new FrameAnalysisOptions { AlphaThreshold = s.TransparentAlphaThreshold, MinimumArea = s.MinimumSlotArea, MinimumWidth = s.MinimumSlotWidth, MinimumHeight = s.MinimumSlotHeight, IgnoreBorderConnectedRegions = s.IgnoreBorderTransparency, MaximumSlots = 8 }, CancellationToken.None);
                var eventId = ShowCollections && SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized ? SelectedEvent.Id : null;
                if (eventId.HasValue) await eventService.AssignFrameAsync(frame.Id, eventId, CancellationToken.None);
                Message = eventId.HasValue ? "Đã nhập frame vào bộ sưu tập “" + SelectedEvent.Name + "”." : "Đã nhập frame."; await Load(frame.Id, eventId);
            }
            catch (Exception e) { Fail(e, e.Message.Contains("Invalid Frame") ? "Frame không hợp lệ" : "Không thể nhập frame"); }
        }

        async Task Delete() { var target = SelectedFrame; try { await service.DeleteAsync(target.Id, CancellationToken.None); Message = "Đã xóa frame."; await Load(); } catch (Exception e) { Fail(e, "Không thể xóa frame"); } }
        async Task Pin(Frame target)
        {
            if (!IsLibraryReady || target == null) return;
            libraryBusy = true; Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested();
            try
            {
                var current = await settings.GetAsync(CancellationToken.None) ?? new Settings();
                var assets = await service.GetAllAsync(CancellationToken.None);
                var ids = new System.Collections.Generic.HashSet<Guid>(BoothLibrarySelection.Frames(assets, current).Select(x => x.Id));
                if (!ids.Remove(target.Id)) ids.Add(target.Id);
                if (!CanEditLibrary) return;
                await service.ReplacePinnedAsync(ids, CancellationToken.None);
                await Load(target.Id, SelectedEvent?.Id);
            }
            catch (Exception error) { Fail(error, "Không thể thay đổi trạng thái ghim"); }
            finally { libraryBusy = false; Raise(nameof(IsLibraryReady)); CommandManager.InvalidateRequerySuggested(); }
        }
        async Task CreateEvent() { try { var value = await eventService.CreateAsync(NewEventName, CancellationToken.None); NewEventName = "Bộ sưu tập mới"; Message = "Đã tạo bộ sưu tập “" + value.Name + "”."; await Load(null, value.Id); } catch (Exception e) { Fail(e, e.Message); } }
        async Task RenameEvent() { try { var id = SelectedEvent.Id.Value; await eventService.RenameAsync(id, EditEventName, CancellationToken.None); Message = "Đã đổi tên bộ sưu tập."; await Load(null, id); } catch (Exception e) { Fail(e, e.Message); } }
        async Task DeleteEvent() { try { var name = SelectedEvent.Name; await PreserveUsedSet(); await eventService.DeleteAsync(SelectedEvent.Id.Value, CancellationToken.None); Message = "Đã xóa bộ sưu tập “" + name + "”. Các frame được chuyển về Chưa phân loại."; await Load(); } catch (Exception e) { Fail(e, e.Message); } }
        async Task AssignEvent() { try { var frameId = SelectedFrame.Id; var eventId = AssignmentEvent.IsUncategorized ? (Guid?)null : AssignmentEvent.Id; await PreserveUsedSet(); await eventService.AssignFrameAsync(frameId, eventId, CancellationToken.None); Message = eventId.HasValue ? "Đã chuyển frame vào “" + AssignmentEvent.Name + "”." : "Đã chuyển frame về Chưa phân loại."; await Load(frameId, SelectedEvent?.Id); } catch (Exception e) { Fail(e, e.Message); } }
        void Fail(Exception e, string text) { log.LogError(e, text); Message = text; }
    }
}
