using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace PhotoBooth.Admin.UI.Services
{
    internal sealed class SingleInstanceCoordinator : IDisposable
    {
        private const string MutexName = @"Local\MiuCamezaPTB";
        private const string ActivationEventName = @"Local\MiuCamezaPTB.Activate";
        private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RecoveryWait = TimeSpan.FromSeconds(10);

        private readonly Mutex mutex = new Mutex(false, MutexName);
        private readonly string markerPath;
        private bool ownsMutex;
        private EventWaitHandle activationEvent;
        private RegisteredWaitHandle activationRegistration;

        private SingleInstanceCoordinator(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            markerPath = Path.Combine(dataDirectory, "photobooth.pid");
        }

        public static SingleInstanceCoordinator TryAcquire(string dataDirectory)
        {
            var coordinator = new SingleInstanceCoordinator(dataDirectory);
            if (coordinator.TryOwn(TimeSpan.Zero))
            {
                coordinator.WriteMarker();
                return coordinator;
            }

            coordinator.SignalExistingInstance();
            var owner = coordinator.FindMarkedOwner() ?? coordinator.FindLegacyOwner();
            if (owner == null || !IsStale(owner))
            {
                owner?.Dispose();
                coordinator.Dispose();
                return null;
            }

            try
            {
                coordinator.WriteRecoveryLog("Terminating unresponsive PhotoBooth process " + owner.Id + ".");
                owner.Kill();
                owner.WaitForExit((int)RecoveryWait.TotalMilliseconds);
            }
            catch (Exception error)
            {
                coordinator.WriteRecoveryLog("Unable to terminate stale PhotoBooth process: " + error.Message);
            }
            finally
            {
                owner.Dispose();
            }

            if (!coordinator.TryOwn(RecoveryWait))
            {
                coordinator.Dispose();
                return null;
            }

            coordinator.WriteMarker();
            coordinator.WriteRecoveryLog("Recovered the PhotoBooth instance lock without restarting Windows.");
            return coordinator;
        }

        public void Attach(Application application)
        {
            activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                activationEvent,
                (state, timedOut) =>
                {
                    if (timedOut || application.Dispatcher.HasShutdownStarted) return;
                    application.Dispatcher.BeginInvoke(new Action(() => ActivateVisibleWindow(application)));
                },
                null,
                Timeout.Infinite,
                false);
        }

        private static void ActivateVisibleWindow(Application application)
        {
            var window = application.Windows.Cast<Window>()
                .Where(x => x.IsVisible)
                .OrderByDescending(x => x.IsActive)
                .FirstOrDefault();
            if (window == null) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
            window.Focus();
        }

        private bool TryOwn(TimeSpan timeout)
        {
            try { ownsMutex = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            return ownsMutex;
        }

        private void SignalExistingInstance()
        {
            try { using (var signal = EventWaitHandle.OpenExisting(ActivationEventName)) signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
        }

        private Process FindMarkedOwner()
        {
            try
            {
                if (!File.Exists(markerPath)) return null;
                int pid;
                if (!int.TryParse(File.ReadAllText(markerPath).Trim(), out pid) || pid == Process.GetCurrentProcess().Id) return null;
                var process = Process.GetProcessById(pid);
                return IsSameExecutable(process) ? process : null;
            }
            catch { return null; }
        }

        private Process FindLegacyOwner()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                return Process.GetProcessesByName(current.ProcessName)
                    .Where(x => x.Id != current.Id && IsSameExecutable(x))
                    .OrderBy(SafeStartTime)
                    .FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero || DateTime.UtcNow - SafeStartTime(x).ToUniversalTime() >= StartupGrace);
            }
            catch { return null; }
        }

        private static bool IsStale(Process process)
        {
            try
            {
                if (process.HasExited) return true;
                var age = DateTime.UtcNow - SafeStartTime(process).ToUniversalTime();
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero && !process.Responding)
                {
                    Thread.Sleep(1500);
                    process.Refresh();
                    if (!process.Responding) return true;
                }
                if (age < StartupGrace) return false;
                return !process.Responding || process.MainWindowHandle == IntPtr.Zero;
            }
            catch { return true; }
        }

        private static bool IsSameExecutable(Process process)
        {
            try
            {
                var currentPath = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                var candidatePath = Path.GetFullPath(process.MainModule.FileName);
                return string.Equals(currentPath, candidatePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static DateTime SafeStartTime(Process process)
        {
            try { return process.StartTime; }
            catch { return DateTime.MaxValue; }
        }

        private void WriteMarker()
        {
            try { File.WriteAllText(markerPath, Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch { }
        }

        private void WriteRecoveryLog(string message)
        {
            try
            {
                var logs = Path.Combine(Path.GetDirectoryName(markerPath), "Logs");
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, "Application.log"), DateTime.UtcNow.ToString("O") + " [InstanceRecovery] " + message + Environment.NewLine);
            }
            catch { }
        }

        public void Dispose()
        {
            activationRegistration?.Unregister(null);
            activationEvent?.Dispose();
            if (ownsMutex)
            {
                try
                {
                    int pid;
                    if (File.Exists(markerPath) && int.TryParse(File.ReadAllText(markerPath).Trim(), out pid) && pid == Process.GetCurrentProcess().Id)
                        File.Delete(markerPath);
                }
                catch { }
                try { mutex.ReleaseMutex(); } catch { }
                ownsMutex = false;
            }
            mutex.Dispose();
        }
    }
}
