using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IDeviceIdentityService
    {
        Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken);

        Task<DeviceIdentity> EnrollAsync(string code, string deviceName, string fingerprint, string appVersion, CancellationToken cancellationToken);

        Task ClearAsync(CancellationToken cancellationToken);

        Task<IReadOnlyList<DeviceIdentity>> ListProfilesAsync(CancellationToken cancellationToken);

        Task SelectProfileAsync(string profileId, CancellationToken cancellationToken);

        Task<DeviceActivationAttempt> StartActivationAsync(string code, string fingerprint, string appVersion, CancellationToken cancellationToken);

        Task<DeviceIdentity> CompleteActivationAsync(DeviceActivationAttempt attempt, string loginIdentifier, string password, string deviceName, string appVersion, CancellationToken cancellationToken);

        Task<DeviceIdentity> ActivateV3Async(string email, string password, string deviceName, string locationId, string appVersion, CancellationToken cancellationToken);

        Task<DeviceActivationAttempt> BeginOwnerActivationAsync(string email, string password, string appVersion, CancellationToken cancellationToken);

        Task<DeviceIdentity> CompleteOwnerActivationAsync(DeviceActivationAttempt attempt, string locationId, string deviceName, string appVersion, CancellationToken cancellationToken);
    }
}
