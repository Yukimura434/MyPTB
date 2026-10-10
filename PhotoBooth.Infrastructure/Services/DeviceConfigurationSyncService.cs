using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    /// <summary>
    /// Refreshes the shared frame library without replacing local booth settings.
    /// </summary>
    public sealed class DeviceConfigurationSyncService : IDeviceConfigurationSyncService
    {
        static readonly HttpClient Http = ApiHttpTransport.Create(TimeSpan.FromSeconds(60));
        const long MaxFrameBytes = 32L * 1024L * 1024L;
        readonly IDeviceIdentityRepository identityRepository;
        readonly IFrameRepository frameRepository;
        readonly IFileStorageService storage;
        readonly IFrameAnalyzer frameAnalyzer;
        readonly ApplicationOptions options;
        readonly ILogger<DeviceConfigurationSyncService> logger;

        public DeviceConfigurationSyncService(IDeviceIdentityRepository identityRepository,
            IFrameRepository frameRepository, IFileStorageService storage,
            IFrameAnalyzer frameAnalyzer, ApplicationOptions options,
            ILogger<DeviceConfigurationSyncService> logger)
        {
            this.identityRepository = identityRepository;
            this.frameRepository = frameRepository;
            this.storage = storage;
            this.frameAnalyzer = frameAnalyzer;
            this.options = options;
            this.logger = logger;
        }

        public async Task<DeviceConfigurationSyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            var identity = await identityRepository.GetAsync(cancellationToken).ConfigureAwait(false);
            if (identity == null || identity.EnrollmentState != DeviceEnrollmentState.Enrolled || string.IsNullOrWhiteSpace(identity.DeviceId))
                return new DeviceConfigurationSyncResult { Message = "Thiết bị chưa được liên kết." };

            var protectedCredential = await identityRepository.GetProtectedCredentialAsync(cancellationToken).ConfigureAwait(false);
            if (protectedCredential == null || protectedCredential.Length == 0)
                return new DeviceConfigurationSyncResult { Message = "Thiết bị chưa có thông tin xác thực." };

            var credential = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedCredential, null, DataProtectionScope.CurrentUser));
            var baseUrl = (options.PhotoApiBaseUrl ?? string.Empty).TrimEnd('/');
            if (baseUrl.Length == 0) throw new InvalidOperationException("Photo API base URL is not configured.");

            // Catalog refresh failure preserves the last verified local library.
            var frameCatalogChanged = false;
            try
            {
                frameCatalogChanged = await SyncFrameCatalogAsync(baseUrl, credential, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "The shared frame catalog refresh is pending; continuing with the last known good catalog.");
            }
            // Event assets are selected locally. Catalog sync must never replace
            // a booth's selected configuration with an Event deployment.
            return new DeviceConfigurationSyncResult
            {
                Applied = frameCatalogChanged,
                Message = frameCatalogChanged ? "Đã đồng bộ thư viện frame." : "Frame đã được cập nhật."
            };
        }

        async Task<bool> SyncFrameCatalogAsync(string baseUrl, string credential, CancellationToken token)
        {
            RemoteFrame[] remoteFrames;
            using (var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v3/device/frames"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                await DeviceRequestProof.SignAsync(request,identityRepository,credential,null,token).ConfigureAwait(false);
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Không thể tải thư viện frame từ xa.");
                    remoteFrames = Read<RemoteFrameCatalogEnvelope>(body)?.Frames ?? new RemoteFrame[0];
                }
            }

            var localFrames = await frameRepository.GetAllAsync(token).ConfigureAwait(false);
            var remoteLocals = localFrames.Where(frame => IsRemoteFrame(frame.SourcePath)).ToList();
            var expectedIds = new HashSet<Guid>(remoteFrames.Select(RemoteLocalId));
            // Older immutable versions are intentionally retained while an active run
            // may still reference them. Their presence is therefore not a sync change.
            var changed = expectedIds.Any(id => remoteLocals.All(frame => frame.Id != id || !File.Exists(frame.SourcePath)));

            foreach (var remoteFrame in remoteFrames)
                await EnsureFrameAsync(remoteFrame, credential, token).ConfigureAwait(false);

            // Do not delete an older cached version here. It can still be referenced by
            // the currently active event or by a customer-run snapshot. Cleanup belongs
            // to a separate retention job after references have been proven absent.
            return changed;
        }

        async Task<Guid> EnsureFrameAsync(RemoteFrame remote, string credential, CancellationToken token)
        {
            if (remote == null || string.IsNullOrWhiteSpace(remote.Id) || string.IsNullOrWhiteSpace(remote.AssetUrl) || string.IsNullOrWhiteSpace(remote.ContentSha256))
                throw new InvalidOperationException("Frame từ xa thiếu thông tin bắt buộc.");
            if (remote.AssetBytes <= 0 || remote.AssetBytes > MaxFrameBytes) throw new InvalidOperationException("Kích thước frame không hợp lệ.");
            var versionIdentity = RemoteVersionIdentity(remote);
            var localId = RemoteLocalId(remote);
            var existing = await frameRepository.GetAsync(localId, token).ConfigureAwait(false);
            if (existing != null && File.Exists(existing.SourcePath) && FileMatchesSha256(existing.SourcePath, remote.ContentSha256))
            {
                var reconciled = ReconcileFrameSlots(existing.SourcePath, remote);
                if (!SameGeometry(existing.Slots, reconciled))
                {
                    existing.Slots = reconciled;
                    await frameRepository.SaveAsync(existing, token).ConfigureAwait(false);
                    logger?.LogInformation("Corrected remote frame slot metadata for cached frame {FrameId}.", localId);
                }
                return localId;
            }

            byte[] bytes;
            using (var request = new HttpRequestMessage(HttpMethod.Get, TrustedAssetUri(remote.AssetUrl)))
            using (var response = await SendFrameRequestAsync(request, credential, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Không thể tải frame từ xa.");
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[81920]; int read; long total = 0;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        total += read; if (total > MaxFrameBytes) throw new InvalidOperationException("Frame vượt quá giới hạn cho phép.");
                        memory.Write(buffer, 0, read);
                    }
                    bytes = memory.ToArray();
                }
            }
            if (bytes.LongLength != remote.AssetBytes) throw new InvalidOperationException("Kích thước frame không khớp.");
            using (var sha = SHA256.Create())
            {
                var actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
                if (!string.Equals(actual, remote.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Xác thực toàn vẹn frame thất bại.");
            }
            // A cache path is immutable and content-addressed. Even if an upstream
            // version identifier is accidentally reused, a running WPF view never has
            // its image overwritten underneath it.
            var relative = "Frames\\Remote\\" + Safe(versionIdentity) + "-" + remote.ContentSha256.ToLowerInvariant() + "." + Safe(remote.AssetFormat ?? "png");
            string path;
            var expectedPath = Path.Combine(options.DataDirectory, relative);
            if (File.Exists(expectedPath) && FileMatchesSha256(expectedPath, remote.ContentSha256)) path = expectedPath;
            else using (var content = new MemoryStream(bytes, false)) path = await storage.SaveAsync(relative, content, token).ConfigureAwait(false);
            var slots = ReconcileFrameSlots(path, remote);
            await frameRepository.SaveAsync(new Frame { Id = localId, Name = remote.Name ?? "Remote frame", SourcePath = path, ThumbnailPath = path, PixelWidth = remote.PixelWidth, PixelHeight = remote.PixelHeight, Slots = slots, CreatedAtUtc = DateTime.UtcNow }, token).ConfigureAwait(false);
            return localId;
        }

        IReadOnlyList<FrameSlot> ReconcileFrameSlots(string path, RemoteFrame remote)
        {
            var remoteSlots = remote.Slots ?? new RemoteSlot[0];
            if (remoteSlots.Length < 1 || remoteSlots.Length > 8) throw new InvalidOperationException("Số ô ảnh của frame không hợp lệ.");
            var supplied = remoteSlots.Select(slot =>
            {
                if (slot.Index < 0 || slot.X < 0 || slot.Y < 0 || slot.Width < 1 || slot.Height < 1 ||
                    slot.X + slot.Width > remote.PixelWidth || slot.Y + slot.Height > remote.PixelHeight)
                    throw new InvalidOperationException("Vị trí ô ảnh vượt khỏi kích thước frame.");
                return new FrameSlot { Id = Guid.NewGuid(), Index = slot.Index, X = slot.X, Y = slot.Y, Width = slot.Width, Height = slot.Height };
            }).OrderBy(slot => slot.Index).ToList();
            if (supplied.Select(slot => slot.Index).Distinct().Count() != supplied.Count || supplied.Where((slot, index) => slot.Index != index).Any())
                throw new InvalidOperationException("Thứ tự ô ảnh của frame không hợp lệ.");
            var analyzed = frameAnalyzer.Analyze(path, new FrameAnalysisOptions());
            if (analyzed.PixelWidth != remote.PixelWidth || analyzed.PixelHeight != remote.PixelHeight)
                throw new InvalidOperationException("Kích thước frame không khớp metadata.");
            return RemoteFrameSlotReconciler.Reconcile(supplied, analyzed.Slots);
        }

        static bool SameGeometry(IReadOnlyList<FrameSlot> left, IReadOnlyList<FrameSlot> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            return !left.OrderBy(slot => slot.Index).Zip(right.OrderBy(slot => slot.Index), (a, b) =>
                a.Index == b.Index && a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height).Contains(false);
        }

        async Task<HttpResponseMessage> SendFrameRequestAsync(HttpRequestMessage request, string credential, CancellationToken token)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
            await DeviceRequestProof.SignAsync(request,identityRepository,credential,null,token).ConfigureAwait(false);
            return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }

        Uri TrustedAssetUri(string value)
        {
            Uri asset,origin;
            if(!Uri.TryCreate(value,UriKind.Absolute,out asset)||!Uri.TryCreate(options.PhotoApiBaseUrl,UriKind.Absolute,out origin) ||
               !string.Equals(asset.Scheme,origin.Scheme,StringComparison.OrdinalIgnoreCase)||
               !string.Equals(asset.Host,origin.Host,StringComparison.OrdinalIgnoreCase)||asset.Port!=origin.Port||
               (!string.Equals(asset.Scheme,Uri.UriSchemeHttps,StringComparison.OrdinalIgnoreCase)&&!origin.IsLoopback))
                throw new InvalidOperationException("Địa chỉ tài nguyên cấu hình không an toàn.");
            return asset;
        }

        static string RemoteVersionIdentity(RemoteFrame remote) => string.IsNullOrWhiteSpace(remote.FrameVersionId)
            ? (remote.Id + ":" + remote.Version)
            : remote.FrameVersionId;

        static Guid RemoteLocalId(RemoteFrame remote)
        {
            using (var identityHash = SHA256.Create())
                return new Guid(identityHash.ComputeHash(Encoding.UTF8.GetBytes(RemoteVersionIdentity(remote))).Take(16).ToArray());
        }

        static bool IsRemoteFrame(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var normalized = path.Replace('/', '\\');
            return normalized.IndexOf("\\Frames\\Remote\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool FileMatchesSha256(string path, string expected)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expected) || !File.Exists(path)) return false;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                    return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        static string Safe(string value) => new string((value ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        static byte[] Hex(string value) { if (value == null || value.Length != 64) throw new InvalidOperationException("Hash frame không hợp lệ."); var bytes = new byte[32]; for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return bytes; }
        static T Read<T>(string value) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value ?? string.Empty))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }

        [DataContract] sealed class RemoteFrameCatalogEnvelope { [DataMember(Name = "frames")] public RemoteFrame[] Frames { get; set; } }
        [DataContract] sealed class RemoteFrame { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "frameVersionId")] public string FrameVersionId { get; set; } [DataMember(Name = "name")] public string Name { get; set; } [DataMember(Name = "version")] public int Version { get; set; } [DataMember(Name = "assetUrl")] public string AssetUrl { get; set; } [DataMember(Name = "assetFormat")] public string AssetFormat { get; set; } [DataMember(Name = "assetBytes")] public long AssetBytes { get; set; } [DataMember(Name = "contentSha256")] public string ContentSha256 { get; set; } [DataMember(Name = "pixelWidth")] public int PixelWidth { get; set; } [DataMember(Name = "pixelHeight")] public int PixelHeight { get; set; } [DataMember(Name = "slots")] public RemoteSlot[] Slots { get; set; } }
        [DataContract] sealed class RemoteSlot { [DataMember(Name = "index")] public int Index { get; set; } [DataMember(Name = "x")] public int X { get; set; } [DataMember(Name = "y")] public int Y { get; set; } [DataMember(Name = "width")] public int Width { get; set; } [DataMember(Name = "height")] public int Height { get; set; } }
    }
}
