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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Database;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    /// <summary>
    /// Device-side pull agent. It stages and verifies every frame before saving
    /// the event configuration, then acknowledges the remote assignment.
    /// </summary>
    public sealed class DeviceConfigurationSyncService : IDeviceConfigurationSyncService
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        const long MaxFrameBytes = 32L * 1024L * 1024L;
        readonly IDeviceIdentityRepository identityRepository;
        readonly IPcSessionContext sessionContext;
        readonly IFrameRepository frameRepository;
        readonly IEventService events;
        readonly IPhotoEventConfigurationRepository configurations;
        readonly IFileStorageService storage;
        readonly SqliteDatabase database;
        readonly ApplicationOptions options;
        readonly ILogger<DeviceConfigurationSyncService> logger;

        public DeviceConfigurationSyncService(IDeviceIdentityRepository identityRepository, IPcSessionContext sessionContext,
            IFrameRepository frameRepository, IEventService events,
            IPhotoEventConfigurationRepository configurations, IFileStorageService storage,
            SqliteDatabase database, ApplicationOptions options,
            ILogger<DeviceConfigurationSyncService> logger)
        {
            this.identityRepository = identityRepository;
            this.sessionContext = sessionContext;
            this.frameRepository = frameRepository;
            this.events = events;
            this.configurations = configurations;
            this.storage = storage;
            this.database = database;
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

            var frameCatalogChanged = await SyncFrameCatalogAsync(baseUrl, credential, cancellationToken).ConfigureAwait(false);
            var assignment = await GetAssignmentAsync(baseUrl, credential, cancellationToken).ConfigureAwait(false);
            if (assignment == null) return new DeviceConfigurationSyncResult
            {
                Applied = frameCatalogChanged,
                Message = frameCatalogChanged ? "Đã đồng bộ thư viện frame." : "Frame đã được cập nhật."
            };

            var result = new DeviceConfigurationSyncResult
            {
                HasAssignment = true,
                EventName = assignment.EventName,
                Version = assignment.Version
            };
            try
            {
                if (string.Equals(assignment.Operation, "archive", StringComparison.OrdinalIgnoreCase))
                {
                    ArchiveRemoteEvent(assignment.EventId);
                    await AcknowledgeAsync(baseUrl, credential, assignment.Id, "applied", null, cancellationToken).ConfigureAwait(false);
                    result.Applied = true;
                    result.Message = "Đã lưu trữ sự kiện theo Customer Web.";
                    return result;
                }
                var localEvent = await EnsureEventAsync(assignment.EventId, assignment.EventName, cancellationToken).ConfigureAwait(false);
                var localFrameIds = new List<Guid>();
                foreach (var remoteFrame in assignment.Frames ?? new RemoteFrame[0])
                    localFrameIds.Add(await EnsureFrameAsync(remoteFrame, credential, cancellationToken).ConfigureAwait(false));
                if (localFrameIds.Count == 0) throw new InvalidOperationException("Cấu hình từ xa không có frame hợp lệ.");

                var payload = assignment.Payload ?? new RemotePayload();
                var configuration = new PhotoEventConfiguration
                {
                    EventId = localEvent.Id,
                    BasePriceMinor = Math.Max(0, payload.BasePriceMinor),
                    AdditionalPrintPriceMinor = Math.Max(0, payload.AdditionalPrintPriceMinor),
                    Currency = string.IsNullOrWhiteSpace(payload.Currency) ? "VND" : payload.Currency,
                    PhotoCount = Clamp(payload.PhotoCount, 1, 8, 1),
                    CountdownSeconds = Clamp(payload.CountdownSeconds, 1, 10, 3),
                    GifFrameDurationMilliseconds = 1000,
                    WaitingTimeoutSeconds = 30,
                    CustomerLayoutMode = string.Equals(payload.CustomerLayoutMode, "portrait", StringComparison.OrdinalIgnoreCase) ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape,
                    ImageRotationDegrees = new[] { -90, 0, 90, 180 }.Contains(payload.ImageRotationDegrees) ? payload.ImageRotationDegrees : 0,
                    Beauty = new BeautySettings(),
                    FrameIds = localFrameIds,
                    PresetIds = new Guid[0],
                    ModifiedAtUtc = DateTime.UtcNow
                };
                await configurations.SaveAsync(localEvent.Name, configuration, cancellationToken).ConfigureAwait(false);
                await configurations.ActivateAsync(localEvent.Id, cancellationToken).ConfigureAwait(false);
                PersistAssignment(assignment, localEvent.Id);

                await AcknowledgeAsync(baseUrl, credential, assignment.Id, "applied", null, cancellationToken).ConfigureAwait(false);
                MarkLocalAssignmentApplied(assignment.Id);
                result.Applied = true;
                result.Message = "Đã đồng bộ cấu hình.";
                return result;
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Remote configuration could not be applied.");
                try { await AcknowledgeAsync(baseUrl, credential, assignment.Id, "failed", "Không thể áp dụng cấu hình trên thiết bị.", cancellationToken).ConfigureAwait(false); } catch { }
                MarkLocalAssignmentFailed(assignment.Id, "Không thể áp dụng cấu hình trên thiết bị.");
                result.Message = "Không thể áp dụng cấu hình từ xa.";
                return result;
            }
        }

        async Task<RemoteAssignment> GetAssignmentAsync(string baseUrl, string credential, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v3/device/configuration"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if ((int)response.StatusCode != 200) throw new InvalidOperationException("Không thể tải cấu hình từ xa.");
                    var envelope = Read<RemoteEnvelope>(body);
                    return envelope?.Assignment;
                }
            }
        }

        async Task<bool> SyncFrameCatalogAsync(string baseUrl, string credential, CancellationToken token)
        {
            RemoteFrame[] remoteFrames;
            using (var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v3/device/frames"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
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
            var changed = remoteLocals.Any(frame => !expectedIds.Contains(frame.Id)) ||
                expectedIds.Any(id => remoteLocals.All(frame => frame.Id != id || !File.Exists(frame.SourcePath)));

            foreach (var remoteFrame in remoteFrames)
                await EnsureFrameAsync(remoteFrame, credential, token).ConfigureAwait(false);

            foreach (var stale in remoteLocals.Where(frame => !expectedIds.Contains(frame.Id)))
            {
                await frameRepository.DeleteAsync(stale.Id, token).ConfigureAwait(false);
                try { if (File.Exists(stale.SourcePath)) File.Delete(stale.SourcePath); } catch (Exception exception) { logger?.LogDebug(exception, "Could not remove obsolete remote frame cache."); }
            }
            return changed;
        }

        async Task AcknowledgeAsync(string baseUrl, string credential, string assignmentId, string status, string error, CancellationToken token)
        {
            var body = Json(new ApplyRequest { AssignmentId = assignmentId, Status = status, Error = error });
            using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v3/device/configuration"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                var employeeToken = sessionContext.Current?.RemoteSessionToken;
                if (string.IsNullOrWhiteSpace(employeeToken)) throw new InvalidOperationException("Phiên nhân viên là bắt buộc để xác nhận cấu hình.");
                request.Headers.TryAddWithoutValidation("X-Employee-Session", employeeToken);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Không thể xác nhận trạng thái cấu hình.");
            }
        }

        async Task<Guid> EnsureFrameAsync(RemoteFrame remote, string credential, CancellationToken token)
        {
            if (remote == null || string.IsNullOrWhiteSpace(remote.Id) || string.IsNullOrWhiteSpace(remote.AssetUrl) || string.IsNullOrWhiteSpace(remote.ContentSha256))
                throw new InvalidOperationException("Frame từ xa thiếu thông tin bắt buộc.");
            if (remote.AssetBytes <= 0 || remote.AssetBytes > MaxFrameBytes) throw new InvalidOperationException("Kích thước frame không hợp lệ.");
            var versionIdentity = RemoteVersionIdentity(remote);
            var localId = RemoteLocalId(remote);
            var existing = await frameRepository.GetAsync(localId, token).ConfigureAwait(false);
            if (existing != null && File.Exists(existing.SourcePath)) return localId;

            byte[] bytes;
            using (var request = new HttpRequestMessage(HttpMethod.Get, remote.AssetUrl))
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
            var relative = "Frames\\Remote\\" + Safe(versionIdentity) + "." + Safe(remote.AssetFormat ?? "png");
            string path;
            using (var content = new MemoryStream(bytes, false)) path = await storage.SaveAsync(relative, content, token).ConfigureAwait(false);
            var remoteSlots = remote.Slots ?? new RemoteSlot[0];
            if (remoteSlots.Length < 1 || remoteSlots.Length > 8) throw new InvalidOperationException("Số ô ảnh của frame không hợp lệ.");
            var slots = remoteSlots.Select(slot =>
            {
                if (slot.Index < 0 || slot.X < 0 || slot.Y < 0 || slot.Width < 1 || slot.Height < 1 ||
                    slot.X + slot.Width > remote.PixelWidth || slot.Y + slot.Height > remote.PixelHeight)
                    throw new InvalidOperationException("Vị trí ô ảnh vượt khỏi kích thước frame.");
                return new FrameSlot { Id = Guid.NewGuid(), Index = slot.Index, X = slot.X, Y = slot.Y, Width = slot.Width, Height = slot.Height };
            }).OrderBy(slot => slot.Index).ToList();
            if (slots.Select(slot => slot.Index).Distinct().Count() != slots.Count || slots.Where((slot, index) => slot.Index != index).Any())
                throw new InvalidOperationException("Thứ tự ô ảnh của frame không hợp lệ.");
            await frameRepository.SaveAsync(new Frame { Id = localId, Name = remote.Name ?? "Remote frame", SourcePath = path, ThumbnailPath = path, PixelWidth = remote.PixelWidth, PixelHeight = remote.PixelHeight, Slots = slots, CreatedAtUtc = DateTime.UtcNow }, token).ConfigureAwait(false);
            return localId;
        }

        static Task<HttpResponseMessage> SendFrameRequestAsync(HttpRequestMessage request, string credential, CancellationToken token)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
            return Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
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

        async Task<PhotoEvent> EnsureEventAsync(string remoteId, string name, CancellationToken token)
        {
            PhotoEvent value = null;
            using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id,Name FROM Events WHERE RemoteEventId=$remote LIMIT 1";
                command.Parameters.AddWithValue("$remote", remoteId ?? string.Empty);
                using (var reader = command.ExecuteReader()) if (reader.Read()) value = new PhotoEvent { Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1) };
            }
            if (value == null)
            {
                var draft = await events.CreateDraftAsync(null, token).ConfigureAwait(false); draft.Name = string.IsNullOrWhiteSpace(name) ? "Remote event" : name;
                value = await events.CreateAsync(draft, token).ConfigureAwait(false);
                using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE Events SET RemoteEventId=$remote,Name=$name,UpdatedAtUtc=$updated WHERE Id=$id";
                    command.Parameters.AddWithValue("$remote", remoteId ?? string.Empty); command.Parameters.AddWithValue("$name", value.Name); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", value.Id.ToString()); command.ExecuteNonQuery();
                }
            }
            else if (!string.IsNullOrWhiteSpace(name) && !string.Equals(value.Name, name, StringComparison.Ordinal))
            {
                value.Name = name;
                using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand()) { command.CommandText = "UPDATE Events SET Name=$name,UpdatedAtUtc=$updated WHERE Id=$id"; command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", value.Id.ToString()); command.ExecuteNonQuery(); }
            }
            return value;
        }

        void ArchiveRemoteEvent(string remoteId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                // Keep sessions, captures and media immutable; only remove the event from active operation.
                command.CommandText = "UPDATE Events SET Status='Archived',IsDefault=0,UpdatedAtUtc=$updated,RowVersion=RowVersion+1 WHERE RemoteEventId=$remote";
                command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$remote", remoteId ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        void PersistAssignment(RemoteAssignment assignment, Guid localEventId)
        {
            using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id FROM ConfigurationVersions WHERE EventId=$event ORDER BY VersionNumber DESC LIMIT 1"; command.Parameters.AddWithValue("$event", localEventId.ToString()); var configId = command.ExecuteScalar() as string; if (string.IsNullOrWhiteSpace(configId)) return;
                command.Parameters.Clear(); command.CommandText = "INSERT OR REPLACE INTO DeviceConfigAssignments(Id,ConfigurationVersionId,RemoteAssignmentId,State,ReceivedAtUtc,AppliedAtUtc,LastError) VALUES($id,$config,$remote,'Received',$received,NULL,NULL)"; command.Parameters.AddWithValue("$id", "remote-" + assignment.Id); command.Parameters.AddWithValue("$config", configId); command.Parameters.AddWithValue("$remote", assignment.Id); command.Parameters.AddWithValue("$received", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
            }
        }
        void MarkLocalAssignmentApplied(string remoteId) => UpdateLocalAssignment(remoteId, "Applied", null, true);
        void MarkLocalAssignmentFailed(string remoteId, string error) => UpdateLocalAssignment(remoteId, "Failed", error, false);
        void UpdateLocalAssignment(string remoteId, string state, string error, bool applied)
        {
            try { using (var c = database.OpenConnection()) using (var q = c.CreateCommand()) { q.CommandText = "UPDATE DeviceConfigAssignments SET State=$state,AppliedAtUtc=CASE WHEN $applied=1 THEN $now ELSE NULL END,LastError=$error WHERE RemoteAssignmentId=$remote"; q.Parameters.AddWithValue("$state", state); q.Parameters.AddWithValue("$applied", applied ? 1 : 0); q.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); q.Parameters.AddWithValue("$error", (object)error ?? DBNull.Value); q.Parameters.AddWithValue("$remote", remoteId); q.ExecuteNonQuery(); } } catch { }
        }

        static int Clamp(int value, int min, int max, int fallback) => value < min || value > max ? fallback : value;
        static string Safe(string value) => new string((value ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        static byte[] Hex(string value) { if (value == null || value.Length != 64) throw new InvalidOperationException("Hash frame không hợp lệ."); var bytes = new byte[32]; for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return bytes; }
        static string Json<T>(T value) { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
        static T Read<T>(string value) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value ?? string.Empty))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }

        [DataContract] sealed class RemoteEnvelope { [DataMember(Name = "assignment")] public RemoteAssignment Assignment { get; set; } }
        [DataContract] sealed class RemoteFrameCatalogEnvelope { [DataMember(Name = "frames")] public RemoteFrame[] Frames { get; set; } }
        [DataContract] sealed class RemoteAssignment { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "eventId")] public string EventId { get; set; } [DataMember(Name = "eventName")] public string EventName { get; set; } [DataMember(Name = "operation")] public string Operation { get; set; } [DataMember(Name = "version")] public int Version { get; set; } [DataMember(Name = "payload")] public RemotePayload Payload { get; set; } [DataMember(Name = "frames")] public RemoteFrame[] Frames { get; set; } }
        [DataContract] sealed class RemotePayload { [DataMember(Name = "basePriceMinor")] public long BasePriceMinor { get; set; } [DataMember(Name = "additionalPrintPriceMinor")] public long AdditionalPrintPriceMinor { get; set; } [DataMember(Name = "currency")] public string Currency { get; set; } [DataMember(Name = "photoCount")] public int PhotoCount { get; set; } [DataMember(Name = "countdownSeconds")] public int CountdownSeconds { get; set; } [DataMember(Name = "customerLayoutMode")] public string CustomerLayoutMode { get; set; } [DataMember(Name = "imageRotationDegrees")] public int ImageRotationDegrees { get; set; } }
        [DataContract] sealed class RemoteFrame { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "frameVersionId")] public string FrameVersionId { get; set; } [DataMember(Name = "name")] public string Name { get; set; } [DataMember(Name = "version")] public int Version { get; set; } [DataMember(Name = "assetUrl")] public string AssetUrl { get; set; } [DataMember(Name = "assetFormat")] public string AssetFormat { get; set; } [DataMember(Name = "assetBytes")] public long AssetBytes { get; set; } [DataMember(Name = "contentSha256")] public string ContentSha256 { get; set; } [DataMember(Name = "pixelWidth")] public int PixelWidth { get; set; } [DataMember(Name = "pixelHeight")] public int PixelHeight { get; set; } [DataMember(Name = "slots")] public RemoteSlot[] Slots { get; set; } }
        [DataContract] sealed class RemoteSlot { [DataMember(Name = "index")] public int Index { get; set; } [DataMember(Name = "x")] public int X { get; set; } [DataMember(Name = "y")] public int Y { get; set; } [DataMember(Name = "width")] public int Width { get; set; } [DataMember(Name = "height")] public int Height { get; set; } }
        [DataContract] sealed class ApplyRequest { [DataMember(Name = "assignmentId")] public string AssignmentId { get; set; } [DataMember(Name = "status")] public string Status { get; set; } [DataMember(Name = "error", EmitDefaultValue = false)] public string Error { get; set; } }
    }
}
