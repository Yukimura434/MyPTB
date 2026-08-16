using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IPrinterService
    {
        Task<IReadOnlyList<string>> GetPrintersAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<DiscoveredPrinter>> ScanAsync(CancellationToken cancellationToken);
        Task<DiscoveredPrinter> ConnectAsync(string printerId, CancellationToken cancellationToken);
        Task<bool> IsConnectedAsync(string printerId, CancellationToken cancellationToken);
        Task<IReadOnlyList<PrinterProfile>> GetProfilesAsync(CancellationToken cancellationToken);
        Task SaveProfileAsync(PrinterProfile profile, CancellationToken cancellationToken);
        Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken);
        Task PrintAsync(PrintJob job, CancellationToken cancellationToken);
    }
}
