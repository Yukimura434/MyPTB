using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IBoothAccessService
    {
        Task<BoothAccessResult> RedeemAsync(string code, CancellationToken cancellationToken);
        Task<BoothAccessResult> LoginEventAsync(string code, string password, CancellationToken cancellationToken);
        Task<BoothAccessResult> ValidateAsync(BoothAccessAuthorization authorization, CancellationToken cancellationToken);
        Task<CreatedBoothAccessCode> CreatePhotoBoothCodeAsync(CreatePhotoBoothCodeRequest request, CancellationToken cancellationToken);
        Task<CreatedBoothAccessCode> CreateSelfBoothPassAsync(CreateSelfBoothPassRequest request, CancellationToken cancellationToken);
    }
}
