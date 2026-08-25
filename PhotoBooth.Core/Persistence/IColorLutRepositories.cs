using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Persistence
{
    public interface IColorLutAssetRepository
    {
        Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken token);
        Task<ColorLutAsset> GetAsync(Guid id, CancellationToken token);
        Task<ColorLutAsset> GetByHashAsync(string sha256, CancellationToken token);
        Task InsertAsync(ColorLutAsset asset, CancellationToken token);
        Task<bool> UpdateAsync(ColorLutAsset asset, long expectedRowVersion, CancellationToken token);
        Task<int> GetUsageCountAsync(Guid id, CancellationToken token);
        Task<bool> DeleteAsync(Guid id, long expectedRowVersion, CancellationToken token);
    }

    public interface IPresetColorRepository
    {
        Task<PresetColorSettings> GetAsync(Guid presetId, CancellationToken token);
        Task SaveAsync(PresetColorSettings settings, long? expectedRowVersion, CancellationToken token);
        Task RemoveAsync(Guid presetId, CancellationToken token);
    }
}
