using System.Threading; using System.Threading.Tasks; using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Persistence { public interface ISettingsRepository { Task<Settings> GetAsync(CancellationToken token); Task SaveAsync(Settings settings, CancellationToken token); } }
