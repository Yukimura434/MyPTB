using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Services
{
    /// <summary>Pulls and validates customer-published configuration into the local event library.</summary>
    public interface IDeviceConfigurationSyncService
    {
        Task<DeviceConfigurationSyncResult> SyncAsync(CancellationToken cancellationToken);
    }

    public sealed class DeviceConfigurationSyncResult
    {
        public bool HasAssignment { get; set; }
        /// <summary>True when local catalog data changed and manager views should refresh.</summary>
        public bool Applied { get; set; }
        public string EventName { get; set; }
        public int? Version { get; set; }
        public string DeploymentState { get; set; }
        public string Message { get; set; }
    }
}
