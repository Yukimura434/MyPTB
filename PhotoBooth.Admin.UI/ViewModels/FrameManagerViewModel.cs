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
        public bool IsUncategorized { get; set; }
        public string CountText => FrameCount + " frame";
    }

    public sealed class FrameManagerViewModel : PageViewModel
    {
        readonly IFrameService service;
        readonly IFrameEventService eventService;
        readonly ISettingsService settings;
        readonly IFileDialogService dialog;
        readonly INavigationService navigation;
        readonly FrameSlotOrderViewModel slotOrder;
        readonly ILogger<FrameManagerViewModel> log;
        string search = string.Empty, sort = "Ghim trước", message, newEventName = "Sự kiện mới", editEventName = string.Empty;
        Frame selected;
        FrameEventItem selectedEvent, assignmentEvent;
        int pinnedCount;
        Guid? pendingSelectedFrameId;

        public FrameManagerViewModel(IFrameService frames, IFrameEventService events, ISettingsService st, IFileDialogService d,
            INavigationService navigationService, FrameSlotOrderViewModel slotOrderViewModel, ILogger<FrameManagerViewModel> l)
        {
            service = frames; eventService = events; settings = st; dialog = d; navigation = navigationService; slotOrder = slotOrderViewModel; log = l;
            ImportCommand = new AsyncCommand(_ => Import()); DeleteCommand = new AsyncCommand(_ => Delete(), _ => SelectedFrame != null);
            PinCommand = new AsyncCommand(_ => Pin(), _ => SelectedFrame != null); RefreshCommand = new AsyncCommand(_ => Load());
            EditSlotOrderCommand = new RelayCommand(_ => EditSlotOrder(), _ => SelectedFrame != null);
            CreateEventCommand = new AsyncCommand(_ => CreateEvent(), _ => !string.IsNullOrWhiteSpace(NewEventName));
            RenameEventCommand = new AsyncCommand(_ => RenameEvent(), _ => CanManageSelectedEvent && !string.IsNullOrWhiteSpace(EditEventName));
            DeleteEventCommand = new AsyncCommand(_ => DeleteEvent(), _ => CanManageSelectedEvent);
            AssignEventCommand = new AsyncCommand(_ => AssignEvent(), _ => SelectedFrame != null && AssignmentEvent != null && !AssignmentEvent.IsAll);
            SortOptions = new[] { "Ghim trước", "Mới nhất", "Tên A–Z" }; _ = Load();
        }

        public override string Title => "Quản lý frame";
        public ObservableCollection<Frame> Frames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<Frame> VisibleFrames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<NumberedFrameSlot> DetailSlots { get; } = new ObservableCollection<NumberedFrameSlot>();
        public ObservableCollection<FrameEventItem> EventItems { get; } = new ObservableCollection<FrameEventItem>();
        public ObservableCollection<FrameEventItem> AssignmentEvents { get; } = new ObservableCollection<FrameEventItem>();
        public string[] SortOptions { get; }
        public string Search { get => search; set { if (Set(ref search, value)) Apply(); } }
        public string Sort { get => sort; set { if (Set(ref sort, value)) Apply(); } }
        public string NewEventName { get => newEventName; set => Set(ref newEventName, value); }
        public string EditEventName { get => editEventName; set => Set(ref editEventName, value); }
        public string Message { get => message; set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public int PinnedCount { get => pinnedCount; private set => Set(ref pinnedCount, value); }
        public const int MaxPins = 10;
        public string FrameSummary => VisibleFrames.Count + " / " + Frames.Count + " frame";
        public bool CanManageSelectedEvent => SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized;

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
            if (target == null) return;
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
                var oldEvent = selectEventId ?? (SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized ? SelectedEvent.Id : null);
                var oldAll = SelectedEvent?.IsAll == true; var oldUncategorized = SelectedEvent?.IsUncategorized == true;
                Frames.Clear(); foreach (var x in await service.GetAllAsync(CancellationToken.None)) Frames.Add(x);
                var events = await eventService.GetAllAsync(CancellationToken.None);
                BuildEvents(events); PinnedCount = Frames.Count(x => x.IsPinned);
                SelectedEvent = oldEvent.HasValue ? EventItems.FirstOrDefault(x => x.Id == oldEvent) : oldUncategorized ? EventItems.First(x => x.IsUncategorized) : oldAll ? EventItems.First(x => x.IsAll) : EventItems.First();
                Apply(); SelectedFrame = selectFrameId.HasValue ? VisibleFrames.FirstOrDefault(x => x.Id == selectFrameId) : VisibleFrames.FirstOrDefault();
            }
            catch (Exception e) { Fail(e, "Không thể tải danh sách frame"); }
        }

        void BuildEvents(System.Collections.Generic.IReadOnlyList<FrameEvent> events)
        {
            EventItems.Clear(); AssignmentEvents.Clear();
            EventItems.Add(new FrameEventItem { Name = "Tất cả frame", FrameCount = Frames.Count, IsAll = true });
            var uncategorized = new FrameEventItem { Name = "Chưa phân loại", FrameCount = Frames.Count(x => !x.EventId.HasValue), IsUncategorized = true };
            EventItems.Add(uncategorized); AssignmentEvents.Add(uncategorized);
            foreach (var item in events.OrderBy(x => x.Name)) { var view = new FrameEventItem { Id = item.Id, Name = item.Name, FrameCount = Frames.Count(x => x.EventId == item.Id) }; EventItems.Add(view); AssignmentEvents.Add(view); }
        }

        void Apply()
        {
            var q = Frames.AsEnumerable();
            if (SelectedEvent?.IsUncategorized == true) q = q.Where(x => !x.EventId.HasValue); else if (SelectedEvent != null && !SelectedEvent.IsAll) q = q.Where(x => x.EventId == SelectedEvent.Id);
            if (!string.IsNullOrWhiteSpace(Search)) q = q.Where(x => (x.Name ?? string.Empty).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0);
            q = Sort == "Tên A–Z" ? q.OrderBy(x => x.Name) : Sort == "Mới nhất" ? q.OrderByDescending(x => x.CreatedAtUtc) : q.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.CreatedAtUtc);
            VisibleFrames.Clear(); foreach (var x in q) VisibleFrames.Add(x); Raise(nameof(FrameSummary));
        }

        FrameEventItem FindAssignment(Guid? id) => id.HasValue ? AssignmentEvents.FirstOrDefault(x => x.Id == id) : AssignmentEvents.FirstOrDefault(x => x.IsUncategorized);

        async Task Import()
        {
            var path = dialog.PickPng(); if (path == null) return;
            try
            {
                var s = await settings.GetAsync(CancellationToken.None) ?? new Settings();
                var frame = await service.ImportAsync(path, new FrameAnalysisOptions { AlphaThreshold = s.TransparentAlphaThreshold, MinimumArea = s.MinimumSlotArea, MinimumWidth = s.MinimumSlotWidth, MinimumHeight = s.MinimumSlotHeight, IgnoreBorderConnectedRegions = s.IgnoreBorderTransparency, MaximumSlots = 8 }, CancellationToken.None);
                var eventId = SelectedEvent != null && !SelectedEvent.IsAll && !SelectedEvent.IsUncategorized ? SelectedEvent.Id : null;
                if (eventId.HasValue) await eventService.AssignFrameAsync(frame.Id, eventId, CancellationToken.None);
                Message = eventId.HasValue ? "Đã nhập frame vào sự kiện “" + SelectedEvent.Name + "”." : "Đã nhập frame."; await Load(frame.Id, eventId);
            }
            catch (Exception e) { Fail(e, e.Message.Contains("Invalid Frame") ? "Frame không hợp lệ" : "Không thể nhập frame"); }
        }

        async Task Delete() { var target = SelectedFrame; try { await service.DeleteAsync(target.Id, CancellationToken.None); Message = "Đã xóa frame."; await Load(); } catch (Exception e) { Fail(e, "Không thể xóa frame"); } }
        async Task Pin() { try { var id = SelectedFrame.Id; await service.SetPinnedAsync(id, !SelectedFrame.IsPinned, CancellationToken.None); await Load(id); } catch (Exception e) { Fail(e, e.Message); } }
        async Task CreateEvent() { try { var value = await eventService.CreateAsync(NewEventName, CancellationToken.None); NewEventName = "Sự kiện mới"; Message = "Đã tạo sự kiện “" + value.Name + "”."; await Load(null, value.Id); } catch (Exception e) { Fail(e, e.Message); } }
        async Task RenameEvent() { try { var id = SelectedEvent.Id.Value; await eventService.RenameAsync(id, EditEventName, CancellationToken.None); Message = "Đã đổi tên sự kiện."; await Load(null, id); } catch (Exception e) { Fail(e, e.Message); } }
        async Task DeleteEvent() { try { var name = SelectedEvent.Name; await eventService.DeleteAsync(SelectedEvent.Id.Value, CancellationToken.None); Message = "Đã xóa sự kiện “" + name + "”. Các frame được chuyển về Chưa phân loại."; await Load(); } catch (Exception e) { Fail(e, e.Message); } }
        async Task AssignEvent() { try { var frameId = SelectedFrame.Id; var eventId = AssignmentEvent.IsUncategorized ? (Guid?)null : AssignmentEvent.Id; await eventService.AssignFrameAsync(frameId, eventId, CancellationToken.None); Message = eventId.HasValue ? "Đã chuyển frame vào “" + AssignmentEvent.Name + "”." : "Đã chuyển frame về Chưa phân loại."; await Load(frameId, SelectedEvent?.Id); } catch (Exception e) { Fail(e, e.Message); } }
        void Fail(Exception e, string text) { log.LogError(e, text); Message = text; }
    }
}
