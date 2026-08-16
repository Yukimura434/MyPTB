using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IPrintJobRepository
    {
        Task AddAsync(PrintJobRecord record, CancellationToken token);
    }
}
