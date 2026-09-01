using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IPhotoEventConfigurationRepository
    {
        Task<PhotoEventConfiguration> GetAsync(Guid eventId, CancellationToken token);
        Task<PhotoEventConfiguration> SaveAsync(string eventName, PhotoEventConfiguration value, CancellationToken token);
        Task ActivateAsync(Guid eventId, CancellationToken token);
        Task DeleteAsync(Guid eventId, CancellationToken token);
    }
}
