using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Services
{
    public interface ILiveBeautyPreviewService
    {
        Task<byte[]> ProcessAsync(byte[] jpegData, BeautySettings settings, CancellationToken token);
        void Reset();
    }
}
