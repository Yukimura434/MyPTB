using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface ICommercialEntitlementService
    {
        CommercialEntitlement LastVerified { get; }
        System.DateTime? LastVerifiedAtUtc { get; }
        Task<CommercialEntitlement> GetCurrentAsync(CancellationToken cancellationToken);
        Task<CommercialAccessDecision> CheckAppAccessAsync(CancellationToken cancellationToken);
        Task<CommercialAccessDecision> CheckAppAccessForNewSessionAsync(CancellationToken cancellationToken);
    }
}
