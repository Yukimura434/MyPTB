using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Services
{
    public interface IBeautySettingsService
    {
        Task<BeautySettings> GetAsync(CancellationToken token);
        Task SaveAsync(BeautySettings value, CancellationToken token);
    }
}
