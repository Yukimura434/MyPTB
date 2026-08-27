using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Persistence
{
    public interface IBeautySettingsRepository
    {
        Task<BeautySettings> GetAsync(CancellationToken token);
        Task SaveAsync(BeautySettings value, CancellationToken token);
    }
}
