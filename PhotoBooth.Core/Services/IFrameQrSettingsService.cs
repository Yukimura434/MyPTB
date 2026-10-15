using System; using System.Threading; using System.Threading.Tasks; using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Services {
 public interface IFrameQrSettingsService { Task SaveQrAsync(Guid frameId, FrameQrSettings settings, CancellationToken token); }
 public interface IPrintedQrService { Task StampAsync(string imagePath, FrameQrSettings settings, string galleryUrl, CancellationToken token); }
}
