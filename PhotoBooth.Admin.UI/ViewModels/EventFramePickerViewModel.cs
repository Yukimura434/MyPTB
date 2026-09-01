using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class EventFrameCategory
    {
        public Guid? Id { get; set; }
        public string Name { get; set; }
        public int FrameCount { get; set; }
        public bool IsAll { get; set; }
        public bool IsUncategorized { get; set; }
    }

    public sealed class SelectableEventFrame : ObservableObject
    {
        bool selected;
        public Frame Frame { get; set; }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
        public int SelectionOrder { get; set; }
    }

    public sealed class EventFramePickerViewModel : PageViewModel
    {
        readonly IFrameService frames;
        readonly IFrameEventService categories;
        readonly INavigationService navigation;
        readonly ILogger<EventFramePickerViewModel> log;
        readonly List<SelectableEventFrame> all = new List<SelectableEventFrame>();
        Action<IReadOnlyList<Frame>> applied;
        EventFrameCategory selectedCategory;
        string search, message;
        int nextOrder;

        public EventFramePickerViewModel(IFrameService frameService, IFrameEventService categoryService,
            INavigationService navigationService, ILogger<EventFramePickerViewModel> logger)
        {
            frames = frameService; categories = categoryService; navigation = navigationService; log = logger;
            ToggleCommand = new RelayCommand(Toggle);
            AddAllCategoryCommand = new RelayCommand(_ => AddAllCategory());
            ApplyCommand = new RelayCommand(_ => Apply());
            CancelCommand = new RelayCommand(_ => navigation.Navigate("events"));
        }

        public override string Title => "Chọn frame cho event";
        public ObservableCollection<EventFrameCategory> Categories { get; } = new ObservableCollection<EventFrameCategory>();
        public ObservableCollection<SelectableEventFrame> VisibleFrames { get; } = new ObservableCollection<SelectableEventFrame>();
        public EventFrameCategory SelectedCategory { get => selectedCategory; set { if (Set(ref selectedCategory, value)) Filter(); } }
        public string Search { get => search; set { if (Set(ref search, value)) Filter(); } }
        public string Message { get => message; private set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public int SelectedCount => all.Count(x => x.IsSelected);
        public string SelectionSummary => SelectedCount + " / 10 frame đã chọn";
        public ICommand ToggleCommand { get; }
        public ICommand AddAllCategoryCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand CancelCommand { get; }

        public async Task OpenAsync(IReadOnlyCollection<Guid> selectedIds, Action<IReadOnlyList<Frame>> onApplied)
        {
            try
            {
                applied = onApplied; all.Clear(); Categories.Clear(); VisibleFrames.Clear(); nextOrder = 0;
                var ids = new HashSet<Guid>(selectedIds ?? new Guid[0]);
                foreach (var frame in await frames.GetAllAsync(CancellationToken.None))
                {
                    var item = new SelectableEventFrame { Frame = frame, IsSelected = ids.Contains(frame.Id) };
                    if (item.IsSelected) item.SelectionOrder = nextOrder++;
                    all.Add(item);
                }
                Categories.Add(new EventFrameCategory { Name = "Tất cả frame", FrameCount = all.Count, IsAll = true });
                Categories.Add(new EventFrameCategory { Name = "Chưa phân loại", FrameCount = all.Count(x => !x.Frame.EventId.HasValue), IsUncategorized = true });
                foreach (var category in (await categories.GetAllAsync(CancellationToken.None)).OrderBy(x => x.Name))
                    Categories.Add(new EventFrameCategory { Id = category.Id, Name = category.Name, FrameCount = all.Count(x => x.Frame.EventId == category.Id) });
                SelectedCategory = Categories.FirstOrDefault();
                Message = null; RaiseSelection();
            }
            catch (Exception error) { log.LogError(error, "Không thể mở thư viện frame"); Message = "Không thể mở thư viện frame: " + error.Message; }
        }

        void Toggle(object parameter)
        {
            var item = parameter as SelectableEventFrame;
            if (item == null) return;
            if (!item.IsSelected && SelectedCount >= 10) { Message = "Mỗi event chỉ được chọn tối đa 10 frame."; return; }
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected) item.SelectionOrder = nextOrder++;
            Message = null; RaiseSelection();
        }

        void AddAllCategory()
        {
            var candidates = Filtered().Where(x => !x.IsSelected).ToList();
            var capacity = 10 - SelectedCount;
            foreach (var item in candidates.Take(capacity)) { item.IsSelected = true; item.SelectionOrder = nextOrder++; }
            Message = candidates.Count > capacity ? "Đã thêm đủ 10 frame; các frame còn lại không được chọn." : "Đã thêm toàn bộ frame trong nhóm đang xem.";
            RaiseSelection();
        }

        void Apply()
        {
            var values = all.Where(x => x.IsSelected).OrderBy(x => x.SelectionOrder).Select(x => x.Frame).ToList();
            if (values.Count == 0) { Message = "Hãy chọn ít nhất một frame."; return; }
            applied?.Invoke(values);
            navigation.Navigate("events");
        }

        void Filter()
        {
            VisibleFrames.Clear();
            foreach (var item in Filtered()) VisibleFrames.Add(item);
        }

        IEnumerable<SelectableEventFrame> Filtered()
        {
            var query = all.AsEnumerable();
            if (SelectedCategory?.IsUncategorized == true) query = query.Where(x => !x.Frame.EventId.HasValue);
            else if (SelectedCategory != null && !SelectedCategory.IsAll) query = query.Where(x => x.Frame.EventId == SelectedCategory.Id);
            if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(x => (x.Frame.Name ?? string.Empty).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0);
            return query.OrderByDescending(x => x.IsSelected).ThenByDescending(x => x.Frame.IsPinned).ThenByDescending(x => x.Frame.CreatedAtUtc).ToList();
        }

        void RaiseSelection() { Raise(nameof(SelectedCount)); Raise(nameof(SelectionSummary)); }
    }
}
