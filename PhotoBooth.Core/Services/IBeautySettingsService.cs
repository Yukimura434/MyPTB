using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Services
{
    public sealed class BeautySettingsChangedEventArgs : EventArgs
    {
        public BeautySettingsChangedEventArgs(BeautySettings settings) { Settings = settings?.Clone() ?? new BeautySettings(); }
        public BeautySettings Settings { get; }
    }

    public interface IBeautySettingsService
    {
        event EventHandler<BeautySettingsChangedEventArgs> SettingsChanged;
        Task<BeautySettings> GetAsync(CancellationToken token);
        Task SaveAsync(BeautySettings value, CancellationToken token);
    }
}
