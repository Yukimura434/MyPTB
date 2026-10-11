using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Core.Pipelines;
using PhotoBooth.Customer.UI.ViewModels;
using PhotoBooth.Customer.UI.Workflow;

// Native net48 regression fixture; no physical print or production data writes.
class DeviceRetryEntry { [STAThread] static void Main(string[] args) => UnifiedUxCheck.RetryMain(args); }
partial class UnifiedUxCheck
{
    static void Field(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    static Task ConfigurePrinter(CustomerShellViewModel shell) => (Task)typeof(CustomerShellViewModel).GetMethod("ConfigurePrinterForRunAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(shell, null);
    public static void RetryMain(string[] args)
    {
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var theme in new[] { "DarkTheme", "Icons" }) app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/MiuPhotoBooth;component/Themes/" + theme + ".xaml", UriKind.Relative)));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Dispatcher.BeginInvoke(new Action(async () => { try { await RunRetry(); Console.WriteLine("ALL DEVICE RETRY CHECKS PASS: " + checks); } catch (Exception e) { Console.WriteLine(e); Environment.ExitCode = 1; } finally { app.Shutdown(); } })); app.Run();
    }
    static async Task RunRetry()
    {
        var camera = new Camera { Fail = true }; var live = new Live();
        var p = await Provider(camera, live, new Auth());
        var capture = p.GetRequiredService<CaptureViewModel>();
        await capture.ActivateAsync();
        Check(camera.Connects == 1 && !capture.CameraConnected, "startup absent camera attempts once");
        camera.Notify(); await capture.ActivateAsync();
        Check(camera.Connects == 1, "reactivation and device events do not reconnect");
        capture.RetryCommand.Execute(null); await Wait(() => capture.RetryCommand.CanExecute(null), "failed explicit retry completed");
        Check(camera.Connects == 2 && capture.HasError, "absent Retry attempts connection and keeps usable error");
        var captureView = new PhotoBooth.Customer.UI.Views.CaptureView { DataContext = capture };
        await Render(captureView, "camera-retry-landscape", 1366, 768);
        await Render(captureView, "camera-retry-portrait", 900, 1600);
        camera.Fail = false; camera.Pending = new TaskCompletionSource<bool>();
        capture.RetryCommand.Execute(null); capture.RetryCommand.Execute(null);
        Check(camera.Connects == 3 && !capture.RetryCommand.CanExecute(null), "rapid Retry clicks coalesce");
        camera.Pending.SetResult(true); camera.Pending = null;
        await Wait(() => capture.RetryCommand.CanExecute(null) && capture.LiveImage != null, "Retry restores live image");
        Check(capture.CameraConnected && !capture.HasError && camera.Requested == "last-camera", "Retry connects only saved camera");
        for (var i = 0; i < 3; i++)
        {
            camera.Connected = false; camera.Notify();
            await Wait(() => !capture.CameraConnected && capture.LiveImage == null, "unplug clears live " + i);
            var attempts = camera.Connects;
            for (var n = 0; n < 20; n++) camera.Notify();
            await Task.Delay(80); Check(camera.Connects == attempts, "no hotplug retry storm " + i);
            capture.RetryCommand.Execute(null);
            await Wait(() => capture.RetryCommand.CanExecute(null) && capture.CameraConnected && capture.LiveImage != null, "explicit replug retry " + i);
        }
        await capture.ShutdownAsync(); capture.RetryCommand.Execute(null);
        Check(!capture.CameraConnected && live.Active == 0 && live.MaxActive == 1, "inactive Retry cannot acquire preview");
        var printer = new RetryPrinter(); var shell = p.GetRequiredService<CustomerShellViewModel>();
        Field(shell, "printers", printer);
        var context = p.GetRequiredService<CustomerWorkflowContext>();
        context.Settings = new Settings { PrintingEnabled = true, DefaultPrinterProfileId = printer.Profile.Id };
        context.RequestedPrintCopies = 1;
        printer.Offline = true; await ConfigurePrinter(shell);
        Check(context.PrintingEnabled && context.ConnectedPrinterProfileId == printer.Profile.Id && context.ConnectedPrinterId == printer.Profile.PrinterId && printer.Connects == 1, "offline default retained and connection attempted at run startup");
        printer.Offline = false; await ConfigurePrinter(shell);
        Check(printer.Connects == 2 && context.PrintingEnabled, "online default acquired at run startup");
        context.RequestedPrintCopies = 0; await ConfigurePrinter(shell);
        Check(!context.PrintingEnabled && printer.Connects == 2, "zero copies never acquires printer");
        context.RequestedPrintCopies = 1; context.Settings.PrintingEnabled = false; await ConfigurePrinter(shell);
        Check(!context.PrintingEnabled && printer.Connects == 2, "disabled printing remains disabled");

        var savedSettings = await p.GetRequiredService<ISettingsService>().GetAsync(none);
        savedSettings.PrintingEnabled = true; savedSettings.DefaultPrinterProfileId = printer.Profile.Id;
        await p.GetRequiredService<ISettingsService>().SaveAsync(savedSettings, none);
        var manager = new PhotoBooth.Admin.UI.ViewModels.PrinterManagerViewModel(printer, p.GetRequiredService<ISettingsService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PhotoBooth.Admin.UI.ViewModels.PrinterManagerViewModel>.Instance);
        await Wait(() => manager.IsSelectionConnected, "Manager restores saved default printer on initialization");
        Check(manager.EditingProfile.Id == printer.Profile.Id && manager.IsSavedDefault, "Manager retains exact saved profile");
        var managerConnects = printer.Connects;
        savedSettings.DefaultPrinterProfileId = Guid.NewGuid();
        await p.GetRequiredService<ISettingsService>().SaveAsync(savedSettings, none);
        var missingManager = new PhotoBooth.Admin.UI.ViewModels.PrinterManagerViewModel(printer, p.GetRequiredService<ISettingsService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PhotoBooth.Admin.UI.ViewModels.PrinterManagerViewModel>.Instance);
        await Wait(() => !missingManager.IsBusy, "missing default initialization finishes");
        Check(!missingManager.IsSelectionConnected && printer.Connects == managerConnects, "Manager never substitutes another printer for a missing saved default");
        savedSettings.DefaultPrinterProfileId = printer.Profile.Id; savedSettings.PrintingEnabled = false;
        await p.GetRequiredService<ISettingsService>().SaveAsync(savedSettings, none);
        var disabledManager = new PhotoBooth.Admin.UI.ViewModels.PrinterManagerViewModel(printer, p.GetRequiredService<ISettingsService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PhotoBooth.Admin.UI.ViewModels.PrinterManagerViewModel>.Instance);
        await Wait(() => !disabledManager.IsBusy, "disabled Manager initialization finishes");
        Check(!disabledManager.IsSelectionConnected && printer.Connects == managerConnects, "Manager respects disabled printing");

        // Separate provider avoids running shell navigation/delivery from this fixture.
        var p2 = await Provider(new Camera(), new Live(), new Auth());
        var vm = p2.GetRequiredService<FrameSelectionViewModel>();
        var c = p2.GetRequiredService<CustomerWorkflowContext>(); var m = p2.GetRequiredService<CustomerWorkflowStateMachine>();
        var sink = new RetryPrintSink(); Field(vm, "printers", printer); Field(vm, "printPipeline", sink); Field(vm, "sessions", new RetrySessions());
        SetupRetryFrame(vm, c, m, printer.Profile);
        printer.Offline = true; vm.PrintCommand.Execute(null); await Wait(() => !vm.IsPrinting, "offline finish failed");
        Check(vm.HasError && sink.Calls == 0, "unavailable printer cannot submit print");
        var view = new PhotoBooth.Customer.UI.Views.FrameSelectionView { DataContext = vm };
        await Render(view, "printer-retry-landscape", 1366, 768); await Render(view, "printer-retry-portrait", 900, 1600);
        var before = printer.Connects; vm.RetryCommand.Execute(null); await Wait(() => vm.RetryCommand.CanExecute(null), "offline retry completes");
        Check(printer.Connects == before + 1 && vm.HasError && sink.Calls == 0, "print Retry reconnects and preserves offline error");
        printer.Offline = false; printer.Pending = new TaskCompletionSource<bool>();
        vm.RetryCommand.Execute(null); vm.RetryCommand.Execute(null); vm.PrintCommand.Execute(null);
        Check(vm.IsPrinting && sink.Calls == 0, "recovery serializes retry and normal print");
        printer.Pending.SetResult(true); printer.Pending = null;
        await Wait(() => !vm.IsPrinting, "recovered print finishes");
        Check(m.State == CustomerWorkflowState.Complete && !vm.HasError && sink.Calls == 1 && sink.Copies == 1 && c.DeliverableId == "retry-existing-deliverable", "Retry resumes exact existing deliverable and copies");
        SetupRetryFrame(vm, c, m, printer.Profile); c.ConnectedPrinterId = null; c.ConnectedPrinterProfileId = null;
        vm.PrintCommand.Execute(null); await Wait(() => !vm.IsPrinting, "missing startup profile recovery finishes");
        Check(sink.Calls == 2 && c.ConnectedPrinterProfileId == printer.Profile.Id, "transient startup profile failure resolves frozen default");
        SetupRetryFrame(vm, c, m, printer.Profile); c.ConnectedPrinterProfileId = Guid.NewGuid();
        vm.PrintCommand.Execute(null); await Wait(() => !vm.IsPrinting, "deleted profile failure");
        Check(vm.HasError && sink.Calls == 2, "deleted selected profile never switches printer");
        SetupRetryFrame(vm, c, m, printer.Profile); sink.UnknownOutcome = true;
        vm.PrintCommand.Execute(null); await Wait(() => !vm.IsPrinting, "unknown print failure");
        vm.RetryCommand.Execute(null); await Wait(() => vm.RetryCommand.CanExecute(null), "unknown retry preserves guard");
        Check(vm.HasError && m.State == CustomerWorkflowState.FrameSelection && sink.Submissions == 2, "pipeline unknown outcome remains blocked on Retry");
        await vm.ShutdownAsync();
    }
    static void SetupRetryFrame(FrameSelectionViewModel vm, CustomerWorkflowContext c, CustomerWorkflowStateMachine m, PrinterProfile profile)
    {
        c.Settings = new Settings { PrintingEnabled = true, DefaultPrinterProfileId = profile.Id };
        c.PrintingEnabled = true; c.RequestedPrintCopies = 1; c.MaximumPrintCopies = 1;
        c.WorkingDirectory = Path.Combine(output, "retry-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(c.WorkingDirectory);
        c.BoothSession = new BoothSession { Id = Guid.NewGuid(), Kind = SessionKinds.Booth, Status = BoothSessionStates.Finalizing, OutputDirectory = c.WorkingDirectory, StartedAtUtc = DateTime.UtcNow };
        c.CurrentShots = new List<CapturedShot> { new CapturedShot { Id = "fixture-shot", Sequence = 1, PicturePath = Path.GetFullPath("Scripts/Validation/Fixtures/ApprovedUx/photo-0.png") } };
        c.DeliverableId = "retry-existing-deliverable"; c.ConnectedPrinterId = profile.PrinterId; c.ConnectedPrinterProfileId = profile.Id;
        typeof(CustomerWorkflowStateMachine).GetProperty("State").SetValue(m, CustomerWorkflowState.FrameSelection);
        Field(vm, "loadedForCurrentEntry", true);
        vm.SelectedFrame = new PhotoBooth.Core.Models.Frame { Id = Guid.NewGuid(), Name = "Ảnh kiểm tra", SourcePath = Path.GetFullPath("Scripts/Validation/Fixtures/ApprovedUx/frame-0.png"), PixelWidth = 600, PixelHeight = 900, Slots = new[] { new FrameSlot { Index = 0, X = 30, Y = 30, Width = 259, Height = 366 } } };
        vm.FrameSlots[0].Photo = new CapturedPhotoChoice(Path.GetFullPath("Scripts/Validation/Fixtures/ApprovedUx/photo-0.png"), 1);
        typeof(FrameSelectionViewModel).GetProperty("PrintingEnabled").SetValue(vm, true); vm.PrintCopies = 1;
    }
    class RetryPrinter : IPrinterService
    {
        public PrinterProfile Profile = new PrinterProfile { Id = Guid.NewGuid(), PrinterId = "fixture", PrinterName = "Máy in kiểm thử", IsDefault = true };
        public bool Offline; public int Connects; public TaskCompletionSource<bool> Pending;
        public Task<IReadOnlyList<string>> GetPrintersAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<string>>(new[] { Profile.PrinterName });
        public Task<IReadOnlyList<DiscoveredPrinter>> ScanAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<DiscoveredPrinter>>(Offline ? new DiscoveredPrinter[0] : new[] { new DiscoveredPrinter { Id = Profile.PrinterId, Name = Profile.PrinterName } });
        public async Task<DiscoveredPrinter> ConnectAsync(string id, CancellationToken t) { Connects++; if (Pending != null) await Pending.Task; if (Offline || id != Profile.PrinterId) throw new IOException("Fixture offline"); return new DiscoveredPrinter { Id = id, Name = Profile.PrinterName }; }
        public Task<bool> IsConnectedAsync(string id, CancellationToken t) => Task.FromResult(!Offline);
        public Task<IReadOnlyList<PrinterProfile>> GetProfilesAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<PrinterProfile>>(new[] { Profile });
        public Task SaveProfileAsync(PrinterProfile p, CancellationToken t) => Task.CompletedTask;
        public Task DeleteProfileAsync(Guid id, CancellationToken t) => Task.CompletedTask;
        public Task PrintAsync(PrintJob j, CancellationToken t) => throw new Exception("No physical printing in regression fixture");
    }
    class RetryPrintSink : IPrintPipeline
    {
        public int Calls, Copies, Submissions; public bool UnknownOutcome;
        public Task ExecuteAsync(Guid s, Guid p, int n, CancellationToken t) { Calls++; if (UnknownOutcome) throw new InvalidOperationException("Previous print outcome requires operator confirmation"); Submissions++; Copies = n; return Task.CompletedTask; }
    }
    class RetrySessions : IBoothSessionService
    {
        public Task<BoothSession> StartAsync(Guid e, Guid? p, CancellationToken t) => throw new NotSupportedException();
        public Task<BoothSession> GetAsync(Guid id, CancellationToken t) => throw new NotSupportedException();
        public Task UpdateAsync(BoothSession s, CancellationToken t) => Task.CompletedTask;
        public Task CompleteAsync(BoothSession s, CancellationToken t) => Task.CompletedTask;
        public Task AbandonAsync(BoothSession s, string r, CancellationToken t) => Task.CompletedTask;
        public Task ReplaceCapturedShotAsync(Guid s, string id, CapturedShot r, CancellationToken t) => Task.CompletedTask;
        public Task ReplaceCapturedShotsAsync(Guid s, IReadOnlyDictionary<string, CapturedShot> r, CancellationToken t) => Task.CompletedTask;
    }
}
