using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Infrastructure.Services
{
    // This channel is intentionally separate from the legacy Velopack repository.
    public sealed class GitHubInstallerUpdateService : IUpdateService
    {
        public const string Repository = "Yukimura434/MyPTB";
        const long MaximumBytes = 512L * 1024 * 1024;
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        readonly HttpClient http;
        readonly Version current;
        readonly string directory;
        Release pending;
        Asset installer;

        public GitHubInstallerUpdateService() : this(Http,
            (Assembly.GetEntryAssembly() ?? typeof(GitHubInstallerUpdateService).Assembly).GetName().Version,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoBooth", "Updates")) { }

        internal GitHubInstallerUpdateService(HttpClient http, Version current, string directory)
        { this.http = http; this.current = current; this.directory = directory; }

        public async Task<bool> IsUpdateAvailableAsync(CancellationToken token)
        {
            pending = null; installer = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using (var request = Request("https://api.github.com/repos/" + Repository + "/releases/latest"))
                using (var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return false;
                    response.EnsureSuccessStatusCode();
                    Release release;
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        release = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(stream);
                    Version version;
                    if (release == null || release.Draft || release.Prerelease || !Version.TryParse((release.Tag ?? "").TrimStart('v'), out version))
                        throw new InvalidDataException("Invalid stable release metadata.");
                    var normalizedCurrent = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
                    if (version <= normalizedCurrent) return false;
                    var name = "MiuPhotoBooth-" + version.ToString(3) + "-Setup.exe";
                    var asset = release.Assets?.SingleOrDefault(x => x.Name == name);
                    var expected = "https://github.com/" + Repository + "/releases/download/" + release.Tag + "/" + name;
                    if (asset == null || asset.Url != expected || asset.Size <= 0 || asset.Size > MaximumBytes ||
                        !System.Text.RegularExpressions.Regex.IsMatch(asset.Digest ?? "", "^sha256:[a-fA-F0-9]{64}$"))
                        throw new InvalidDataException("Release installer or verification data is unavailable.");
                    pending = release; installer = asset;
                    return true;
                }
            }
        }

        public Task<string> GetAvailableVersionAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(pending?.Tag?.TrimStart('v')); }

        public async Task<string> DownloadUpdateAsync(CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                return await DownloadVerifiedAsync(timeout.Token).ConfigureAwait(false);
            }
        }

        async Task<string> DownloadVerifiedAsync(CancellationToken token)
        {
            var asset = installer ?? throw new InvalidOperationException("Check for updates first.");
            Directory.CreateDirectory(directory);
            // Unique directories prevent stale or concurrent downloads being installed.
            var downloadDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(downloadDirectory);
            var destination = Path.Combine(downloadDirectory, asset.Name);
            var partial = destination + ".partial";
            try
            {
                using (var request = Request(asset.Url))
                using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != asset.Size)
                        throw new InvalidDataException("Installer size mismatch.");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920]; long total = 0; int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                        {
                            total += count;
                            if (total > asset.Size) throw new InvalidDataException("Installer exceeds expected size.");
                            await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        }
                        if (total != asset.Size) throw new InvalidDataException("Installer download is incomplete.");
                    }
                }
                token.ThrowIfCancellationRequested();
                using (var sha = SHA256.Create())
                using (var file = File.OpenRead(partial))
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                    if (!string.Equals(actual, asset.Digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Installer verification failed.");
                }
                File.Move(partial, destination);
                return destination;
            }
            catch { if (File.Exists(partial)) File.Delete(partial); throw; }
        }

        static HttpRequestMessage Request(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("MiuPhotoBooth/3.0.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            return request;
        }
        [DataContract] sealed class Release
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public Asset[] Assets { get; set; }
        }
        [DataContract] sealed class Asset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
        }
    }
}
