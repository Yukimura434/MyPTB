using System;
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
using PhotoBooth.Shared;
using Velopack;

namespace PhotoBooth.Admin.UI
{
    public partial class App : Application
    {
        private ServiceProvider provider;
        private string dataDirectory;
        private int shuttingDown;

        [STAThread]
        private static void Main(string[] args)
        {
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

                x.Handled = true;
            };

            var services = new ServiceCollection();

            services.AddLogging(x =>
                x.AddConsole()
                    .SetMinimumLevel(LogLevel.Information)
                    .AddProvider(
                        new RotatingFileLoggerProvider(
                            Path.Combine(dataDirectory, "Logs"))));

            services.AddPhotoBoothInfrastructure(
                new ApplicationOptions
                {
                    ApplicationName = "PhotoBooth",
                    DataDirectory = dataDirectory,
                    DatabasePath = Path.Combine(
                        dataDirectory,
                        "photobooth.db"),
                    RestartLiveViewDuringRecovery = false
                });

            services.AddCustomerMode();

            services.AddSingleton<IFileDialogService, FileDialogService>();
            services.AddSingleton<ICustomerModeController, CustomerModeController>();
            services.AddSingleton<VelopackUpdateService>();
            services.AddSingleton<IUpdateService>(x => x.GetRequiredService<VelopackUpdateService>());

            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<FrameManagerViewModel>();
            services.AddSingleton<PresetManagerViewModel>();
            services.AddSingleton<PrinterManagerViewModel>();
            services.AddSingleton<DiagnosticsViewModel>();
            services.AddSingleton<LocalShareViewModel>();
            services.AddSingleton<InterfaceViewModel>();
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

            _ = CheckForUpdatesAsync();
        }

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
                        .ShutdownAsync()
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
            ShutdownCamera();

            provider?.Dispose();

            base.OnExit(e);
        }
    }
}
