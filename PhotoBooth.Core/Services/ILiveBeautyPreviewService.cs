using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Services
{
    public interface ILiveBeautyPreviewService
    {
        // Null means use the original JPEG (disabled, cancelled or unavailable).
        Task<LiveBeautyPixels> ProcessAsync(byte[] jpegData, BeautySettings settings, CancellationToken token);
        void Reset();
    }
}
