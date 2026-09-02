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
    public sealed class EventPresetPickerItem : ObservableObject
    {
        bool selected;
        byte[] previewBytes;
        public Preset Preset { get; set; }
        public string Name => Preset.Name;
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
        public byte[] PreviewBytes { get => previewBytes; set => Set(ref previewBytes, value); }
    }

    public sealed class EventPresetPickerViewModel : PageViewModel
    {
        readonly IPresetService presets;
        readonly IColorLutService colors;
        readonly INavigationService navigation;
        readonly ILogger<EventPresetPickerViewModel> log;
        readonly string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model.png");
        byte[] modelBytes;
        string search = string.Empty;
        string message;
        Action<IReadOnlyList<Preset>> apply;
        CancellationTokenSource rendering;

        public EventPresetPickerViewModel(IPresetService presetService, IColorLutService colorService,
            INavigationService navigationService, ILogger<EventPresetPickerViewModel> logger)
        {
            presets = presetService; colors = colorService; navigation = navigationService; log = logger;
            try { if (File.Exists(modelPath)) modelBytes = File.ReadAllBytes(modelPath); } catch (Exception error) { log.LogWarning(error, "Unable to load preset model image"); }
            ToggleCommand = new RelayCommand(Toggle);
            ApplyCommand = new RelayCommand(_ => Apply());
            CancelCommand = new RelayCommand(_ => navigation.Navigate("events"));
        }

        public override string Title => "Chọn preset cho event";
        public ObservableCollection<EventPresetPickerItem> Items { get; } = new ObservableCollection<EventPresetPickerItem>();
        public ObservableCollection<EventPresetPickerItem> VisibleItems { get; } = new ObservableCollection<EventPresetPickerItem>();
        public string Search { get => search; set { if (Set(ref search, value)) Filter(); } }
        public string Message { get => message; private set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public string SelectionSummary => Items.Count(x => x.IsSelected) + " preset đã chọn";
        public ICommand ToggleCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand CancelCommand { get; }

        public async Task OpenAsync(IReadOnlyCollection<Guid> selectedIds, Action<IReadOnlyList<Preset>> applySelection)
        {
            apply = applySelection;
            rendering?.Cancel(); rendering?.Dispose(); rendering = new CancellationTokenSource();
            Items.Clear();
            var selected = new HashSet<Guid>(selectedIds ?? new Guid[0]);
            foreach (var preset in (await presets.GetAllAsync(CancellationToken.None)).OrderByDescending(x => x.IsPinned).ThenBy(x => x.Name))
                Items.Add(new EventPresetPickerItem { Preset = preset, IsSelected = selected.Contains(preset.Id), PreviewBytes = modelBytes });
            Search = string.Empty; Message = null; Filter(); Raise(nameof(SelectionSummary));
            _ = RenderPreviews(rendering.Token);
        }

        void Toggle(object parameter)
        {
            var item = parameter as EventPresetPickerItem;
            if (item == null) return;
            item.IsSelected = !item.IsSelected;
            Raise(nameof(SelectionSummary));
        }

        void Apply()
        {
            apply?.Invoke(Items.Where(x => x.IsSelected).Select(x => x.Preset).ToList());
            navigation.Navigate("events");
        }

        void Filter()
        {
            var query = Items.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(x => (x.Name ?? string.Empty).IndexOf(Search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            VisibleItems.Clear(); foreach (var item in query) VisibleItems.Add(item);
        }

        async Task RenderPreviews(CancellationToken token)
        {
            foreach (var item in Items.ToList())
            {
                try
                {
                    var bytes = await colors.RenderPreviewAsync(item.Preset.LutAssetId, modelPath, ColorLutData.DefaultStrength, token);
                    if (token.IsCancellationRequested || !Items.Contains(item)) return;
                    item.PreviewBytes = bytes;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception error) { log.LogWarning(error, "Unable to render preset picker preview for {PresetId}", item.Preset.Id); }
            }
        }
    }
}
