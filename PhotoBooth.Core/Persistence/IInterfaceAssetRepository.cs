using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IInterfaceAssetRepository
    {
        Task<IReadOnlyList<InterfaceAsset>> GetAllAsync(CancellationToken token);
        Task<InterfaceAsset> GetSelectedAsync(CancellationToken token);
        Task AddAsync(InterfaceAsset asset, CancellationToken token);
        Task SelectAsync(Guid id, CancellationToken token);
    }
}
