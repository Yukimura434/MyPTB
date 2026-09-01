using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IDeliverableIntegrityService
    {
        Task ValidateAsync(Deliverable deliverable, CancellationToken token);
    }
}
