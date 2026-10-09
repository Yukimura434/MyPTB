using System; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Persistence { public interface IFrameRepository {
        // Replace the local used set atomically; collection membership is not changed.
        Task ReplacePinnedAsync(System.Collections.Generic.IReadOnlyCollection<System.Guid> ids, CancellationToken token);
 Task<Frame> GetAsync(Guid id, CancellationToken token); Task<IReadOnlyList<Frame>> GetAllAsync(CancellationToken token); Task SaveAsync(Frame frame, CancellationToken token); Task DeleteAsync(Guid id, CancellationToken token); } }
