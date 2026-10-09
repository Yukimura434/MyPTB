using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Services
{
    public static class RemoteStorageProviders
    {
        public const string SystemLibrary = "r2";
        public const string GoogleDrive = "drive";
        public static string Normalize(string value) => value == GoogleDrive || value == "google_drive" ? GoogleDrive : SystemLibrary;
    }

    public sealed class RemoteStorageConfiguration
    {
        public bool Succeeded { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string Provider { get; set; } = RemoteStorageProviders.SystemLibrary;
        public string OrganizationProvider { get; set; } = RemoteStorageProviders.SystemLibrary;
        public string DeviceOverride { get; set; }
        public bool GoogleDriveConnected { get; set; }
        public string GoogleDriveStatus { get; set; }
        public string GoogleEmail { get; set; }
    }

    public interface IRemoteStorageConfigurationService
    {
        Task<RemoteStorageConfiguration> GetAsync(CancellationToken token);
        Task<RemoteStorageConfiguration> SetProviderAsync(string provider, CancellationToken token);
    }
}
