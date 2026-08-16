using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Services;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace PhotoBooth.Admin.UI.Services
{
    public sealed class VelopackUpdateService : IUpdateService
    {
        private const string RepositoryUrl = "https://github.com/Yukimura434/MiuCamezaPTB";

        private readonly ILogger<VelopackUpdateService> logger;
        private readonly UpdateManager updateManager;
        private UpdateInfo pendingUpdate;

        public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
        {
            this.logger = logger;
            updateManager = new UpdateManager(
                new GithubSource(RepositoryUrl, null, false));
        }

        public async Task<bool> IsUpdateAvailableAsync(CancellationToken token)
        {
            pendingUpdate = await CheckForUpdatesAsync(token);
            return pendingUpdate != null;
        }

        public async Task<string> GetAvailableVersionAsync(CancellationToken token)
        {
            pendingUpdate = pendingUpdate ?? await CheckForUpdatesAsync(token);
            return pendingUpdate?.TargetFullRelease?.Version?.ToString();
        }

        public async Task<bool> DownloadUpdateAsync(CancellationToken token)
        {
            pendingUpdate = await CheckForUpdatesAsync(token);
            if (pendingUpdate == null)
                return false;

            logger.LogInformation(
                "Downloading PhotoBooth update {Version}.",
                pendingUpdate.TargetFullRelease.Version);

            await updateManager.DownloadUpdatesAsync(pendingUpdate, null, token);
            return true;
        }

        public void ApplyUpdateAndRestart()
        {
            if (pendingUpdate == null)
                throw new InvalidOperationException("No downloaded update is ready to apply.");

            logger.LogInformation(
                "Applying PhotoBooth update {Version} and restarting.",
                pendingUpdate.TargetFullRelease.Version);
            updateManager.ApplyUpdatesAndRestart(pendingUpdate);
        }

        private async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var update = await updateManager.CheckForUpdatesAsync();
                token.ThrowIfCancellationRequested();
                return update;
            }
            catch (NotInstalledException)
            {
                logger.LogDebug("Skipping update check because PhotoBooth is not a Velopack installation.");
                return null;
            }
        }
    }
}
