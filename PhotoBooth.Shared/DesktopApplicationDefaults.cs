using System;
using System.IO;

namespace PhotoBooth.Shared
{
    public static class DesktopApplicationDefaults
    {
        public const string DataVersion = "2.0.0-alpha";
        public const string DatabaseFileName = "photobooth-v3.db";
        public const string PhotoApiBaseUrl = "https://api.miuphotobooth.com";

        public static string ResolveDataDirectory()
        {
            return ResolveDataDirectory(
                Environment.GetEnvironmentVariable("PHOTOBOOTH_DATA_DIRECTORY"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        public static string ResolveDataDirectory(string configured, string localApplicationDataDirectory)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
            if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
                throw new ArgumentException("Local application data directory is required.", nameof(localApplicationDataDirectory));
            return Path.Combine(localApplicationDataDirectory, "PhotoBooth", DataVersion, "Data");
        }

        public static string ResolvePhotoApiBaseUrl()
        {
            return ResolvePhotoApiBaseUrl(Environment.GetEnvironmentVariable("PHOTOBOOTH_API_BASE_URL"));
        }

        public static string ResolvePhotoApiBaseUrl(string configured)
        {
            return string.IsNullOrWhiteSpace(configured) ? PhotoApiBaseUrl : configured.Trim().TrimEnd('/');
        }
    }
}
