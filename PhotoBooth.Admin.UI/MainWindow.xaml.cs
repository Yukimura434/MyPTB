using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoBooth.Admin.UI.ViewModels;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI
{
    public partial class MainWindow : Window
    {
        readonly DispatcherTimer idleTimer;
        readonly TimeSpan idleTimeout;
        readonly IPcSessionContext sessionContext;
        DateTime lastActivityUtc;

        public MainWindow(IPcSessionContext sessionContext)
        {
            this.sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
            InitializeComponent();
            idleTimeout = TimeSpan.FromMinutes(EnvironmentInteger("PHOTOBOOTH_ADMIN_IDLE_MINUTES", 60, 1, 240));
            lastActivityUtc = DateTime.UtcNow;
            idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            idleTimer.Tick += IdleTimer_Tick;
            Loaded += (_, __) => { lastActivityUtc = DateTime.UtcNow; idleTimer.Start(); };
            Closed += (_, __) => idleTimer.Stop();
            PreviewKeyDown += RecordActivity;
            PreviewMouseDown += RecordActivity;
            PreviewMouseMove += RecordActivity;
        }

        void RecordActivity(object sender, InputEventArgs e) => lastActivityUtc = DateTime.UtcNow;

        void IdleTimer_Tick(object sender, EventArgs e)
        {
            if (!IsVisible || !IsActive) return;
            if (sessionContext.Current?.IsExpired != true) return;
            lastActivityUtc = DateTime.UtcNow;
            (DataContext as MainViewModel)?.RequestLock();
        }

        static int EnvironmentInteger(string name, int fallback, int minimum, int maximum)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
                ? Math.Max(minimum, Math.Min(maximum, parsed))
                : fallback;
        }
    }
}
