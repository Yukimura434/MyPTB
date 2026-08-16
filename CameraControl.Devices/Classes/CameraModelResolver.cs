using System;
using System.Collections.Generic;
using System.Linq;

namespace CameraControl.Devices.Classes
{
    public enum CameraBrand
    {
        Unknown,
        Canon,
        Nikon,
        Sony
    }

    /// <summary>
    /// Identifies a camera independently from its transport. Canon is routed to
    /// EDSDK, Nikon to the native MTP driver table and Sony to WIA or Wi-Fi.
    /// </summary>
    public static class CameraModelResolver
    {
        public static CameraBrand DetectBrand(string model, string manufacturer = null, string deviceId = null)
        {
            var identity = String.Join(" ", new[] { manufacturer, model, deviceId }
                .Where(x => !String.IsNullOrWhiteSpace(x))).ToUpperInvariant();

            if (identity.Contains("VID_04A9") || identity.Contains("CANON")) return CameraBrand.Canon;
            if (identity.Contains("VID_04B0") || identity.Contains("NIKON")) return CameraBrand.Nikon;
            if (identity.Contains("VID_054C") || identity.Contains("SONY") || identity.Contains("ILCE-")) return CameraBrand.Sony;
            return CameraBrand.Unknown;
        }

        public static string NormalizeModel(string model)
        {
            if (String.IsNullOrWhiteSpace(model)) return String.Empty;
            var value = model.Trim();
            var prefixes = new[] { "NIKON CORPORATION ", "NIKON ", "SONY CORPORATION ", "SONY " };
            foreach (var prefix in prefixes)
            {
                if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                value = value.Substring(prefix.Length).Trim();
                break;
            }
            return String.Join(" ", value.Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static Type ResolveNativeDriver(string model, IDictionary<string, Type> drivers)
        {
            if (drivers == null || drivers.Count == 0) return null;
            var normalized = NormalizeModel(model);
            return drivers.Where(x => String.Equals(NormalizeModel(x.Key), normalized, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value).FirstOrDefault();
        }
    }
}
