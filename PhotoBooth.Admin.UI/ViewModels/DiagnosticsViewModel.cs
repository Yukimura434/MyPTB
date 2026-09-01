using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class DiagnosticsViewModel : PageViewModel
    {
        readonly IStatsRepository statistics;
        readonly IStorageManager storage;
        readonly IMediaThumbnailService thumbnails;
        CaptureLibrarySnapshot snapshot = EmptySnapshot();
        IReadOnlyList<CaptureCardViewModel> captures = new CaptureCardViewModel[0];
        IReadOnlyList<string> eventSuggestions = new string[0];
        CaptureFilterOption selectedFilter;
        CaptureCardViewModel selectedCapture;
        IReadOnlyList<CaptureMediaViewModel> selectedMedia = new CaptureMediaViewModel[0];
        DateTime? selectedDate = DateTime.Today;
        string filterText;
        string error;
        bool loading;
        int searchGeneration;

        public DiagnosticsViewModel(IStatsRepository statistics, IStorageManager storage, IMediaThumbnailService thumbnails)
        {
            this.statistics = statistics;
            this.storage = storage;
            this.thumbnails = thumbnails;
            FilterOptions = new[]
            {
                new CaptureFilterOption(CaptureLibraryFilterModes.All, "Tất cả lượt chụp"),
                new CaptureFilterOption(CaptureLibraryFilterModes.Date, "Theo ngày"),
                new CaptureFilterOption(CaptureLibraryFilterModes.Event, "Theo sự kiện"),
                new CaptureFilterOption(CaptureLibraryFilterModes.Session, "Theo session")
            };
            selectedFilter = FilterOptions[0];
            SearchCommand = new AsyncCommand(_ => SearchAsync());
            OpenCaptureCommand = new AsyncCommand(OpenCaptureAsync);
            CloseCaptureCommand = new RelayCommand(_ => CloseCapture());
            OpenMediaCommand = new RelayCommand(OpenMedia);
            _ = InitializeAsync();
        }

        public override string Title => "Dữ liệu & thống kê";
        public IReadOnlyList<CaptureFilterOption> FilterOptions { get; }

        public CaptureFilterOption SelectedFilter
        {
            get => selectedFilter;
            set
            {
                if (!Set(ref selectedFilter, value)) return;
                Error = null;
                Raise(nameof(IsDateFilter));
                Raise(nameof(IsEventFilter));
                Raise(nameof(IsSessionFilter));
                Raise(nameof(IsTextFilter));
                Raise(nameof(FilterHint));
            }
        }

        public bool IsDateFilter => string.Equals(SelectedFilter?.Mode, CaptureLibraryFilterModes.Date, StringComparison.Ordinal);
        public bool IsEventFilter => string.Equals(SelectedFilter?.Mode, CaptureLibraryFilterModes.Event, StringComparison.Ordinal);
        public bool IsSessionFilter => string.Equals(SelectedFilter?.Mode, CaptureLibraryFilterModes.Session, StringComparison.Ordinal);
        public bool IsTextFilter => IsEventFilter || IsSessionFilter;
        public string FilterHint => IsSessionFilter ? "Nhập Session ID hoặc mã lượt khách" : "Nhập hoặc chọn tên sự kiện";

        public DateTime? SelectedDate { get => selectedDate; set => Set(ref selectedDate, value); }
        public string FilterText { get => filterText; set => Set(ref filterText, value); }
        public IReadOnlyList<string> EventSuggestions { get => eventSuggestions; private set => Set(ref eventSuggestions, value); }
        public CaptureLibrarySnapshot Snapshot { get => snapshot; private set { if (Set(ref snapshot, value)) RaiseSummary(); } }
        public IReadOnlyList<CaptureCardViewModel> Captures { get => captures; private set { if (Set(ref captures, value)) Raise(nameof(ResultText)); } }
        public string CaptureCountText => (Snapshot?.CaptureCount ?? 0).ToString("N0");
        public string PrintedPhotoCountText => (Snapshot?.PrintedPhotoCount ?? 0).ToString("N0");
        public string ExtraPrintCountText => (Snapshot?.ExtraPrintCount ?? 0).ToString("N0");
        public string RevenueText => Snapshot != null && Snapshot.HasRevenueData ? Snapshot.RevenueAmount.ToString("N0") + " ₫" : "—";
        public string RevenueHint => Snapshot != null && Snapshot.HasRevenueData ? "Theo bộ lọc hiện tại" : "Chờ cấu hình thanh toán";
        public string ResultText => Snapshot == null ? string.Empty : Captures.Count < Snapshot.CaptureCount
            ? "Hiển thị " + Captures.Count.ToString("N0") + " / " + Snapshot.CaptureCount.ToString("N0") + " lượt chụp gần nhất"
            : Snapshot.CaptureCount.ToString("N0") + " lượt chụp";

        public CaptureCardViewModel SelectedCapture
        {
            get => selectedCapture;
            private set
            {
                if (!Set(ref selectedCapture, value)) return;
                Raise(nameof(IsCaptureOpen));
                Raise(nameof(SelectedCaptureTitle));
            }
        }

        public bool IsCaptureOpen => SelectedCapture != null;
        public string SelectedCaptureTitle => SelectedCapture == null ? string.Empty : SelectedCapture.Title;
        public IReadOnlyList<CaptureMediaViewModel> SelectedMedia { get => selectedMedia; private set => Set(ref selectedMedia, value); }
        public bool IsLoading { get => loading; private set => Set(ref loading, value); }
        public string Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrWhiteSpace(Error);
        public ICommand SearchCommand { get; }
        public ICommand OpenCaptureCommand { get; }
        public ICommand CloseCaptureCommand { get; }
        public ICommand OpenMediaCommand { get; }

        async Task InitializeAsync()
        {
            try { EventSuggestions = await statistics.GetEventSuggestionsAsync(CancellationToken.None); }
            catch (Exception exception) { Error = "Không thể tải danh sách sự kiện: " + exception.Message; }
            await SearchAsync();
        }

        async Task SearchAsync()
        {
            var generation = Interlocked.Increment(ref searchGeneration);
            try
            {
                IsLoading = true;
                Error = null;
                var filter = CreateFilter();
                if (filter == null) return;
                var result = await statistics.SearchCaptureLibraryAsync(filter, CancellationToken.None);
                if (generation != searchGeneration) return;
                Snapshot = result ?? EmptySnapshot();
                var cards = (Snapshot.Captures ?? new CaptureLibraryItem[0]).Select(x => new CaptureCardViewModel(x)).ToList();
                Captures = cards;
                CloseCapture();
                await LoadCardThumbnailsAsync(cards, generation);
            }
            catch (Exception exception)
            {
                Error = "Không thể đọc thư viện lượt chụp: " + exception.Message;
            }
            finally
            {
                if (generation == searchGeneration) IsLoading = false;
            }
        }

        CaptureLibraryFilter CreateFilter()
        {
            var mode = SelectedFilter?.Mode ?? CaptureLibraryFilterModes.All;
            var filter = new CaptureLibraryFilter { Mode = mode, Query = FilterText, MaximumItems = 250 };
            if (!string.Equals(mode, CaptureLibraryFilterModes.Date, StringComparison.Ordinal)) return filter;
            if (!SelectedDate.HasValue)
            {
                Error = "Hãy chọn hoặc nhập ngày theo định dạng ngày/tháng/năm.";
                return null;
            }
            var localStart = DateTime.SpecifyKind(SelectedDate.Value.Date, DateTimeKind.Local);
            filter.FromUtc = localStart.ToUniversalTime();
            filter.ToUtc = localStart.AddDays(1).ToUniversalTime();
            return filter;
        }

        async Task LoadCardThumbnailsAsync(IReadOnlyList<CaptureCardViewModel> cards, int generation)
        {
            foreach (var card in cards)
            {
                if (generation != searchGeneration) return;
                var path = ResolvePath(card.Value.ThumbnailManagedRelativePath, card.Value.ThumbnailPath);
                if (path == null) continue;
                try { card.ThumbnailBytes = await thumbnails.CreateAsync(path, 360, CancellationToken.None); }
                catch (Exception) { card.IsMediaMissing = true; }
            }
        }

        async Task OpenCaptureAsync(object parameter)
        {
            var card = parameter as CaptureCardViewModel;
            if (card == null) return;
            try
            {
                IsLoading = true;
                Error = null;
                var media = await statistics.GetCaptureMediaAsync(card.Value.CaptureId, CancellationToken.None);
                var items = media.Select(x => new CaptureMediaViewModel(x, ResolvePath(x.ManagedRelativePath, x.LocalPath))).ToList();
                SelectedCapture = card;
                SelectedMedia = items;
                foreach (var item in items)
                {
                    if (item.FilePath == null) { item.IsMissing = true; continue; }
                    try { item.ThumbnailBytes = await thumbnails.CreateAsync(item.FilePath, 720, CancellationToken.None); }
                    catch (Exception) { item.IsMissing = true; }
                }
            }
            catch (Exception exception)
            {
                Error = "Không thể mở lượt chụp: " + exception.Message;
            }
            finally { IsLoading = false; }
        }

        void CloseCapture()
        {
            SelectedCapture = null;
            SelectedMedia = new CaptureMediaViewModel[0];
        }

        void OpenMedia(object parameter)
        {
            var media = parameter as CaptureMediaViewModel;
            if (media == null || string.IsNullOrWhiteSpace(media.FilePath) || !File.Exists(media.FilePath))
            {
                Error = "Tệp media không còn tồn tại trong kho dữ liệu.";
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = media.FilePath, UseShellExecute = true, Verb = "open" });
            }
            catch (Exception exception)
            {
                Error = "Windows không thể mở tệp này: " + exception.Message;
            }
        }

        string ResolvePath(string managedRelativePath, string legacyPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(managedRelativePath)) return storage.GetFullPath(managedRelativePath);
                if (!string.IsNullOrWhiteSpace(legacyPath) && Path.IsPathRooted(legacyPath)) return Path.GetFullPath(legacyPath);
            }
            catch (Exception) { }
            return null;
        }

        void RaiseSummary()
        {
            Raise(nameof(CaptureCountText));
            Raise(nameof(PrintedPhotoCountText));
            Raise(nameof(ExtraPrintCountText));
            Raise(nameof(RevenueText));
            Raise(nameof(RevenueHint));
            Raise(nameof(ResultText));
        }

        static CaptureLibrarySnapshot EmptySnapshot() => new CaptureLibrarySnapshot { Captures = new CaptureLibraryItem[0] };
    }

    public sealed class CaptureFilterOption
    {
        public CaptureFilterOption(string mode, string label) { Mode = mode; Label = label; }
        public string Mode { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    public sealed class CaptureCardViewModel : ObservableObject
    {
        byte[] thumbnailBytes;
        bool mediaMissing;
        public CaptureCardViewModel(CaptureLibraryItem value) { Value = value; }
        public CaptureLibraryItem Value { get; }
        public string Title => "Lượt chụp " + Value.CreatedAtLocal.ToString("dd/MM/yyyy HH:mm");
        public string CaptureIdText => "Capture ID: " + Value.CaptureId;
        public string EventText => Value.EventName;
        public string SessionText => "Session: " + Value.SessionDisplayCode + "  ·  " + Value.SessionId.ToString();
        public string MediaText => Value.PictureCount + " ảnh  ·  " + Value.VideoCount + " video  ·  " + Value.GifCount + " GIF";
        public byte[] ThumbnailBytes { get => thumbnailBytes; set => Set(ref thumbnailBytes, value); }
        public bool IsMediaMissing { get => mediaMissing; set => Set(ref mediaMissing, value); }
    }

    public sealed class CaptureMediaViewModel : ObservableObject
    {
        byte[] thumbnailBytes;
        bool missing;
        public CaptureMediaViewModel(CaptureLibraryMedia value, string filePath) { Value = value; FilePath = filePath; }
        public CaptureLibraryMedia Value { get; }
        public string FilePath { get; }
        public string Label
        {
            get
            {
                switch (Value.Role)
                {
                    case CaptureAssetTypes.Picture: return "Ảnh gốc " + Value.Position;
                    case CaptureAssetTypes.Video: return "Video gốc " + Value.Position;
                    case CaptureAssetTypes.Composite: return "Ảnh đã ghép frame";
                    case CaptureAssetTypes.CompositeVideo: return "Video đã ghép frame";
                    case CaptureAssetTypes.Gif: return "GIF";
                    default: return "Media";
                }
            }
        }
        public string TypeIcon => string.Equals(Value.Role, CaptureAssetTypes.Video, StringComparison.Ordinal) || string.Equals(Value.Role, CaptureAssetTypes.CompositeVideo, StringComparison.Ordinal) ? "▶" : "";
        public bool IsVideo => string.Equals(Value.Role, CaptureAssetTypes.Video, StringComparison.Ordinal) || string.Equals(Value.Role, CaptureAssetTypes.CompositeVideo, StringComparison.Ordinal);
        public byte[] ThumbnailBytes { get => thumbnailBytes; set => Set(ref thumbnailBytes, value); }
        public bool IsMissing { get => missing; set => Set(ref missing, value); }
    }
}
