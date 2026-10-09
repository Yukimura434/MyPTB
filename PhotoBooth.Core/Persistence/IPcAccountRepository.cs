using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IPcAccountRepository
    {
        Task<PcAccountRecord> GetByEmailAsync(string email, CancellationToken cancellationToken);
        Task UpsertAsync(PcAccountRecord account, CancellationToken cancellationToken);
        Task<PcRememberedLogin> GetRememberedLoginAsync(CancellationToken cancellationToken);
        Task SaveRememberedLoginAsync(PcRememberedLogin login, CancellationToken cancellationToken);
        Task ClearRememberedLoginAsync(CancellationToken cancellationToken);
    }
}
