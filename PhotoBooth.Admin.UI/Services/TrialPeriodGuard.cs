using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhotoBooth.Admin.UI.Services
{
    internal static class TrialPeriodGuard
    {
        private static readonly TimeSpan TrialDuration = TimeSpan.FromHours(24);
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyPTB.OneDayTrial.v1");

        public static bool TryAuthorize(out string message)
        {
            try
            {
                var now = DateTime.UtcNow;
                var path = GetStatePath();
                var state = File.Exists(path) ? ReadState(path) : new TrialState(now, now);

                if (now < state.LastRunUtc.AddMinutes(-2))
                {
                    message = "System clock rollback was detected. This trial can no longer be used.";
                    return false;
                }

                if (now - state.FirstRunUtc >= TrialDuration)
                {
                    message = "The 24-hour MyPTB trial period has expired.";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                WriteState(path, new TrialState(state.FirstRunUtc, now));
                message = null;
                return true;
            }
            catch
            {
                message = "The MyPTB trial state is invalid or cannot be verified.";
                return false;
            }
        }

        private static string GetStatePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPTB",
                "Trial",
                "trial.dat");
        }

        private static TrialState ReadState(string path)
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var values = Encoding.UTF8.GetString(plainBytes).Split('|');
            if (values.Length != 2)
                throw new InvalidDataException("Invalid trial state.");

            return new TrialState(
                ParseUtc(values[0]),
                ParseUtc(values[1]));
        }

        private static void WriteState(string path, TrialState state)
        {
            var text = state.FirstRunUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                       + "|"
                       + state.LastRunUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(text),
                Entropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
        }

        private static DateTime ParseUtc(string value)
        {
            return new DateTime(
                long.Parse(value, CultureInfo.InvariantCulture),
                DateTimeKind.Utc);
        }

        private sealed class TrialState
        {
            public TrialState(DateTime firstRunUtc, DateTime lastRunUtc)
            {
                FirstRunUtc = firstRunUtc;
                LastRunUtc = lastRunUtc;
            }

            public DateTime FirstRunUtc { get; }
            public DateTime LastRunUtc { get; }
        }
    }
}
