using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IPhotoEventManagementService
    {
        event EventHandler EventsChanged;
        Task<IReadOnlyList<PhotoEvent>> GetAllAsync(CancellationToken token);
        Task<PhotoEvent> CreateAsync(string name, CancellationToken token);
        Task<PhotoEventConfiguration> GetConfigurationAsync(Guid eventId, CancellationToken token);
        Task<PhotoEventConfiguration> SaveAsync(string eventName, PhotoEventConfiguration configuration, CancellationToken token);
        Task ActivateAsync(Guid eventId, CancellationToken token);
        Task DeleteAsync(Guid eventId, CancellationToken token);
    }
}
