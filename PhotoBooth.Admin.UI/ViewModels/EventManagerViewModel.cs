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
        readonly INavigationService navigation;
        readonly ILogger<EventManagerViewModel> log;
        PhotoEvent selectedEvent;
        string createName = "Event mới", editName, message;
        int photoCount = 1, countdown = 3, smooth, brighten, tone, sharpen, eye, slim;
        long rowVersion;
        bool beautyEnabled, loading, dirty;
        EventOption<int> gifDuration, waitingTimeout, rotation;
        EventOption<CustomerLayoutMode> layout;
        int loadVersion;
        readonly string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model.png");
        byte[] modelBytes;

        public EventManagerViewModel(IPhotoEventManagementService eventService, IFrameService frameService, IPresetService presetService,
            IColorLutService colorService, EventFramePickerViewModel framePicker, EventPresetPickerViewModel eventPresetPicker,
            INavigationService navigationService, ILogger<EventManagerViewModel> logger)
        {
            service = eventService; frames = frameService; presets = presetService; colors = colorService; picker = framePicker; presetPicker = eventPresetPicker; navigation = navigationService; log = logger;
            try { if (File.Exists(modelPath)) modelBytes = File.ReadAllBytes(modelPath); } catch (Exception error) { log.LogWarning(error, "Unable to load preset model image"); }
            PhotoCounts = Enumerable.Range(1, 8).ToArray();
            Countdowns = Enumerable.Range(1, 10).ToArray();
            GifDurations = new[] { Option("0.4 giây", 400), Option("0.6 giây", 600), Option("0.8 giây", 800), Option("1.0 giây", 1000) };
            WaitingTimeouts = new[] { Option("30 giây", 30), Option("1 phút", 60), Option("2 phút", 120), Option("5 phút", 300), Option("10 phút", 600), Option("15 phút", 900) };
            Layouts = new[] { Option("Màn hình ngang", CustomerLayoutMode.Landscape), Option("Màn hình dọc", CustomerLayoutMode.Portrait) };
            Rotations = new[] { Option("Không xoay", 0), Option("Xoay phải 90°", 90), Option("Xoay 180°", 180), Option("Xoay trái 90°", -90) };
            CreateCommand = new AsyncCommand(_ => Create());
            SaveCommand = new AsyncCommand(_ => Save(false), _ => SelectedEvent != null && Dirty);
            ActivateCommand = new AsyncCommand(_ => Save(true), _ => SelectedEvent != null);
            DeleteCommand = new AsyncCommand(_ => Delete(), _ => SelectedEvent != null && !SelectedEvent.IsDefault);
            OpenFramePickerCommand = new AsyncCommand(_ => OpenFramePicker(), _ => SelectedEvent != null);
            RemoveFrameCommand = new RelayCommand(RemoveFrame);
            OpenPresetPickerCommand = new AsyncCommand(_ => OpenPresetPicker(), _ => SelectedEvent != null);
            RemovePresetCommand = new RelayCommand(RemovePreset);
            ReloadCommand = new AsyncCommand(_ => RefreshAsync());
            _ = RefreshAsync();
        }

        public override string Title => "Events";
        public ObservableCollection<PhotoEvent> Events { get; } = new ObservableCollection<PhotoEvent>();
        public ObservableCollection<Frame> SelectedFrames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<EventPresetCard> SelectedPresets { get; } = new ObservableCollection<EventPresetCard>();
        public IReadOnlyList<int> PhotoCounts { get; }
        public IReadOnlyList<int> Countdowns { get; }
        public IReadOnlyList<EventOption<int>> GifDurations { get; }
        public IReadOnlyList<EventOption<int>> WaitingTimeouts { get; }
        public IReadOnlyList<EventOption<CustomerLayoutMode>> Layouts { get; }
        public IReadOnlyList<EventOption<int>> Rotations { get; }

        public PhotoEvent SelectedEvent
        {
            get => selectedEvent;
            set
            {
                if (!Set(ref selectedEvent, value)) return;
                EditName = value?.Name ?? string.Empty;
                Raise(nameof(HasSelection)); Raise(nameof(IsActive));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                _ = LoadConfiguration(value, ++loadVersion);
            }
        }
        public string CreateName { get => createName; set => Set(ref createName, value); }
        public string EditName { get => editName; set { if (Set(ref editName, value)) MarkDirty(); } }
        public string Message { get => message; private set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public bool HasSelection => SelectedEvent != null;
        public bool IsActive => SelectedEvent?.IsDefault == true;
        public bool Dirty { get => dirty; private set => Set(ref dirty, value); }
        public int PhotoCount { get => photoCount; set { if (Set(ref photoCount, value)) MarkDirty(); } }
        public int Countdown { get => countdown; set { if (Set(ref countdown, value)) MarkDirty(); } }
        public EventOption<int> GifDuration { get => gifDuration; set { if (Set(ref gifDuration, value)) MarkDirty(); } }
        public EventOption<int> WaitingTimeout { get => waitingTimeout; set { if (Set(ref waitingTimeout, value)) MarkDirty(); } }
        public EventOption<CustomerLayoutMode> Layout { get => layout; set { if (Set(ref layout, value)) MarkDirty(); } }
        public EventOption<int> Rotation { get => rotation; set { if (Set(ref rotation, value)) MarkDirty(); } }
        public bool BeautyEnabled { get => beautyEnabled; set { if (Set(ref beautyEnabled, value)) { Raise(nameof(IsBeautyEditorEnabled)); MarkDirty(); } } }
        public bool IsBeautyEditorEnabled => BeautyEnabled && HasSelection;
        public int SmoothSkin { get => smooth; set { if (Set(ref smooth, Clamp(value))) MarkDirty(); } }
        public int BrightenSkin { get => brighten; set { if (Set(ref brighten, Clamp(value))) MarkDirty(); } }
        public int SkinTone { get => tone; set { if (Set(ref tone, Clamp(value))) MarkDirty(); } }
        public int Sharpen { get => sharpen; set { if (Set(ref sharpen, Clamp(value))) MarkDirty(); } }
        public int EyeSize { get => eye; set { if (Set(ref eye, Clamp(value))) MarkDirty(); } }
        public int SlimFace { get => slim; set { if (Set(ref slim, Clamp(value))) MarkDirty(); } }
        public string EventSummary => Events.Count == 0 ? "Chưa có event" : Events.Count + " event";
        public string FrameSummary => SelectedFrames.Count + " / 10 frame";
        public string PresetSummary => SelectedPresets.Count + " preset";

        public ICommand CreateCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ActivateCommand { get; }
        public ICommand DeleteCommand { get; }
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
                foreach (var item in (await service.GetAllAsync(CancellationToken.None)).OrderByDescending(x => x.IsDefault).ThenByDescending(x => x.UpdatedAtUtc)) Events.Add(item);
                Raise(nameof(EventSummary));
                SelectedEvent = id.HasValue ? Events.FirstOrDefault(x => x.Id == id.Value) ?? Events.FirstOrDefault() : Events.FirstOrDefault();
            }
            catch (Exception error) { Fail(error, "Không thể tải Events"); }
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
                GifDuration = GifDurations.FirstOrDefault(x => x.Value == configuration.GifFrameDurationMilliseconds) ?? GifDurations.Last();
                WaitingTimeout = WaitingTimeouts.FirstOrDefault(x => x.Value == configuration.WaitingTimeoutSeconds) ?? WaitingTimeouts.First();
                Layout = Layouts.FirstOrDefault(x => x.Value == configuration.CustomerLayoutMode) ?? Layouts.First();
                Rotation = Rotations.FirstOrDefault(x => x.Value == configuration.ImageRotationDegrees) ?? Rotations.First();
                var beauty = configuration.Beauty ?? new BeautySettings();
                BeautyEnabled = beauty.Enabled; SmoothSkin = beauty.SmoothSkin; BrightenSkin = beauty.BrightenSkin;
                SkinTone = beauty.SkinTone; Sharpen = beauty.Sharpen; EyeSize = beauty.EyeSize; SlimFace = beauty.SlimFace;
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
                CreateName = "Event mới";
                await RefreshAsync(created.Id);
                Message = "Đã tạo event. Hãy chọn frame, preset và lưu cấu hình.";
            }
            catch (Exception error) { Fail(error, "Không thể tạo event"); }
        }

        async Task Save(bool activate)
        {
            var target = SelectedEvent;
            if (target == null) return;
            try
            {
                var configuration = CurrentConfiguration(target.Id);
                var saved = await service.SaveAsync(EditName, configuration, CancellationToken.None);
                rowVersion = saved.RowVersion; Dirty = false;
                if (activate)
                {
                    await service.ActivateAsync(target.Id, CancellationToken.None);
                    await RefreshAsync(target.Id);
                    Message = "Đã lưu và sử dụng event. Frame, preset, setting và Beauty đã được áp dụng.";
                }
                else
                {
                    await RefreshAsync(target.Id);
                    Message = "Đã lưu cấu hình event.";
                }
            }
            catch (Exception error) { Fail(error, activate ? "Không thể sử dụng event" : "Không thể lưu event"); }
        }

        async Task Delete()
        {
            var target = SelectedEvent;
            if (target == null) return;
            try
            {
                await service.DeleteAsync(target.Id, CancellationToken.None);
                await RefreshAsync();
                Message = "Đã xóa event “" + target.Name + "”.";
            }
            catch (Exception error) { Fail(error, "Không thể xóa event"); }
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
            foreach (var frame in selected.Take(10)) SelectedFrames.Add(frame);
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
                    var bytes = await colors.RenderPreviewAsync(card.Preset.LutAssetId, modelPath, ColorLutData.DefaultStrength, CancellationToken.None);
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
            GifFrameDurationMilliseconds = GifDuration?.Value ?? 1000, WaitingTimeoutSeconds = WaitingTimeout?.Value ?? 30,
            CustomerLayoutMode = Layout?.Value ?? CustomerLayoutMode.Landscape, ImageRotationDegrees = Rotation?.Value ?? 0,
            Beauty = new BeautySettings { Enabled=BeautyEnabled,SmoothSkin=SmoothSkin,BrightenSkin=BrightenSkin,SkinTone=SkinTone,Sharpen=Sharpen,EyeSize=EyeSize,SlimFace=SlimFace },
            FrameIds = SelectedFrames.Select(x => x.Id).ToList(),
            PresetIds = SelectedPresets.Select(x => x.Id).ToList()
        };

        void MarkDirty() { if (!loading) { Dirty = true; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
        void Fail(Exception error, string text) { log.LogError(error, text); Message = text + ": " + error.Message; }
        static int Clamp(int value) => Math.Max(0, Math.Min(100, value));
        static EventOption<T> Option<T>(string label, T value) => new EventOption<T> { Label = label, Value = value };
    }
}
