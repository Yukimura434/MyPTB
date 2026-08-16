using System;
using System.Collections.ObjectModel;
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
    public sealed class InterfaceViewModel : PageViewModel
    {
        readonly IInterfaceAssetService assets; readonly IFileDialogService dialog; readonly ISettingsService settings; readonly ILogger<InterfaceViewModel> log;
        InterfaceAsset selected; string message; bool showLiveView = true; double liveViewX = 10, liveViewY = 10;
        public InterfaceViewModel(IInterfaceAssetService assetService, IFileDialogService files, ISettingsService settingsService, ILogger<InterfaceViewModel> logger)
        {
            assets = assetService; dialog = files; settings = settingsService; log = logger;
            ImportCommand = new AsyncCommand(_ => Import()); SelectCommand = new AsyncCommand(_ => Select(), _ => SelectedAsset != null); RefreshCommand = new AsyncCommand(_ => Load()); SaveLiveViewCommand = new AsyncCommand(_ => SaveLiveView()); ResetLiveViewCommand = new AsyncCommand(_ => ResetLiveView()); _ = Load();
        }
        public override string Title => "Interface";
        public ObservableCollection<InterfaceAsset> Assets { get; } = new ObservableCollection<InterfaceAsset>();
        public InterfaceAsset SelectedAsset { get => selected; set { if (Set(ref selected, value)) CommandManager.InvalidateRequerySuggested(); } }
        public string Message { get => message; private set => Set(ref message, value); }
        public bool ShowLiveView { get => showLiveView; set => Set(ref showLiveView, value); }
        public double LiveViewX { get => liveViewX; set { if (Set(ref liveViewX, Clamp(value, 0, 100))) Raise(nameof(PreviewLiveViewX)); } }
        public double LiveViewY { get => liveViewY; set { if (Set(ref liveViewY, Clamp(value, 0, 68))) Raise(nameof(PreviewLiveViewY)); } }
        public double PreviewLiveViewX => LiveViewX * 16;
        public double PreviewLiveViewY => LiveViewY * 9;
        public ICommand ImportCommand { get; } public ICommand SelectCommand { get; } public ICommand RefreshCommand { get; } public ICommand SaveLiveViewCommand { get; } public ICommand ResetLiveViewCommand { get; }
        async Task Load()
        {
            try { Assets.Clear(); foreach (var asset in await assets.GetAllAsync(CancellationToken.None)) Assets.Add(asset); if (SelectedAsset == null) SelectedAsset = Assets.Count > 0 ? Assets[0] : null; var saved = await settings.GetAsync(CancellationToken.None); ShowLiveView = saved.ShowWaitingLiveView; LiveViewX = saved.WaitingLiveViewX; LiveViewY = saved.WaitingLiveViewY; }
            catch (Exception e) { Fail(e, "Could not load interface backgrounds"); }
        }
        async Task Import() { var path = dialog.PickBackground(); if (path == null) return; try { SelectedAsset = await assets.ImportAsync(path, CancellationToken.None); Message = "Background imported"; await Load(); SelectedAsset = Find(SelectedAsset?.Id); } catch (Exception e) { Fail(e, e.Message); } }
        async Task Select() { try { var id = SelectedAsset.Id; await assets.SelectAsync(id, CancellationToken.None); Message = "Selected background saved to SQLite"; await Load(); SelectedAsset = Find(id); } catch (Exception e) { Fail(e, "Could not select background"); } }
        async Task SaveLiveView() { try { var saved = await settings.GetAsync(CancellationToken.None); saved.ShowWaitingLiveView = ShowLiveView; saved.WaitingLiveViewX = LiveViewX; saved.WaitingLiveViewY = LiveViewY; await settings.SaveAsync(saved, CancellationToken.None); Message = "Live View position saved"; } catch (Exception e) { Fail(e, "Could not save Live View position"); } }
        async Task ResetLiveView() { ShowLiveView = true; LiveViewX = 10; LiveViewY = 10; await SaveLiveView(); }
        InterfaceAsset Find(Guid? id) { if (!id.HasValue) return null; foreach (var asset in Assets) if (asset.Id == id.Value) return asset; return null; }
        static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
        void Fail(Exception e, string text) { log.LogError(e, text); Message = text; }
    }
}
