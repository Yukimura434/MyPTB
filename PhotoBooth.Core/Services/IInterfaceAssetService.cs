using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IInterfaceAssetService
    {
        Task<IReadOnlyList<InterfaceAsset>> GetAllAsync(CancellationToken token);
        Task<InterfaceAsset> GetSelectedAsync(CancellationToken token);
        Task<InterfaceAsset> ImportAsync(string sourcePath, CancellationToken token);
        Task SelectAsync(Guid id, CancellationToken token);
    }
}
