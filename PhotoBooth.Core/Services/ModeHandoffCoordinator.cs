using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Services
{
    /// <summary>Serializes an in-process UI ownership transfer without closing the camera session.</summary>
    public sealed class ModeHandoffCoordinator
    {
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public async Task<TimeSpan> TransferAsync(
            Func<CancellationToken, Task> deactivate,
            Func<CancellationToken, Task> activate,
            CancellationToken token)
        {
            if (deactivate == null) throw new ArgumentNullException(nameof(deactivate));
            if (activate == null) throw new ArgumentNullException(nameof(activate));
            await gate.WaitAsync(token);
            var watch = Stopwatch.StartNew();
            try
            {
                await deactivate(token);
                await activate(token);
                return watch.Elapsed;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
