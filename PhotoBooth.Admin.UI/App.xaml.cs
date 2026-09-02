using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Services;
using PhotoBooth.Admin.UI.ViewModels;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI;
using PhotoBooth.Customer.UI.ViewModels;
using PhotoBooth.Infrastructure;
using PhotoBooth.Infrastructure.Logging;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
#if !TRIAL_BUILD
using Velopack;
#endif

namespace PhotoBooth.Admin.UI
{
    public partial class App : Application
    {
        private ServiceProvider provider;
        private string dataDirectory;
        private int shuttingDown;
        internal IServiceProvider Services => provider;

        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--camera-smoke", StringComparison.Ordinal))
            {
                Environment.ExitCode = RunCameraSmoke();
                return;
            }
            if (args != null && args.Length > 0 &&
                (string.Equals(args[0], "--video-encode", StringComparison.Ordinal) ||
                 string.Equals(args[0], "--video-compose", StringComparison.Ordinal)))
            {
                Environment.ExitCode = VideoService.RunEncoderCommand(args);
                return;
            }
#if !TRIAL_BUILD
            VelopackApp.Build().Run();
#endif

            var instanceDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoBooth",
                "Data");
            using (var instance = SingleInstanceCoordinator.TryAcquire(instanceDataDirectory))
            {
                if (instance == null) return;

                var app = new App();
                instance.Attach(app);
                app.InitializeComponent();
                app.Run();
            }
        }

        private static int RunCameraSmoke()
        {
            var root = Path.Combine(Path.GetTempPath(), "PhotoBooth-CameraSmoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var services = new ServiceCollection(); services.AddLogging();
                var options = new ApplicationOptions { ApplicationName="PhotoBooth.CameraSmoke",DataDirectory=root,DatabasePath=Path.Combine(root,"smoke.db"),UseFakeCamera=true,RestartLiveViewDuringRecovery=false };
                options.Features["Video"] = false; options.Features["VideoNativeEncoder"] = false;
                services.AddPhotoBoothInfrastructure(options);
                using (var smokeProvider = services.BuildServiceProvider())
                {
                    smokeProvider.InitializePhotoBooth();
                    var camera = smokeProvider.GetRequiredService<ICameraService>();
                    camera.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var cameras = camera.ScanAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (cameras == null || cameras.Count == 0) throw new InvalidOperationException("Fake camera was not discovered.");
                    camera.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                Console.WriteLine("Camera smoke passed."); return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
            finally { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

#if TRIAL_BUILD
            if (!TrialPeriodGuard.TryAuthorize(out var trialMessage))
            {
                MessageBox.Show(
                    trialMessage,
                    "MyPTB Trial",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }
#endif

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppDomain.CurrentDomain.ProcessExit += (s, x) => ShutdownCamera();

            dataDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "PhotoBooth",
                "Data");

            DispatcherUnhandledException += (s, x) =>
            {
                try
                {
                    var logs = Path.Combine(dataDirectory, "Logs");

                    Directory.CreateDirectory(logs);

                    File.AppendAllText(
                        Path.Combine(logs, "Error.log"),
                        DateTime.UtcNow.ToString("O")
                        + Environment.NewLine
                        + x.Exception
                        + Environment.NewLine);
                }
                catch
                {
                }

                var startup = MainWindow == null || !MainWindow.IsVisible;
                MessageBox.Show(
                    startup
                        ? "PhotoBooth could not start. Details were written to Data\\Logs\\Error.log."
                        : "PhotoBooth gặp lỗi và sẽ đóng an toàn. Bạn có thể mở lại ứng dụng ngay mà không cần khởi động lại Windows. Chi tiết: Data\\Logs\\Error.log.",
                    startup ? "PhotoBooth startup error" : "PhotoBooth error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                x.Handled = true;
                Shutdown(-1);
                return;
            };

            var services = new ServiceCollection();

            services.AddLogging(x =>
                x.AddConsole()
                    .SetMinimumLevel(LogLevel.Information)
                    .AddProvider(
                        new RotatingFileLoggerProvider(
                            Path.Combine(dataDirectory, "Logs"))));

            var applicationOptions = new ApplicationOptions
                {
                    ApplicationName = "PhotoBooth",
                    DataDirectory = dataDirectory,
                    DatabasePath = Path.Combine(
                        dataDirectory,
                        "photobooth.db"),
                    RestartLiveViewDuringRecovery = false
                };
            // Encoding runs in a child process so native FFmpeg failures cannot
            // corrupt or terminate the main PhotoBooth workflow.
            applicationOptions.Features["VideoNativeEncoder"] =
                !string.Equals(Environment.GetEnvironmentVariable("PHOTOBOOTH_VIDEO_NATIVE"), "0", StringComparison.Ordinal);
            applicationOptions.Features["Video"] =
                !string.Equals(Environment.GetEnvironmentVariable("PHOTOBOOTH_VIDEO"), "0", StringComparison.Ordinal);
            services.AddPhotoBoothInfrastructure(applicationOptions);

            services.AddCustomerMode();

            services.AddSingleton<IFileDialogService, FileDialogService>();
            services.AddSingleton<ICustomerModeController, CustomerModeController>();
#if !TRIAL_BUILD
            services.AddSingleton<VelopackUpdateService>();
            services.AddSingleton<IUpdateService>(x => x.GetRequiredService<VelopackUpdateService>());
#endif

            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<EventFramePickerViewModel>();
            services.AddSingleton<EventPresetPickerViewModel>();
            services.AddSingleton<EventManagerViewModel>();
            services.AddSingleton<FrameManagerViewModel>();
            services.AddSingleton<FrameSlotOrderViewModel>();
            services.AddSingleton<PresetManagerViewModel>();
            services.AddSingleton<BeautyViewModel>();
            services.AddSingleton<PrinterManagerViewModel>();
            services.AddSingleton<DiagnosticsViewModel>();
            services.AddSingleton<LocalShareViewModel>();
            services.AddSingleton<InterfaceViewModel>();
            services.AddSingleton<AboutViewModel>();
            services.AddSingleton<MainViewModel>();

            services.AddTransient<MainWindow>();

            provider = services.BuildServiceProvider();

            provider.InitializePhotoBooth();

            // Không còn AccessWindow/Login/PIN.
            // Khởi động trực tiếp vào Admin.
            var window = provider.GetRequiredService<MainWindow>();

            window.DataContext =
                provider.GetRequiredService<MainViewModel>();

            MainWindow = window;

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            window.Show();

#if !TRIAL_BUILD
            _ = CheckForUpdatesAsync();
#endif
        }

#if !TRIAL_BUILD
        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            try
            {
                var updater = provider.GetRequiredService<VelopackUpdateService>();
                if (!await updater.DownloadUpdateAsync(CancellationToken.None))
                    return;

                ShutdownCamera();
                updater.ApplyUpdateAndRestart();
            }
            catch (Exception exception)
            {
                provider?.GetService<ILogger<App>>()?.LogWarning(
                    exception,
                    "Automatic update check failed; application startup will continue.");
            }
        }
#endif

        protected override void OnSessionEnding(
            SessionEndingCancelEventArgs e)
        {
            ShutdownCamera();
            base.OnSessionEnding(e);
        }

        private void ShutdownCamera()
        {
            if (Interlocked.Exchange(ref shuttingDown, 1) != 0)
                return;

            using (var timeout =
                   new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    provider?
                        .GetService<IRecoveryService>()?
                        .StopAsync(timeout.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                }

                try
                {
                    provider?
                        .GetService<CaptureViewModel>()?
                        .ShutdownAsync(timeout.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                }

                try
                {
                    provider?
                        .GetService<ICameraService>()?
                        .DisconnectAsync(timeout.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            using (var watchdog = new Timer(_ => ForceTerminateHungExit(), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan))
            {
                ShutdownCamera();
                provider?.Dispose();
            }

            base.OnExit(e);
        }

        private void ForceTerminateHungExit()
        {
            try
            {
                var logs = Path.Combine(dataDirectory ?? string.Empty, "Logs");
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, "Error.log"), DateTime.UtcNow.ToString("O") + " PhotoBooth shutdown exceeded 15 seconds; forcing process termination so the next run can recover." + Environment.NewLine);
            }
            catch { }
            try { Process.GetCurrentProcess().Kill(); } catch { }
        }
    }
}
