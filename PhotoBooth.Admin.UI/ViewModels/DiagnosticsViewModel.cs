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
        readonly IRemoteDeliveryQueue remoteDelivery;
        readonly IQrCodeService qr;
        CaptureLibrarySnapshot snapshot = EmptySnapshot();
        IReadOnlyList<CaptureCardViewModel> captures = new CaptureCardViewModel[0];
        IReadOnlyList<string> eventSuggestions = new string[0];
        CaptureFilterOption selectedFilter;
        CaptureCardViewModel selectedCapture;
        IReadOnlyList<CaptureMediaViewModel> selectedMedia = new CaptureMediaViewModel[0];
        DateTime? selectedDate = DateTime.Today;
        string filterText;
        string error;
        string syncMessage;
        bool loading;
        int searchGeneration;
        byte[] selectedQrBytes;
        string selectedQrUrl;

        public DiagnosticsViewModel(IStatsRepository statistics, IStorageManager storage, IMediaThumbnailService thumbnails, IRemoteDeliveryQueue remoteDelivery, IQrCodeService qr)
        {
            this.statistics = statistics;
            this.storage = storage;
            this.thumbnails = thumbnails;
            this.remoteDelivery = remoteDelivery;
            this.qr = qr;
            FilterOptions = new[]
            {
                new CaptureFilterOption(CaptureLibraryFilterModes.All, "Tất cả lượt chụp"),
                new CaptureFilterOption(CaptureLibraryFilterModes.Date, "Theo ngày"),
                new CaptureFilterOption(CaptureLibraryFilterModes.Event, "Theo sự kiện"),
                new CaptureFilterOption(CaptureLibraryFilterModes.Session, "Theo lượt chụp")
            };
            selectedFilter = FilterOptions[0];
            SearchCommand = new AsyncCommand(_ => SearchAsync());
            RetrySyncCommand = new AsyncCommand(_ => RetrySyncAsync());
            CloseCaptureCommand = new RelayCommand(_ => CloseCapture());
            OpenMediaCommand = new RelayCommand(OpenMedia);
            CloseQrCommand = new RelayCommand(_ => { SelectedQrBytes=null;SelectedQrUrl=null; });
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
        public string FilterHint => IsSessionFilter ? "Nhập mã lượt chụp" : "Nhập hoặc chọn tên sự kiện";

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
        public string ReadyShareCountText => (Snapshot?.ReadyShareCount ?? 0).ToString("N0");
        public string PendingShareCountText => (Snapshot?.PendingShareCount ?? 0).ToString("N0");
        public string FailedShareCountText => (Snapshot?.FailedShareCount ?? 0).ToString("N0");
        public string LocalOnlyShareCountText => (Snapshot?.LocalOnlyShareCount ?? 0).ToString("N0");
        public bool HasRecoverableUploads => (Snapshot?.RecoverableShareCount ?? 0) > 0;
        public string RecoveryTitle => "Có " + (Snapshot?.RecoverableShareCount ?? 0).ToString("N0") + " lượt chụp cần tiếp tục";
        public string RecoveryDescription => "Bạn chỉ cần bấm một lần. MiuManager sẽ khôi phục toàn bộ lượt bị gián đoạn trên thiết bị này.";
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
        public string SyncMessage { get => syncMessage; private set { if(Set(ref syncMessage,value)) Raise(nameof(HasSyncMessage)); } }
        public bool HasSyncMessage => !string.IsNullOrWhiteSpace(SyncMessage);
        public ICommand SearchCommand { get; }
        public ICommand RetrySyncCommand { get; }
        public ICommand CloseCaptureCommand { get; }
        public ICommand OpenMediaCommand { get; }
        public ICommand CloseQrCommand { get; }
        public byte[] SelectedQrBytes { get=>selectedQrBytes; private set { if(Set(ref selectedQrBytes,value))Raise(nameof(IsQrOpen)); } }
        public string SelectedQrUrl { get=>selectedQrUrl; private set=>Set(ref selectedQrUrl,value); }
        public bool IsQrOpen => SelectedQrBytes!=null;

        async Task ShowQrAsync(object parameter)
        {
            var card=parameter as CaptureCardViewModel;var value=card?.Value;
            if(value==null||string.IsNullOrWhiteSpace(value.GalleryUrl)){Error="Lượt chụp này chưa có liên kết QR. Hãy tải lại ảnh hoặc kiểm tra phương thức chia sẻ.";return;}
            Uri uri;if(!Uri.TryCreate(value.GalleryUrl,UriKind.Absolute,out uri)){Error="Liên kết chia sẻ của lượt chụp không hợp lệ.";return;}
            SelectedQrUrl=uri.AbsoluteUri;SelectedQrBytes=await qr.GeneratePngAsync(uri,420,CancellationToken.None);
        }

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
                var cards = (Snapshot.Captures ?? new CaptureLibraryItem[0]).Select(x => new CaptureCardViewModel(x,OpenCaptureAsync,ShowQrAsync,RetryCaptureSyncAsync)).ToList();
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

        async Task RetrySyncAsync()
        {
            try { SyncMessage=""; var count=await remoteDelivery.RetryPendingAsync(null,CancellationToken.None); SyncMessage=count>0?"Đã khôi phục "+count+" lượt chụp. Ảnh sẽ tiếp tục được tải tự động.":"Không có lượt chụp bị gián đoạn cần khôi phục."; await RefreshSyncStateAsync(); }
            catch(Exception){Error="Chưa thể tiếp tục tải ảnh. Hãy kiểm tra kết nối mạng rồi thử lại.";}
        }

        async Task RetryCaptureSyncAsync(object parameter)
        {
            var card=parameter as CaptureCardViewModel;
            if(card==null)return;
            try { SyncMessage=""; var count=await remoteDelivery.RetryPendingAsync(card.Value.CaptureId,CancellationToken.None); SyncMessage=count>0?"Đã khôi phục lượt chụp này. Ảnh sẽ tiếp tục được tải tự động.":"Lượt chụp này không có phần tải lên bị gián đoạn."; await RefreshSyncStateAsync(); }
            catch(Exception){Error="Chưa thể tải lại ảnh của lượt chụp này. Hãy kiểm tra kết nối mạng.";}
        }

        async Task RefreshSyncStateAsync()
        {
            for(var attempt=0;attempt<4;attempt++)
            {
                if(attempt>0)await Task.Delay(1000);
                await SearchAsync();
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
            Raise(nameof(ReadyShareCountText));
            Raise(nameof(PendingShareCountText));
            Raise(nameof(FailedShareCountText));
            Raise(nameof(LocalOnlyShareCountText));
            Raise(nameof(HasRecoverableUploads));
            Raise(nameof(RecoveryTitle));
            Raise(nameof(RecoveryDescription));
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
        public CaptureCardViewModel(CaptureLibraryItem value,Func<object,Task> open,Func<object,Task> showQr,Func<object,Task> retry)
        {
            Value=value;
            OpenCommand=new AsyncCommand(_=>open(this));
            ShowQrCommand=new AsyncCommand(_=>showQr(this),_=>HasQrAction);
            RetryCommand=new AsyncCommand(_=>retry(this),_=>HasRetryAction);
        }
        public CaptureLibraryItem Value { get; }
        public string Title => "Lượt chụp " + Value.CreatedAtLocal.ToString("dd/MM/yyyy HH:mm");
        public string CaptureIdText => "Mã ảnh: " + Value.CaptureId;
        public string EventText => Value.EventName;
        public string SessionText => "Mã lượt chụp: " + Value.SessionDisplayCode;
        public string MediaText => Value.PictureCount + " ảnh  ·  " + Value.VideoCount + " video  ·  " + Value.GifCount + " GIF";
        public string SyncStatusText => SyncStatus(Value.Status);
        public string SyncStatusHint => HasQrAction?"Khách có thể quét lại mã QR để nhận ảnh.":HasFailed?"Tải ảnh chưa hoàn tất. Có thể khôi phục mà không ảnh hưởng ảnh gốc.":HasPending?"Ứng dụng đang tự tải ảnh ở chế độ nền.":"Ảnh hiện chỉ được lưu trên máy này.";
        public string StatusBackground => HasQrAction?"#1E4032":HasFailed?"#47262D":HasPending?"#44351F":"#232A37";
        public string StatusForeground => HasQrAction?"#8FE0B4":HasFailed?"#FFB7BF":HasPending?"#FFD08A":"#C3CAD5";
        public string SyncDetailText => Value.FailedSyncCount>0?Value.FailedSyncCount+" tác vụ thất bại":Value.PendingSyncCount>0?Value.PendingSyncCount+" tác vụ đang chờ":!string.IsNullOrWhiteSpace(Value.GalleryUrl)?"Đã có liên kết web":"";
        public string SyncErrorText => Value.LastSyncError;
        public bool HasQrAction => !string.IsNullOrWhiteSpace(Value.GalleryUrl);
        public bool HasFailed => Value.FailedSyncCount>0||string.Equals(Value.Status,"Failed",StringComparison.Ordinal);
        public bool HasPending => Value.PendingSyncCount>0||string.Equals(Value.Status,"QueuedForUpload",StringComparison.Ordinal)||string.Equals(Value.Status,"Uploading",StringComparison.Ordinal)||string.Equals(Value.Status,"Uploaded",StringComparison.Ordinal)||string.Equals(Value.Status,"RetryWaiting",StringComparison.Ordinal);
        public bool HasRetryAction => HasFailed||string.Equals(Value.Status,"RetryWaiting",StringComparison.Ordinal);
        public ICommand OpenCommand { get; }
        public ICommand ShowQrCommand { get; }
        public ICommand RetryCommand { get; }
        static string SyncStatus(string value)
        {
            switch(value){case "QueuedForUpload":return "Đang chờ tải ảnh";case "Uploading":return "Đang tải ảnh";case "Uploaded":return "Đang tạo liên kết nhận ảnh";case "GalleryReady":return "Sẵn sàng nhận ảnh";case "RetryWaiting":return "Đang chờ kết nối ổn định";case "Failed":return "Cần tải lại";case "Ready":return "Chỉ lưu trên máy";default:return "Chưa chia sẻ";}
        }
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
