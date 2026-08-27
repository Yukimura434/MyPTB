using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IVideoService
    {
        void AddLiveViewFrame(byte[] imageData, DateTime timestampUtc);
        Task CreateAsync(string stillImagePath, string destinationPath, DateTime shutterTimestampUtc, int durationSeconds, bool flipHorizontally, CancellationToken token);
        Task ComposeAsync(string stillCompositePath, Frame frame, IReadOnlyDictionary<int, string> slotAssignments, string destinationPath, CancellationToken token);
        Task<string> CreatePreviewVideoAsync(string videoPath, string previewDirectory, CancellationToken token);
    }
}
