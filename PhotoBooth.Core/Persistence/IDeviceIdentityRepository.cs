using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IDeviceIdentityRepository
    {
        Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken);

        Task<byte[]> GetProtectedCredentialAsync(CancellationToken cancellationToken);

        Task SaveEnrolledAsync(DeviceIdentity identity, byte[] protectedCredential, CancellationToken cancellationToken);

        Task ClearAsync(CancellationToken cancellationToken);

        // Only call after activation confirms permanent revocation of this installation in this organization.
        Task ResetRevokedInstallationAsync(string installationPublicId, string organizationId, CancellationToken cancellationToken);

        Task<string> GetInstallationPublicIdAsync(CancellationToken cancellationToken);

        Task<IReadOnlyList<DeviceIdentity>> ListProfilesAsync(CancellationToken cancellationToken);

        Task SelectProfileAsync(string profileId, CancellationToken cancellationToken);

        Task SaveProfileAsync(DeviceIdentity identity, byte[] protectedCredential, CancellationToken cancellationToken);
    }
}
