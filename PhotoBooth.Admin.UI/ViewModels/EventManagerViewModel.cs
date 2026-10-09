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
    public sealed class EventPresetCard : ObservableObject
    {
        byte[] previewBytes;
        public Preset Preset { get; set; }
        public Guid Id => Preset.Id;
        public string Name => Preset.Name;
        public byte[] PreviewBytes { get => previewBytes; set => Set(ref previewBytes, value); }
    }

    public sealed class EventOption<T>
    {
        public string Label { get; set; }
        public T Value { get; set; }
        public override string ToString() => Label;
    }

    public sealed class EventManagerViewModel : PageViewModel
    {
        readonly IPhotoEventManagementService service;
        readonly IFrameService frames;
        readonly IPresetService presets;
        readonly IColorLutService colors;
        readonly EventFramePickerViewModel picker;
        readonly EventPresetPickerViewModel presetPicker;
        readonly IFileDialogService fileDialog;
        readonly INavigationService navigation;
        readonly ILogger<EventManagerViewModel> log;
        PhotoEvent selectedEvent;
        string createName = "Sự kiện mới", editName, outputDirectory, message;
        int photoCount = 8, countdown = 5, includedPrintCopies = 1, maximumPrintCopies = 10, colorPresetStrengthPercent = 50, smooth, brighten, tone, sharpen, eye, slim, blemish, darkCircle;
        long basePriceMinor, additionalPrintPriceMinor;
        long rowVersion;
        bool beautyEnabled, loading, dirty;
        EventOption<int> gifDuration, waitingTimeout, rotation;
        EventOption<CustomerLayoutMode> layout;
        EventOption<EventPrintPolicyMode> printPolicy;
        int loadVersion;
        readonly string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model.png");
        byte[] modelBytes;

        public EventManagerViewModel(IPhotoEventManagementService eventService, IFrameService frameService, IPresetService presetService,
            IColorLutService colorService, EventFramePickerViewModel framePicker, EventPresetPickerViewModel eventPresetPicker,
            INavigationService navigationService, IFileDialogService files, ILogger<EventManagerViewModel> logger)
        {
            service = eventService; frames = frameService; presets = presetService; colors = colorService; picker = framePicker; presetPicker = eventPresetPicker; navigation = navigationService; fileDialog = files; log = logger;
            try { if (File.Exists(modelPath)) modelBytes = File.ReadAllBytes(modelPath); } catch (Exception error) { log.LogWarning(error, "Unable to load preset model image"); }
            PhotoCounts = Enumerable.Range(1, 8).ToArray();
            Countdowns = Enumerable.Range(1, 10).ToArray();
            GifDurations = new[] { Option("0.4 giây", 400), Option("0.6 giây", 600), Option("0.8 giây", 800), Option("1.0 giây", 1000) };
            WaitingTimeouts = new[] { Option("30 giây", 30), Option("1 phút", 60), Option("2 phút", 120), Option("5 phút", 300), Option("10 phút", 600), Option("15 phút", 900) };
            Layouts = new[] { Option("Màn hình ngang", CustomerLayoutMode.Landscape), Option("Màn hình dọc", CustomerLayoutMode.Portrait) };
            Rotations = new[] { Option("Không xoay", 0), Option("Xoay phải 90°", 90), Option("Xoay 180°", 180), Option("Xoay trái 90°", -90) };
            PrintPolicies = new[] { Option("Không in", EventPrintPolicyMode.Disabled), Option("Khách chọn số bản", EventPrintPolicyMode.Manual), Option("In theo số bản đã đặt", EventPrintPolicyMode.Automatic) };
            CreateCommand = new AsyncCommand(_ => Create());
            SaveCommand = new AsyncCommand(_ => Save(false), _ => CanEditConfiguration && Dirty);
            ActivateCommand = new AsyncCommand(_ => Save(true), _ => SelectedEvent?.AcceptsNewBoothSessions == true && (!IsActive || Dirty));
            AdvanceLifecycleCommand = new AsyncCommand(_ => AdvanceLifecycle(), _ => CanAdvanceLifecycle);
            DeleteCommand = new AsyncCommand(_ => Delete(), _ => SelectedEvent?.LifecycleState == PhotoEventLifecycleStates.Draft && !SelectedEvent.IsSelectedForThisDevice && !SelectedEvent.IsSystemDefault);
            ChooseOutputDirectoryCommand = new AsyncCommand(_ => ChooseOutputDirectory(), _ => CanEditConfiguration);
            OpenFramePickerCommand = new AsyncCommand(_ => OpenFramePicker(), _ => CanEditConfiguration);
            RemoveFrameCommand = new RelayCommand(RemoveFrame);
            OpenPresetPickerCommand = new AsyncCommand(_ => OpenPresetPicker(), _ => CanEditConfiguration);
            RemovePresetCommand = new RelayCommand(RemovePreset);
            ReloadCommand = new AsyncCommand(_ => RefreshAsync(), _ => SelectedEvent != null && Dirty);
            _ = RefreshAsync();
        }

        public override string Title => "Sự kiện & vận hành";
        public ObservableCollection<PhotoEvent> Events { get; } = new ObservableCollection<PhotoEvent>();
        public ObservableCollection<Frame> SelectedFrames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<EventPresetCard> SelectedPresets { get; } = new ObservableCollection<EventPresetCard>();
        public IReadOnlyList<int> PhotoCounts { get; }
        public IReadOnlyList<int> Countdowns { get; }
        public IReadOnlyList<EventOption<int>> GifDurations { get; }
        public IReadOnlyList<EventOption<int>> WaitingTimeouts { get; }
        public IReadOnlyList<EventOption<CustomerLayoutMode>> Layouts { get; }
        public IReadOnlyList<EventOption<int>> Rotations { get; }
        public IReadOnlyList<EventOption<EventPrintPolicyMode>> PrintPolicies { get; }

        public PhotoEvent SelectedEvent
        {
            get => selectedEvent;
            set
            {
                if (!Set(ref selectedEvent, value)) return;
                EditName = value?.Name ?? string.Empty;
                OutputDirectory = value?.OutputDirectory ?? string.Empty;
                Raise(nameof(HasSelection)); Raise(nameof(IsActive)); Raise(nameof(IsSelectedForThisDevice)); Raise(nameof(IsSystemDefault)); Raise(nameof(SelectedEventName));
                Raise(nameof(ActionStatusText)); Raise(nameof(PrimaryActionText));Raise(nameof(LifecycleStatusText));Raise(nameof(LifecycleActionText));Raise(nameof(CanAdvanceLifecycle));Raise(nameof(CanEditConfiguration));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                _ = LoadConfiguration(value, ++loadVersion);
            }
        }
        public string CreateName { get => createName; set => Set(ref createName, value); }
        public string EditName { get => editName; set { if (Set(ref editName, value)) MarkDirty(); } }
        public string OutputDirectory { get => outputDirectory; set { if (Set(ref outputDirectory, value)) MarkDirty(); } }
        public string Message { get => message; private set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public bool HasSelection => SelectedEvent != null;
        public bool IsActive => SelectedEvent?.IsSelectedForThisDevice == true;
        public bool IsSelectedForThisDevice => IsActive;
        public bool IsSystemDefault => SelectedEvent?.IsSystemDefault == true;
        public bool CanEditConfiguration => SelectedEvent != null && SelectedEvent.LifecycleState != PhotoEventLifecycleStates.Closed && SelectedEvent.LifecycleState != PhotoEventLifecycleStates.Delivered && SelectedEvent.LifecycleState != PhotoEventLifecycleStates.Archived;
        public string SelectedEventName => SelectedEvent?.Name ?? "Chưa chọn sự kiện";
        public string LifecycleStatusText => SelectedEvent?.LifecycleDisplayName ?? "Chưa xác định";
        public string ActionStatusText => Dirty ? "Có thay đổi chưa lưu" : IsActive ? "Đang dùng trên máy này" : "Cấu hình đã lưu";
        public string PrimaryActionText => IsActive ? (Dirty ? "Lưu & cập nhật máy này" : "Đang dùng trên máy này") : (Dirty ? "Lưu & dùng trên máy này" : "Dùng trên máy này");
        public string LifecycleActionText
        {
            get
            {
                if(SelectedEvent?.IsSystemDefault==true)return "Luôn mở";
                if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Draft||SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Scheduled)return "Mở nhận lượt chụp";
                if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Open)return "Đóng nhận lượt chụp";
                if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Closed)return "Xác nhận đã bàn giao";
                if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Delivered)return "Lưu trữ sự kiện";
                return "Không còn thao tác";
            }
        }
        public bool CanAdvanceLifecycle => SelectedEvent!=null&&!SelectedEvent.IsSystemDefault&&!Dirty&&NextLifecycleState()!=null;
        public bool Dirty
        {
            get => dirty;
            private set
            {
                if (!Set(ref dirty, value)) return;
                Raise(nameof(ActionStatusText));
                Raise(nameof(PrimaryActionText));
                Raise(nameof(CanAdvanceLifecycle));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
        public int PhotoCount { get => photoCount; set { if (Set(ref photoCount, value)) MarkDirty(); } }
        public int Countdown { get => countdown; set { if (Set(ref countdown, value)) MarkDirty(); } }
        public long BasePriceMinor { get => basePriceMinor; set { if (Set(ref basePriceMinor, Math.Max(0,value))) MarkDirty(); } }
        public long AdditionalPrintPriceMinor { get => additionalPrintPriceMinor; set { if (Set(ref additionalPrintPriceMinor, Math.Max(0,value))) MarkDirty(); } }
        public EventOption<int> GifDuration { get => gifDuration; set { if (Set(ref gifDuration, value)) MarkDirty(); } }
        public EventOption<int> WaitingTimeout { get => waitingTimeout; set { if (Set(ref waitingTimeout, value)) MarkDirty(); } }
        public EventOption<CustomerLayoutMode> Layout { get => layout; set { if (Set(ref layout, value)) MarkDirty(); } }
        public EventOption<int> Rotation { get => rotation; set { if (Set(ref rotation, value)) MarkDirty(); } }
        public EventOption<EventPrintPolicyMode> PrintPolicy { get => printPolicy; set { if (Set(ref printPolicy, value)) { Raise(nameof(IsPrintingConfigured)); NormalizePrintPolicy(); MarkDirty(); } } }
        public bool IsPrintingConfigured => (PrintPolicy?.Value ?? EventPrintPolicyMode.Manual) != EventPrintPolicyMode.Disabled;
        public int IncludedPrintCopies { get => includedPrintCopies; set { var normalized=Math.Max(0,Math.Min(10,value)); if(Set(ref includedPrintCopies,normalized)){if(MaximumPrintCopies<normalized)MaximumPrintCopies=normalized;MarkDirty();} } }
        public int MaximumPrintCopies { get => maximumPrintCopies; set { var normalized=Math.Max(IncludedPrintCopies,Math.Min(20,value)); if(Set(ref maximumPrintCopies,normalized))MarkDirty(); } }
        public int ColorPresetStrengthPercent { get => colorPresetStrengthPercent; set { if(Set(ref colorPresetStrengthPercent,Clamp(value))){Raise(nameof(ColorPresetStrengthText));MarkDirty();_ = RenderPresetPreviews(loadVersion);} } }
        public string ColorPresetStrengthText => ColorPresetStrengthPercent + "%";
        public bool BeautyEnabled { get => beautyEnabled; set { if (Set(ref beautyEnabled, value)) { Raise(nameof(IsBeautyEditorEnabled)); MarkDirty(); } } }
        public bool IsBeautyEditorEnabled => BeautyEnabled && HasSelection;
        public int SmoothSkin { get => smooth; set { if (Set(ref smooth, Clamp(value))) MarkDirty(); } }
        public int BrightenSkin { get => brighten; set { if (Set(ref brighten, Clamp(value))) MarkDirty(); } }
        public int SkinTone { get => tone; set { if (Set(ref tone, Clamp(value))) MarkDirty(); } }
        public int Sharpen { get => sharpen; set { if (Set(ref sharpen, Clamp(value))) MarkDirty(); } }
        public int EyeSize { get => eye; set { if (Set(ref eye, Clamp(value))) MarkDirty(); } }
        public int SlimFace { get => slim; set { if (Set(ref slim, Clamp(value))) MarkDirty(); } }
        public int BlemishRemoval { get => blemish; set { if (Set(ref blemish, Clamp(value))) MarkDirty(); } }
        public int DarkCircleReduction { get => darkCircle; set { if (Set(ref darkCircle, Clamp(value))) MarkDirty(); } }
        public string EventSummary => Events.Count == 0 ? "Chưa có sự kiện" : Events.Count + " sự kiện";
        public string FrameSummary => SelectedFrames.Count + " khung";
        public string PresetSummary => SelectedPresets.Count + " bộ lọc";

        public ICommand CreateCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ActivateCommand { get; }
        public ICommand AdvanceLifecycleCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ChooseOutputDirectoryCommand { get; }
        public ICommand OpenFramePickerCommand { get; }
        public ICommand RemoveFrameCommand { get; }
        public ICommand OpenPresetPickerCommand { get; }
        public ICommand RemovePresetCommand { get; }
        public ICommand ReloadCommand { get; }

        public async Task RefreshAsync(Guid? selectId = null)
        {
            try
            {
                var id = selectId ?? SelectedEvent?.Id;
                Events.Clear();
                foreach (var item in (await service.GetAllAsync(CancellationToken.None)).OrderBy(x=>x.IsArchived).ThenByDescending(x => x.IsSelectedForThisDevice).ThenByDescending(x => x.UpdatedAtUtc)) Events.Add(item);
                Raise(nameof(EventSummary));
                SelectedEvent = id.HasValue ? Events.FirstOrDefault(x => x.Id == id.Value) ?? Events.FirstOrDefault() : Events.FirstOrDefault();
            }
            catch (Exception error) { Fail(error, "Không thể tải danh sách sự kiện"); }
        }

        public async Task RefreshAfterRemoteImportAsync(string eventName)
        {
            if (Dirty)
            {
                Message = "Đã nhận sự kiện mới từ web. Thay đổi đang chỉnh sửa được giữ nguyên; hãy lưu hoặc tải lại để xem bản mới.";
                return;
            }
            await RefreshAsync();
            Message = string.IsNullOrWhiteSpace(eventName)
                ? "Đã cập nhật thư viện sự kiện từ web."
                : "Đã lưu “" + eventName + "” vào thư viện. Sự kiện đang vận hành không bị thay đổi.";
        }

        async Task LoadConfiguration(PhotoEvent target, int version)
        {
            if (target == null) { SelectedFrames.Clear(); SelectedPresets.Clear(); Raise(nameof(FrameSummary)); Raise(nameof(PresetSummary)); Dirty = false; return; }
            try
            {
                var configuration = await service.GetConfigurationAsync(target.Id, CancellationToken.None);
                var allFrames = await frames.GetAllAsync(CancellationToken.None);
                var allPresets = await presets.GetAllAsync(CancellationToken.None);
                if (version != loadVersion || SelectedEvent?.Id != target.Id) return;
                loading = true;
                rowVersion = configuration.RowVersion;
                PhotoCount = configuration.PhotoCount;
                Countdown = configuration.CountdownSeconds;
                BasePriceMinor = configuration.BasePriceMinor;
                AdditionalPrintPriceMinor = configuration.AdditionalPrintPriceMinor;
                GifDuration = GifDurations.FirstOrDefault(x => x.Value == configuration.GifFrameDurationMilliseconds) ?? GifDurations.Last();
                WaitingTimeout = WaitingTimeouts.FirstOrDefault(x => x.Value == configuration.WaitingTimeoutSeconds) ?? WaitingTimeouts.First();
                Layout = Layouts.FirstOrDefault(x => x.Value == configuration.CustomerLayoutMode) ?? Layouts.First();
                Rotation = Rotations.FirstOrDefault(x => x.Value == configuration.ImageRotationDegrees) ?? Rotations.First();
                PrintPolicy = PrintPolicies.FirstOrDefault(x => x.Value == configuration.PrintPolicyMode) ?? PrintPolicies[1];
                IncludedPrintCopies = configuration.IncludedPrintCopies;
                MaximumPrintCopies = configuration.MaximumPrintCopies;
                ColorPresetStrengthPercent = configuration.ColorPresetStrengthPercent;
                var beauty = configuration.Beauty ?? new BeautySettings();
                BeautyEnabled = beauty.Enabled; SmoothSkin = beauty.SmoothSkin; BrightenSkin = beauty.BrightenSkin;
                SkinTone = beauty.SkinTone; Sharpen = beauty.Sharpen; EyeSize = beauty.EyeSize; SlimFace = beauty.SlimFace; BlemishRemoval=beauty.BlemishRemoval; DarkCircleReduction=beauty.DarkCircleReduction;
                SelectedFrames.Clear();
                foreach (var id in configuration.FrameIds ?? new Guid[0])
                {
                    var frame = allFrames.FirstOrDefault(x => x.Id == id);
                    if (frame != null) SelectedFrames.Add(frame);
                }
                Raise(nameof(FrameSummary)); Message = null; Dirty = false;
                SelectedPresets.Clear();
                foreach (var id in configuration.PresetIds ?? new Guid[0])
                {
                    var preset = allPresets.FirstOrDefault(x => x.Id == id);
                    if (preset != null) SelectedPresets.Add(new EventPresetCard { Preset = preset, PreviewBytes = modelBytes });
                }
                Raise(nameof(PresetSummary));
                _ = RenderPresetPreviews(version);
            }
            catch (Exception error) { Fail(error, "Không thể tải cấu hình event"); }
            finally { loading = false; Raise(nameof(IsBeautyEditorEnabled)); }
        }

        async Task Create()
        {
            try
            {
                var created = await service.CreateAsync(CreateName, CancellationToken.None);
                CreateName = "Sự kiện mới";
                await RefreshAsync(created.Id);
                Message = "Đã tạo sự kiện. Hãy chọn khung ảnh, bộ lọc màu và lưu thay đổi.";
            }
            catch (Exception error) { Fail(error, "Không thể tạo sự kiện"); }
        }

        async Task Save(bool activate)
        {
            var target = SelectedEvent;
            if (target == null) return;
            try
            {
                var configuration = CurrentConfiguration(target.Id);
                var saved = await service.SaveAsync(EditName, OutputDirectory, configuration, CancellationToken.None);
                rowVersion = saved.RowVersion; Dirty = false;
                if (activate)
                {
                    await service.ActivateAsync(target.Id, CancellationToken.None);
                    await RefreshAsync(target.Id);
                    Message = "Đã lưu và sử dụng sự kiện. Các thay đổi sẽ được áp dụng từ lượt chụp tiếp theo.";
                }
                else
                {
                    await RefreshAsync(target.Id);
                    Message = "Đã lưu các thay đổi của sự kiện.";
                }
            }
            catch (Exception error) { Fail(error, activate ? "Không thể sử dụng sự kiện" : "Không thể lưu sự kiện"); }
        }

        async Task Delete()
        {
            var target = SelectedEvent;
            if (target == null) return;
            try
            {
                await service.DeleteAsync(target.Id, CancellationToken.None);
                await RefreshAsync();
                Message = "Đã xóa sự kiện “" + target.Name + "”.";
            }
            catch (Exception error) { Fail(error, "Không thể xóa sự kiện"); }
        }

        Task ChooseOutputDirectory()
        {
            try
            {
                var selected = fileDialog.PickFolder(OutputDirectory);
                if (!string.IsNullOrWhiteSpace(selected)) OutputDirectory = Path.GetFullPath(selected);
            }
            catch (Exception error) { Fail(error, "Không thể chọn thư mục lưu ảnh"); }
            return Task.CompletedTask;
        }

        async Task AdvanceLifecycle()
        {
            var target=SelectedEvent;var next=NextLifecycleState();
            if(target==null||next==null)return;
            try
            {
                var updated=await service.TransitionAsync(target.Id,next,CancellationToken.None);
                await RefreshAsync(updated.Id);
                Message=next==PhotoEventLifecycleStates.Open?"Sự kiện đã mở và có thể nhận lượt chụp mới.":next==PhotoEventLifecycleStates.Closed?"Sự kiện đã đóng. Các lượt đã có vẫn tiếp tục đồng bộ và bàn giao.":next==PhotoEventLifecycleStates.Delivered?"Đã ghi nhận hoàn tất bàn giao sự kiện.":"Đã lưu trữ sự kiện.";
            }
            catch(Exception error){Fail(error,"Không thể đổi trạng thái sự kiện");}
        }

        string NextLifecycleState()
        {
            if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Draft||SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Scheduled)return PhotoEventLifecycleStates.Open;
            if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Open)return PhotoEventLifecycleStates.Closed;
            if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Closed)return PhotoEventLifecycleStates.Delivered;
            if(SelectedEvent?.LifecycleState==PhotoEventLifecycleStates.Delivered)return PhotoEventLifecycleStates.Archived;
            return null;
        }

        async Task OpenFramePicker()
        {
            await picker.OpenAsync(SelectedFrames.Select(x => x.Id).ToList(), ApplyPickedFrames);
            navigation.Navigate("event-frame-picker");
        }

        async Task OpenPresetPicker()
        {
            await presetPicker.OpenAsync(SelectedPresets.Select(x => x.Id).ToList(), ApplyPickedPresets);
            navigation.Navigate("event-preset-picker");
        }

        void ApplyPickedFrames(IReadOnlyList<Frame> selected)
        {
            SelectedFrames.Clear();
            foreach (var frame in selected) SelectedFrames.Add(frame);
            Raise(nameof(FrameSummary)); MarkDirty();
        }

        void RemoveFrame(object parameter)
        {
            var frame = parameter as Frame;
            if (frame == null) return;
            SelectedFrames.Remove(frame); Raise(nameof(FrameSummary)); MarkDirty();
        }

        void ApplyPickedPresets(IReadOnlyList<Preset> selected)
        {
            SelectedPresets.Clear();
            foreach (var preset in selected) SelectedPresets.Add(new EventPresetCard { Preset = preset, PreviewBytes = modelBytes });
            Raise(nameof(PresetSummary)); MarkDirty();
            _ = RenderPresetPreviews(loadVersion);
        }

        void RemovePreset(object parameter)
        {
            var card = parameter as EventPresetCard;
            if (card == null) return;
            SelectedPresets.Remove(card); Raise(nameof(PresetSummary)); MarkDirty();
        }

        async Task RenderPresetPreviews(int version)
        {
            foreach (var card in SelectedPresets.ToList())
            {
                try
                {
                    var bytes = await colors.RenderPreviewAsync(card.Preset.LutAssetId, modelPath, ColorPresetStrengthPercent/100f, CancellationToken.None);
                    if (version != loadVersion) return;
                    if (!SelectedPresets.Contains(card)) continue;
                    card.PreviewBytes = bytes;
                }
                catch (Exception error) { log.LogWarning(error, "Unable to render event preset preview for {PresetId}", card.Id); }
            }
        }

        PhotoEventConfiguration CurrentConfiguration(Guid eventId) => new PhotoEventConfiguration
        {
            EventId = eventId, RowVersion = rowVersion, PhotoCount = PhotoCount, CountdownSeconds = Countdown,
            BasePriceMinor = BasePriceMinor, AdditionalPrintPriceMinor = AdditionalPrintPriceMinor, Currency = "VND",
            GifFrameDurationMilliseconds = GifDuration?.Value ?? 800, WaitingTimeoutSeconds = WaitingTimeout?.Value ?? 60,
            CustomerLayoutMode = Layout?.Value ?? CustomerLayoutMode.Landscape, ImageRotationDegrees = Rotation?.Value ?? 0,
            PrintPolicyMode = PrintPolicy?.Value ?? EventPrintPolicyMode.Manual,
            IncludedPrintCopies = IsPrintingConfigured ? Math.Max(1,IncludedPrintCopies) : 0,
            MaximumPrintCopies = IsPrintingConfigured ? Math.Max(Math.Max(1,IncludedPrintCopies),MaximumPrintCopies) : 0,
            ColorPresetStrengthPercent = ColorPresetStrengthPercent,
            Beauty = new BeautySettings { Enabled=BeautyEnabled,SmoothSkin=SmoothSkin,BrightenSkin=BrightenSkin,SkinTone=SkinTone,Sharpen=Sharpen,EyeSize=EyeSize,SlimFace=SlimFace,BlemishRemoval=BlemishRemoval,DarkCircleReduction=DarkCircleReduction },
            FrameIds = SelectedFrames.Select(x => x.Id).ToList(),
            PresetIds = SelectedPresets.Select(x => x.Id).ToList()
        };

        void MarkDirty() { if (!loading) { Dirty = true; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
        void NormalizePrintPolicy()
        {
            if(!IsPrintingConfigured){includedPrintCopies=0;maximumPrintCopies=0;Raise(nameof(IncludedPrintCopies));Raise(nameof(MaximumPrintCopies));return;}
            if(includedPrintCopies<1)includedPrintCopies=1;
            if(maximumPrintCopies<includedPrintCopies)maximumPrintCopies=Math.Max(10,includedPrintCopies);
            Raise(nameof(IncludedPrintCopies));Raise(nameof(MaximumPrintCopies));
        }
        void Fail(Exception error, string text) { log.LogError(error, text); Message = text + ": " + error.Message; }
        static int Clamp(int value) => Math.Max(0, Math.Min(100, value));
        static EventOption<T> Option<T>(string label, T value) => new EventOption<T> { Label = label, Value = value };
    }
}
