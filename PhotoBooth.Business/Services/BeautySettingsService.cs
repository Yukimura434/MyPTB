using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
namespace PhotoBooth.Business.Services
{
    public sealed class BeautySettingsService : IBeautySettingsService
    {
        readonly IBeautySettingsRepository repository;
        public BeautySettingsService(IBeautySettingsRepository value) { repository = value; }
        public event EventHandler<BeautySettingsChangedEventArgs> SettingsChanged;
        public async Task<BeautySettings> GetAsync(CancellationToken token) => Normalize(await repository.GetAsync(token).ConfigureAwait(false));
        public async Task SaveAsync(BeautySettings value, CancellationToken token)
        {
            var normalized = Normalize(value);
            await repository.SaveAsync(normalized, token).ConfigureAwait(false);
            SettingsChanged?.Invoke(this, new BeautySettingsChangedEventArgs(normalized));
        }
        static BeautySettings Normalize(BeautySettings value)
        {
            value = value?.Clone() ?? new BeautySettings();
            value.SmoothSkin = Clamp(value.SmoothSkin); value.BrightenSkin = Clamp(value.BrightenSkin);
            value.SkinTone = Clamp(value.SkinTone); value.Sharpen = Clamp(value.Sharpen);
            value.EyeSize = Clamp(value.EyeSize); value.SlimFace = Clamp(value.SlimFace);
            return value;
        }
        static int Clamp(int value) => Math.Max(0, Math.Min(100, value));
    }
}
