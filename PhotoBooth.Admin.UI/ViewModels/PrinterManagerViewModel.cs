using System;
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
    public sealed class PrinterManagerViewModel : PageViewModel
    {
        readonly IPrinterService service;
        readonly ISettingsService settings;
        readonly ILogger<PrinterManagerViewModel> log;
        DiscoveredPrinter selectedPrinter, connectedPrinter;
        PrinterProfile editing;
        string status = "Chưa kết nối", message;
        bool printingEnabled = true, loadingPrintingSetting, busy, dirty, savedDefault, savedProfile;

        public PrinterManagerViewModel(IPrinterService service, ISettingsService settings, ILogger<PrinterManagerViewModel> log)
        {
            this.service = service; this.settings = settings; this.log = log;
            ScanCommand = new AsyncCommand(_ => Scan(), _ => !IsBusy);
            ConnectCommand = new AsyncCommand(_ => Connect(), _ => !IsBusy && SelectedPrinter != null);
            SaveCommand = new AsyncCommand(_ => Save(false), _ => CanSave);
            SaveAndTestCommand = new AsyncCommand(_ => Save(true), _ => CanSave);
            _ = Initialize();
        }
        public override string Title => "Máy in";
        public ObservableCollection<DiscoveredPrinter> Printers { get; } = new ObservableCollection<DiscoveredPrinter>();
        public ObservableCollection<string> PaperSizes { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> MediaTypes { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> PaperTypes { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Qualities { get; } = new ObservableCollection<string>();
        public int[] Copies { get; } = { 1, 2, 3, 4 };
        public DiscoveredPrinter SelectedPrinter { get => selectedPrinter; set { if (Set(ref selectedPrinter, value)) { Message = null; RaiseState(); } } }
        public PrinterProfile EditingProfile => editing;
        public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) RaiseState(); } }
        public bool CanSelectPrinter => !IsBusy;
        public bool HasEditingProfile => editing != null;
        public bool IsEmpty => !IsBusy && Printers.Count == 0;
        public bool NeedsConnection => !HasEditingProfile;
        public bool IsSelectionConnected => connectedPrinter != null && string.Equals(SelectedPrinter?.Id, connectedPrinter.Id, StringComparison.OrdinalIgnoreCase);
        public bool CanEdit => HasEditingProfile && IsSelectionConnected && !IsBusy;
        public bool CanSave => CanEdit && PaperSizes.Contains(PaperSize) && Copies.Contains(DefaultCopies);
        public bool HasPaperSizes => PaperSizes.Count > 0;
        public bool HasPaperTypes => PaperTypes.Count > 0;
        public bool HasQualities => Qualities.Count > 0;
        public bool SupportsColor => connectedPrinter?.SupportsColor == true;
        public bool IsSavedDefault => HasEditingProfile && savedDefault;
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public string Message { get => message; private set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public string PrinterStatus { get => status; private set => Set(ref status, value); }
        public string PrinterSummary => Printers.Count.ToString();
        public string ConfigurationName => connectedPrinter?.Name ?? "Chưa kết nối máy in";
        public string ConfigurationCaption => !HasEditingProfile ? "Kết nối để tải các tùy chọn in" : IsSelectionConnected ? "Đang cấu hình máy đã kết nối" : "Cấu hình của máy đã kết nối trước đó";
        public string ConnectionText => ConnectionLabel(connectedPrinter?.ConnectionType);
        public string ColorSupportText => SupportsColor ? "Có" : "Không";
        public string DuplexSupportText => connectedPrinter?.SupportsDuplex == true ? "Có" : "Không";
        public string ConnectLabel => IsBusy ? "Đang xử lý…" : IsSelectionConnected ? "Kết nối lại" : "Kết nối máy đã chọn";
        public string ConnectionHint => SelectedPrinter == null ? "Chọn một máy in để kết nối." : IsSelectionConnected ? "Máy này đã được kết nối để cấu hình." : "Đã chọn " + SelectedPrinter.Name + ". Bấm kết nối để tải cấu hình.";
        public string PaperHelp => HasPaperSizes ? "Chọn đúng khổ giấy đang dùng." : "Kiểm tra thiết lập máy in trong Windows rồi kết nối lại.";
        public string SaveState => !HasEditingProfile ? "Chưa có cấu hình" : dirty ? "Có thay đổi chưa lưu" : savedProfile ? "Cấu hình đã lưu" : "Chưa lưu cấu hình";
        public string PrintingHelp => PrintingEnabled ? "Đang bật · Áp dụng từ lượt chụp tiếp theo" : "Đang tắt · Các lượt chụp tiếp theo sẽ không in";
        public bool PrintingEnabled { get => printingEnabled; set { if (!Set(ref printingEnabled, value)) return; Raise(nameof(PrintingHelp)); if (!loadingPrintingSetting) _ = SavePrintingSetting(value); } }
        public string PaperSize { get => editing?.PaperSize; set { if (editing == null || editing.PaperSize == value) return; editing.PaperSize = value; Edited(); } }
        public string PaperType { get => editing?.PaperType; set { if (editing == null || editing.PaperType == value) return; editing.PaperType = value; Edited(); } }
        public string MediaType { get => editing?.MediaType; set { if (editing == null || editing.MediaType == value) return; editing.MediaType = value; Edited(); } }
        public string Quality { get => editing?.Quality; set { if (editing == null || editing.Quality == value) return; editing.Quality = value; Edited(); } }
        public int DefaultCopies { get => editing?.DefaultCopies ?? 1; set { if (editing == null || editing.DefaultCopies == value) return; editing.DefaultCopies = value; Edited(); } }
        public bool Landscape { get => editing?.Landscape == true; set { if (editing == null || editing.Landscape == value) return; editing.Landscape = value; Edited(); } }
        public bool PrintInColor { get => editing?.PrintInColor == true; set { if (editing == null || editing.PrintInColor == value) return; editing.PrintInColor = value && SupportsColor; Edited(); } }
        public bool IsDefault { get => editing?.IsDefault == true; set { if (editing == null || editing.IsDefault == value) return; editing.IsDefault = value; Edited(); } }
        public ICommand ScanCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand SaveAndTestCommand { get; }
        async Task Initialize()
        {
            Settings configured = null;
            IsBusy = true;
            try { loadingPrintingSetting = true; configured = await settings.GetAsync(CancellationToken.None); PrintingEnabled = configured?.PrintingEnabled ?? true; }
            catch (Exception e) { Fail(e, "Không thể tải cài đặt in. Vui lòng thử lại."); }
            finally { loadingPrintingSetting = false; IsBusy = false; }
            await Scan();
            if (!PrintingEnabled) return;
            IsBusy = true;
            try
            {
                var profiles = await service.GetProfilesAsync(CancellationToken.None);
                var profile = configured?.DefaultPrinterProfileId.HasValue == true
                    ? profiles.FirstOrDefault(x => x.Id == configured.DefaultPrinterProfileId.Value)
                    : profiles.FirstOrDefault(x => x.IsDefault);
                var device = profile == null ? null : Printers.FirstOrDefault(x => string.Equals(x.Id, profile.PrinterId, StringComparison.OrdinalIgnoreCase));
                if (device == null) return;
                SelectedPrinter = device;
                await Connect();
            }
            catch (Exception e) { Fail(e, "Chưa thể kết nối máy in mặc định. Vui lòng kiểm tra rồi kết nối lại."); }
            finally { IsBusy = false; }
        }
        async Task SavePrintingSetting(bool enabled)
        {
            IsBusy = true;
            try { var value = await settings.GetAsync(CancellationToken.None) ?? new Settings(); value.PrintingEnabled = enabled; await settings.SaveAsync(value, CancellationToken.None); Message = enabled ? "Đã bật in cho các lượt chụp tiếp theo." : "Đã tắt in cho các lượt chụp tiếp theo."; }
            catch (Exception e) { printingEnabled = !enabled; Raise(nameof(PrintingEnabled)); Raise(nameof(PrintingHelp)); Fail(e, "Không thể lưu trạng thái in. Vui lòng thử lại."); }
            finally { IsBusy = false; }
        }
        async Task Scan()
        {
            var selectedId = SelectedPrinter?.Id;
            IsBusy = true; Message = null; PrinterStatus = "Đang quét máy in…";
            connectedPrinter = null; editing = null; Printers.Clear(); RaiseState();
            try
            {
                foreach (var device in await service.ScanAsync(CancellationToken.None)) Printers.Add(device);
                SelectedPrinter = Printers.FirstOrDefault(x => x.Id == selectedId) ?? Printers.FirstOrDefault();
                PrinterStatus = Printers.Count == 0 ? "Chưa tìm thấy máy in. Kiểm tra máy trong Windows rồi quét lại." : Printers.Count + " máy in được phát hiện";
            }
            catch (Exception e) { Fail(e, "Không thể quét máy in. Vui lòng thử lại."); }
            finally { IsBusy = false; RaiseState(); }
        }
        async Task Connect()
        {
            var requested = SelectedPrinter;
            if (requested == null) return;
            IsBusy = true; Message = null; PrinterStatus = "Đang kết nối…";
            connectedPrinter = null; editing = null; RaiseState();
            try
            {
                var device = await service.ConnectAsync(requested.Id, CancellationToken.None);
                var saved = (await service.GetProfilesAsync(CancellationToken.None)).FirstOrDefault(x => string.Equals(x.PrinterId, device.Id, StringComparison.OrdinalIgnoreCase));
                // Edit a detached copy; changing controls must not alter an active service profile before Save.
                var profile = new PrinterProfile { Id = saved?.Id ?? Guid.NewGuid(), Name = device.Name, PrinterName = device.Name, PrinterId = device.Id,
                    PaperSize = saved?.PaperSize, PaperType = saved?.PaperType, MediaType = saved?.MediaType, Quality = saved?.Quality, DefaultCopies = Math.Max(1, Math.Min(4, saved?.DefaultCopies ?? 1)),
                    Landscape = saved?.Landscape ?? false, PrintInColor = device.SupportsColor && (saved?.PrintInColor ?? true), IsDefault = saved?.IsDefault ?? false, UseDefaultBorder = false };
                Fill(MediaTypes, new[] { "Mặc định của máy in" }.Concat(device.MediaTypes ?? new string[0]).ToArray());
                profile.MediaType = string.IsNullOrWhiteSpace(profile.MediaType) ? "Mặc định của máy in" : profile.MediaType;
                if (!MediaTypes.Contains(profile.MediaType)) MediaTypes.Add(profile.MediaType);
                Fill(PaperSizes, device.PaperSizes); Fill(PaperTypes, device.PaperSources); Fill(Qualities, device.Resolutions);
                profile.PaperSize = PaperSizes.FirstOrDefault(x => PrinterPaperNames.Match(x, profile.PaperSize)) ?? PaperSizes.FirstOrDefault();
                profile.PaperType = Choose(profile.PaperType, PaperTypes); profile.Quality = Choose(profile.Quality, Qualities);
                connectedPrinter = device; editing = profile; savedProfile = saved != null; savedDefault = saved?.IsDefault == true; dirty = false;
                PrinterStatus = "Đã kết nối " + device.Name;
                if (!HasPaperSizes) Message = "Máy in chưa cung cấp khổ giấy. Kiểm tra trong Windows rồi kết nối lại.";
            }
            catch (Exception e) { Fail(e, "Không thể kết nối máy in. Kiểm tra máy trong Windows rồi thử lại."); }
            finally { IsBusy = false; RaiseFields(); }
        }
        async Task Save(bool test)
        {
            if (!CanSave) return;
            var profile = editing;
            IsBusy = true;
            try
            {
                profile.UseDefaultBorder = false; profile.PrintInColor &= SupportsColor;
                Message = "Đang lưu cấu hình…";
                await service.SaveProfileAsync(profile, CancellationToken.None);
                var app = await settings.GetAsync(CancellationToken.None) ?? new Settings();
                if (profile.IsDefault || app.DefaultPrinterProfileId == profile.Id)
                {
                    app.DefaultPrinterProfileId = profile.IsDefault ? (Guid?)profile.Id : null;
                    await settings.SaveAsync(app, CancellationToken.None);
                }
                var verified = (await service.GetProfilesAsync(CancellationToken.None)).FirstOrDefault(x => string.Equals(x.PrinterId, profile.PrinterId, StringComparison.OrdinalIgnoreCase));
                if (verified == null) throw new InvalidOperationException("Saved printer profile missing.");
                editing = verified; savedDefault = verified.IsDefault; savedProfile = true; dirty = false; RaiseFields();
                if (test) { Message = "Đang gửi trang in thử…"; await service.PrintAsync(CreateTestJob(verified), CancellationToken.None); }
                PrinterStatus = "Đã lưu cấu hình";
                Message = test ? "Đã lưu cấu hình và gửi 1 bản in thử." : "Đã lưu cấu hình máy in.";
            }
            catch (Exception e) { Fail(e, test ? "Không thể hoàn tất lưu hoặc in thử. Kiểm tra máy in trước khi thử lại." : "Không thể lưu cấu hình. Vui lòng thử lại."); }
            finally { IsBusy = false; }
        }
        static PrintJob CreateTestJob(PrinterProfile p) => new PrintJob { Id = Guid.NewGuid(), PrinterName = p.PrinterName, Copies = 1, PaperSize = p.PaperSize, PaperType = p.PaperType, MediaType = p.MediaType, Quality = p.Quality, Landscape = p.Landscape, UseDefaultBorder = false, PrintInColor = p.PrintInColor };
        static void Fill(ObservableCollection<string> target, string[] values) { target.Clear(); foreach (var value in (values ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()) target.Add(value); }
        static string Choose(string value, ObservableCollection<string> values) => values.Contains(value) ? value : values.FirstOrDefault();
        public static string ConnectionLabel(string value) => value == "Local network" ? "Mạng nội bộ" : value == "Windows printer" ? "Máy in Windows" : string.IsNullOrWhiteSpace(value) ? "—" : value;
        void Edited([System.Runtime.CompilerServices.CallerMemberName] string property = null) { dirty = true; Raise(property); RaiseState(); }
        void RaiseFields() { foreach (var property in new[] { nameof(EditingProfile), nameof(PaperSize), nameof(PaperType), nameof(MediaType), nameof(Quality), nameof(DefaultCopies), nameof(Landscape), nameof(PrintInColor), nameof(IsDefault) }) Raise(property); RaiseState(); }
        void RaiseState()
        {
            foreach (var property in new[] { nameof(CanSelectPrinter), nameof(HasEditingProfile), nameof(IsEmpty), nameof(NeedsConnection), nameof(IsSelectionConnected), nameof(CanEdit), nameof(CanSave), nameof(HasPaperSizes), nameof(HasPaperTypes), nameof(HasQualities), nameof(SupportsColor), nameof(IsSavedDefault), nameof(PrinterSummary), nameof(ConfigurationName), nameof(ConfigurationCaption), nameof(ConnectionText), nameof(ColorSupportText), nameof(DuplexSupportText), nameof(ConnectLabel), nameof(ConnectionHint), nameof(PaperHelp), nameof(SaveState) }) Raise(property);
            CommandManager.InvalidateRequerySuggested();
        }
        void Fail(Exception e, string text) { log.LogError(e, text); PrinterStatus = "Cần kiểm tra"; Message = text; }
    }
}
