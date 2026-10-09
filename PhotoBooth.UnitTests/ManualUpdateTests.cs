using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Infrastructure.Services;
using Xunit;
namespace PhotoBooth.UnitTests
{
    public sealed class ManualUpdateTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "miu-update-test-" + Guid.NewGuid().ToString("N"));
        sealed class Handler : HttpMessageHandler
        {
            public int Calls; public string Json; public byte[] Payload = Encoding.UTF8.GetBytes("synthetic-installer");
            public HttpStatusCode Status = HttpStatusCode.OK;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                return Task.FromResult(new HttpResponseMessage(Status) { Content = request.RequestUri.Host == "api.github.com" ? (HttpContent)new StringContent(Json) : new ByteArrayContent(Payload) });
            }
            public void Release(string version = "3.0.1", string digest = null, string url = null, bool preview = false)
            {
                using (var sha = SHA256.Create()) digest = digest ?? BitConverter.ToString(sha.ComputeHash(Payload)).Replace("-", "").ToLowerInvariant();
                url = url ?? "https://github.com/Yukimura434/MyPTB/releases/download/v" + version + "/MiuPhotoBooth-" + version + "-Setup.exe";
                Json = "{\"tag_name\":\"v" + version + "\",\"draft\":false,\"prerelease\":" + (preview ? "true" : "false") + ",\"assets\":[{\"name\":\"MiuPhotoBooth-" + version + "-Setup.exe\",\"size\":" + Payload.Length + ",\"digest\":\"sha256:" + digest + "\",\"browser_download_url\":\"" + url + "\"}]}";
            }
        }
        [Fact] public async Task Construction_and_check_never_download_or_install()
        {
            var h = new Handler(); h.Release(); var update = new GitHubInstallerUpdateService(new HttpClient(h), new Version(3,0,0,0), root);
            Assert.Equal(0,h.Calls); Assert.True(await update.IsUpdateAvailableAsync(CancellationToken.None)); Assert.Equal(1,h.Calls);
            Assert.Equal("3.0.1",await update.GetAvailableVersionAsync(CancellationToken.None)); Assert.Equal(1,h.Calls); Assert.False(Directory.Exists(root));
            var path=await update.DownloadUpdateAsync(CancellationToken.None); Assert.Equal(2,h.Calls); Assert.Equal(h.Payload,File.ReadAllBytes(path));
        }
        [Theory][InlineData("3.0.0")][InlineData("2.9.9")]
        public async Task Same_or_older_version_never_offers_downgrade(string version)
        { var h=new Handler();h.Release(version);var update=new GitHubInstallerUpdateService(new HttpClient(h),new Version(3,0,0,0),root);Assert.False(await update.IsUpdateAvailableAsync(CancellationToken.None));Assert.Null(await update.GetAvailableVersionAsync(CancellationToken.None)); }
        [Fact] public async Task Wrong_checksum_is_rejected_and_partial_removed()
        { var h=new Handler();h.Release(digest:new string('0',64));var update=new GitHubInstallerUpdateService(new HttpClient(h),new Version(3,0,0),root);Assert.True(await update.IsUpdateAvailableAsync(CancellationToken.None));await Assert.ThrowsAsync<InvalidDataException>(()=>update.DownloadUpdateAsync(CancellationToken.None));Assert.Empty(Directory.GetFiles(root,"*",SearchOption.AllDirectories)); }
        [Theory][InlineData("https://example.test/file.exe",false)][InlineData(null,true)]
        public async Task Foreign_asset_and_prerelease_are_rejected(string url,bool preview)
        {var h=new Handler();h.Release(url:url,preview:preview);var update=new GitHubInstallerUpdateService(new HttpClient(h),new Version(3,0,0),root);await Assert.ThrowsAsync<InvalidDataException>(()=>update.IsUpdateAvailableAsync(CancellationToken.None));Assert.False(Directory.Exists(root));}
        [Fact] public async Task Network_failure_clears_previous_offer()
        {var h=new Handler();h.Release();var update=new GitHubInstallerUpdateService(new HttpClient(h),new Version(3,0,0),root);Assert.True(await update.IsUpdateAvailableAsync(CancellationToken.None));h.Status=HttpStatusCode.ServiceUnavailable;await Assert.ThrowsAsync<HttpRequestException>(()=>update.IsUpdateAvailableAsync(CancellationToken.None));Assert.Null(await update.GetAvailableVersionAsync(CancellationToken.None));await Assert.ThrowsAsync<InvalidOperationException>(()=>update.DownloadUpdateAsync(CancellationToken.None));}
        public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
