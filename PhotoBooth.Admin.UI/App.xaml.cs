using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Services;
using PhotoBooth.Admin.UI.ViewModels;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Infrastructure;
using PhotoBooth.Infrastructure.Logging;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using PhotoBooth.Customer.UI;

namespace PhotoBooth.Admin.UI
{
    public partial class App : Application
    {
        private ServiceProvider provider;
        private string dataDirectory;
        private int shuttingDown;
        private static bool uiSmoke;
        private CancellationTokenSource configurationSyncCancellation;
        internal IServiceProvider Services => provider;

        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--delivery-smoke", StringComparison.Ordinal))
            {
                Environment.ExitCode = DeliverySmoke.Run();
                return;
            }
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

            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            uiSmoke = Array.Exists(args ?? new string[0], value => value == "--ui-smoke");
            if (uiSmoke)
            {
                var smokeApp = new App(); smokeApp.InitializeComponent(); smokeApp.Run(); return;
            }
            var instanceDataDirectory = DesktopApplicationDefaults.ResolveDataDirectory();
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

        protected override async void OnStartup(StartupEventArgs e)
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

            dataDirectory = uiSmoke ? Path.Combine(Path.GetTempPath(), "MiuPhotoBooth-UiSmoke-" + Guid.NewGuid().ToString("N")) : DesktopApplicationDefaults.ResolveDataDirectory();

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
                    ApplicationName = "MiuPhotoBooth",
                    UseFakeCamera = uiSmoke || Environment.GetEnvironmentVariable("PHOTOBOOTH_USE_FAKE_CAMERA") == "1",
                    DataDirectory = dataDirectory,
                    DatabasePath = Path.Combine(
                        dataDirectory,
                        DesktopApplicationDefaults.DatabaseFileName),
                    PhotoApiBaseUrl = DesktopApplicationDefaults.ResolvePhotoApiBaseUrl(),
                    UploadMaxRetries = EnvironmentInteger("PHOTOBOOTH_UPLOAD_MAX_RETRIES", 5, 1, 10),
                    UploadTimeoutSeconds = EnvironmentInteger("PHOTOBOOTH_UPLOAD_TIMEOUT_SECONDS", 120, 15, 1800),
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


            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<EventFramePickerViewModel>();
            services.AddSingleton<EventPresetPickerViewModel>();
            services.AddSingleton<EventManagerViewModel>();
            services.AddSingleton<FrameManagerViewModel>();
            services.AddSingleton<FrameSlotOrderViewModel>();
            services.AddSingleton<PresetManagerViewModel>();
            services.AddSingleton<BeautyViewModel>();
            services.AddSingleton<PrinterManagerViewModel>();
            services.AddSingleton<BoothAccessViewModel>();
            services.AddSingleton<DiagnosticsViewModel>();
            services.AddSingleton<LocalShareViewModel>();
            services.AddSingleton<ShareSettingsViewModel>();
            services.AddSingleton<AnalyticsViewModel>();
            services.AddSingleton<InterfaceViewModel>();
            services.AddSingleton<AboutViewModel>();
            services.AddSingleton<DeviceEnrollmentViewModel>();
            services.AddSingleton<MainViewModel>();

            services.AddTransient<LoginWindow>();
            services.AddTransient<MainWindow>();

            provider = services.BuildServiceProvider();

            await System.Threading.Tasks.Task.Run(() => provider.InitializePhotoBooth());
            provider.GetRequiredService<IPcAuthenticationService>()
                .EnsureDevelopmentAccountsAsync(CancellationToken.None)
                .GetAwaiter().GetResult();

            if (!uiSmoke && !EnsureDeviceReady())
            {
                Shutdown();
                return;
            }

            // Remove legacy saved login even after an unclean previous exit.
            await provider.GetRequiredService<IPcAuthenticationService>().TryResumeAsync(CancellationToken.None);
            await provider.GetRequiredService<ICustomerModeController>().StartAsync();
            if (uiSmoke)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, __) => { timer.Stop(); Shutdown(MainWindow is PhotoBooth.Customer.UI.MainWindow && MainWindow.IsVisible ? 0 : 1); };
                timer.Start();
                return;
            }

            configurationSyncCancellation = new CancellationTokenSource();
            _ = RunConfigurationSyncAsync(configurationSyncCancellation.Token);


        }

        private bool EnsureDeviceReady()
        {
            var identityService = provider.GetRequiredService<IDeviceIdentityService>();
            var needsReauthentication = false;
            DeviceIdentity identity;
            try { identity = identityService.GetAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                provider.GetService<ILogger<App>>()?.LogWarning(exception, "Device readiness could not be read during startup.");
                identity = null;
            }

            if (identity?.EnrollmentState == DeviceEnrollmentState.Enrolled)
            {
                CommercialAccessDecision access = null;
                try { access = provider.GetRequiredService<ICommercialEntitlementService>().CheckAppAccessAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception exception) { provider.GetService<ILogger<App>>()?.LogWarning(exception, "Device security state could not be checked during startup."); }
                if (access?.BlockReason != CommercialAccessBlockReason.DeviceLocked &&
                    access?.BlockReason != CommercialAccessBlockReason.DeviceNotEnrolled) return true;
                needsReauthentication = access?.BlockReason == CommercialAccessBlockReason.DeviceNotEnrolled;

                if (access?.BlockReason == CommercialAccessBlockReason.DeviceLocked)
                {
                    var unlockViewModel = provider.GetRequiredService<DeviceEnrollmentViewModel>();
                    unlockViewModel.PrepareForUnlock();
                    MessageBox.Show(
                        "Thiết bị đang bị khóa tạm thời. Chủ tài khoản cần xác minh lại trên đúng máy này để mở khóa an toàn.",
                        "Thiết bị đang bị khóa",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    var unlockWindow = new DeviceEnrollmentWindow(unlockViewModel) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                    unlockWindow.ShowDialog();
                    return unlockViewModel.CompletedIdentity?.EnrollmentState == DeviceEnrollmentState.Enrolled;
                }
            }

            var setupViewModel = provider.GetRequiredService<DeviceEnrollmentViewModel>();
            if (needsReauthentication) setupViewModel.PrepareForReauthentication();
            var setup = new DeviceEnrollmentWindow(setupViewModel)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            setup.ShowDialog();

            try { identity = identityService.GetAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch { identity = null; }
            if (identity?.EnrollmentState == DeviceEnrollmentState.Enrolled &&
                (!needsReauthentication || setupViewModel.CompletedIdentity?.EnrollmentState == DeviceEnrollmentState.Enrolled)) return true;

#if DEBUG
            // Development accounts remain available for local UI and hardware-independent testing.
            return true;
#else
            MessageBox.Show(
                "Máy này chưa được kết nối với cửa hàng. Hãy hoàn tất thiết lập thiết bị trước khi đăng nhập.",
                "Chưa thể bắt đầu",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
#endif
        }

        private async System.Threading.Tasks.Task RunConfigurationSyncAsync(CancellationToken token)
        {
            try
            {
                await provider.GetRequiredService<IDeviceConfigurationSyncService>().SyncAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                provider?.GetService<ILogger<App>>()?.LogWarning(exception, "Startup configuration sync failed; the local workflow remains available.");
            }
        }

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

            try { configurationSyncCancellation?.Cancel(); } catch { }
            // Revoke locally first; network logout is best-effort and bounded.
            using (var logoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                try { provider?.GetService<IPcAuthenticationService>()?.ForgetAsync(logoutTimeout.Token).GetAwaiter().GetResult(); }
                catch (Exception exception) { provider?.GetService<ILogger<App>>()?.LogWarning(exception, "Session cleanup during exit failed."); }
            }
            var context = provider?.GetService<PhotoBooth.Customer.UI.Workflow.CustomerWorkflowContext>();
            if (context != null) { context.Authorization = null; context.ManagerSession = null; context.PreviousPcSession = null; }
            provider?.GetService<IPcSessionContext>()?.SignOut();

            using (var timeout =
                   new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    provider?
                        .GetService<IRemoteDeliveryQueue>()?
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
                        .GetService<IEventExportQueue>()?
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

        private static int EnvironmentInteger(string name, int fallback, int minimum, int maximum)
        {
            int parsed;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out parsed)
                ? Math.Max(minimum, Math.Min(maximum, parsed))
                : fallback;
        }

    }
}

