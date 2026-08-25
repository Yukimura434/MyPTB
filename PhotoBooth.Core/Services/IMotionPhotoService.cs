using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Services
{
    public interface IMotionPhotoService
    {
        void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc);
        Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, CancellationToken token);
    }
}
