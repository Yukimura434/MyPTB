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
        static readonly HttpClient Http = ApiHttpTransport.Create(TimeSpan.FromSeconds(60));
        const long MaxFrameBytes = 32L * 1024L * 1024L;
        const long MaxColorPresetBytes = 2L * 1024L * 1024L;
        readonly IDeviceIdentityRepository identityRepository;
        readonly IPcSessionContext sessionContext;
        readonly IFrameRepository frameRepository;
        readonly IEventService events;
        readonly IPhotoEventConfigurationRepository configurations;
        readonly IFileStorageService storage;
        readonly IFrameAnalyzer frameAnalyzer;
        readonly IColorLutService colorLuts;
        readonly IPresetService presets;
        readonly SqliteDatabase database;
        readonly ApplicationOptions options;
        readonly ILogger<DeviceConfigurationSyncService> logger;

        public DeviceConfigurationSyncService(IDeviceIdentityRepository identityRepository, IPcSessionContext sessionContext,
            IFrameRepository frameRepository, IEventService events,
            IPhotoEventConfigurationRepository configurations, IFileStorageService storage,
            IFrameAnalyzer frameAnalyzer, IColorLutService colorLutService, IPresetService presetService,
            SqliteDatabase database, ApplicationOptions options,
            ILogger<DeviceConfigurationSyncService> logger)
        {
            this.identityRepository = identityRepository;
            this.sessionContext = sessionContext;
            this.frameRepository = frameRepository;
            this.events = events;
            this.configurations = configurations;
            this.storage = storage;
            this.frameAnalyzer = frameAnalyzer;
            colorLuts = colorLutService;
            presets = presetService;
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

            // The shared catalog is convenience data. A broken frame that is unrelated
            // to the pending event must not prevent that event from being imported.
            // Required assignment frames are downloaded and validated again below.
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
                Version = assignment.Version,
                DeploymentState = assignment.DeploymentState
            };

            // Reconcile acknowledgements without re-downloading an already verified
            // immutable configuration. Ready means available locally; Applied is only
            // reported after the operator selects that exact version on this device.
            var localState = LocalAssignmentState(assignment.Id);
            if (string.Equals(localState,"Ready",StringComparison.OrdinalIgnoreCase) || string.Equals(localState,"Applied",StringComparison.OrdinalIgnoreCase))
            {
                var selected=string.Equals(localState,"Applied",StringComparison.OrdinalIgnoreCase)||IsLocalAssignmentSelected(assignment.Id);
                await ReconcileReadyAcknowledgementAsync(baseUrl,credential,assignment,selected,cancellationToken).ConfigureAwait(false);
                if(selected)MarkLocalAssignmentApplied(assignment.Id);
                result.Applied = selected;
                result.DeploymentState=selected?"Applied":"Ready";
                result.Message = selected ? "Cấu hình đang được dùng trên máy này." : "Cấu hình đã sẵn sàng. Người vận hành có thể chọn để sử dụng.";
                return result;
            }

            try
            {
                await TryAcknowledgeAsync(baseUrl,credential,assignment.Id,"downloading",null,null,cancellationToken).ConfigureAwait(false);
                if (string.Equals(assignment.Operation, "archive", StringComparison.OrdinalIgnoreCase))
                {
                    BeginDeployment(assignment);
                    ArchiveRemoteEvent(assignment.EventId,assignment.EventStatus);
                    PersistArchiveAssignment(assignment,"Ready");
                    await TryAcknowledgeAsync(baseUrl,credential,assignment.Id,"ready",null,null,cancellationToken).ConfigureAwait(false);
                    MarkLocalAssignmentApplied(assignment.Id);
                    await TryAcknowledgeAsync(baseUrl, credential, assignment.Id, "applied", null,null, cancellationToken).ConfigureAwait(false);
                    result.Applied = true;
                    result.DeploymentState="Applied";
                    result.Message = "Đã cập nhật trạng thái sự kiện.";
                    return result;
                }
                var localEvent = await EnsureEventAsync(assignment.EventId, assignment.EventName,assignment.EventStatus, cancellationToken).ConfigureAwait(false);
                BeginDeployment(assignment);
                var localFrameIds = new List<Guid>();
                foreach (var remoteFrame in assignment.Frames ?? new RemoteFrame[0])
                {
                    SetDeploymentAssetState(assignment.Id,remoteFrame,"Downloading",null,null);
                    try
                    {
                        localFrameIds.Add(await EnsureFrameAsync(remoteFrame, credential, cancellationToken).ConfigureAwait(false));
                        SetDeploymentAssetState(assignment.Id,remoteFrame,"Verified",null,null);
                    }
                    catch(Exception error)
                    {
                        SetDeploymentAssetState(assignment.Id,remoteFrame,"Failed","asset_validation",SafeError(error));
                        throw;
                    }
                }
                if (localFrameIds.Count == 0) throw new InvalidOperationException("Cấu hình từ xa không có frame hợp lệ.");

                var payload = assignment.Payload ?? new RemotePayload();
                var localPresetIds = new List<Guid>();
                if(payload.ColorPreset!=null)
                {
                    SetDeploymentColorPresetState(assignment.Id,payload.ColorPreset,"Downloading",null,null);
                    try
                    {
                        localPresetIds.Add(await EnsureColorPresetAsync(payload.ColorPreset,credential,cancellationToken).ConfigureAwait(false));
                        SetDeploymentColorPresetState(assignment.Id,payload.ColorPreset,"Verified",null,null);
                    }
                    catch(Exception error)
                    {
                        SetDeploymentColorPresetState(assignment.Id,payload.ColorPreset,"Failed","asset_validation",SafeError(error));
                        throw;
                    }
                }
                var printPolicyMode=ParsePrintPolicyMode(payload.PrintPolicy?.Mode);
                var includedPrintCopies=payload.PrintPolicy?.IncludedCopies??1;
                var maximumPrintCopies=payload.PrintPolicy?.MaximumCopies??10;
                if(printPolicyMode==EventPrintPolicyMode.Disabled){if(includedPrintCopies!=0)throw new InvalidOperationException("Chính sách in đã tắt nhưng vẫn có số bản in.");maximumPrintCopies=0;}
                else if(includedPrintCopies<1||includedPrintCopies>10||maximumPrintCopies<includedPrintCopies||maximumPrintCopies>20)
                    throw new InvalidOperationException("Chính sách in trong cấu hình không hợp lệ.");
                var configuration = new PhotoEventConfiguration
                {
                    EventId = localEvent.Id,
                    BasePriceMinor = Math.Max(0, payload.BasePriceMinor),
                    AdditionalPrintPriceMinor = Math.Max(0, payload.AdditionalPrintPriceMinor),
                    Currency = string.IsNullOrWhiteSpace(payload.Currency) ? "VND" : payload.Currency,
                    PhotoCount = Clamp(payload.PhotoCount, 1, 8, 1),
                    CountdownSeconds = Clamp(payload.CountdownSeconds, 1, 10, 3),
                    GifFrameDurationMilliseconds = Clamp(payload.GifFrameDurationMilliseconds, 400, 1000, 1000),
                    WaitingTimeoutSeconds = WaitingTimeout(payload.WaitingTimeoutSeconds),
                    CustomerLayoutMode = payload.CustomerLayoutMode == 1 ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape,
                    ImageRotationDegrees = new[] { -90, 0, 90, 180 }.Contains(payload.ImageRotationDegrees) ? payload.ImageRotationDegrees : 0,
                    Beauty = payload.Beauty == null ? new BeautySettings() : new BeautySettings
                    {
                        Enabled=payload.Beauty.Enabled,SmoothSkin=Clamp(payload.Beauty.SmoothSkin,0,100,0),BrightenSkin=Clamp(payload.Beauty.BrightenSkin,0,100,0),
                        SkinTone=Clamp(payload.Beauty.SkinTone,0,100,0),Sharpen=Clamp(payload.Beauty.Sharpen,0,100,0),EyeSize=Clamp(payload.Beauty.EyeSize,0,100,0),
                        SlimFace=Clamp(payload.Beauty.SlimFace,0,100,0),BlemishRemoval=Clamp(payload.Beauty.BlemishRemoval,0,100,0),DarkCircleReduction=Clamp(payload.Beauty.DarkCircleReduction,0,100,0)
                    },
                    PrintPolicyMode = printPolicyMode,
                    IncludedPrintCopies = includedPrintCopies,
                    MaximumPrintCopies = maximumPrintCopies,
                    ColorPresetStrengthPercent = payload.ColorPreset == null ? 50 : Clamp(payload.ColorPreset.StrengthPercent,0,100,50),
                    FrameIds = localFrameIds,
                    PresetIds = localPresetIds,
                    ModifiedAtUtc = DateTime.UtcNow
                };
                var existingConfiguration = await configurations.GetAsync(localEvent.Id, cancellationToken).ConfigureAwait(false);
                configuration.RowVersion = existingConfiguration?.RowVersion ?? 0;
                // Remote delivery only stages a Ready configuration. Selecting the event
                // in MiuManager is the sole operation allowed to activate it.
                await configurations.SaveAsync(localEvent.Name, configuration, cancellationToken).ConfigureAwait(false);
                PersistAssignment(assignment, localEvent.Id);

                // The local commit is already complete. A temporary network/authentication
                // failure here must not turn a valid local snapshot into a failed one.
                await TryAcknowledgeAsync(baseUrl, credential, assignment.Id, "ready", null,null, cancellationToken).ConfigureAwait(false);
                result.Applied = true;
                result.DeploymentState="Ready";
                result.Message = "Đã lưu sự kiện vào thư viện. Người vận hành có thể chọn để sử dụng.";
                return result;
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Remote configuration could not be imported.");
                var category=ConfigurationErrorCategory(exception);
                MarkLocalAssignmentFailed(assignment.Id,category,SafeError(exception));
                await TryAcknowledgeAsync(baseUrl, credential, assignment.Id, "failed",category, "Không thể lưu cấu hình trên thiết bị.", cancellationToken).ConfigureAwait(false);
                result.DeploymentState="Failed";
                result.Message = "Không thể lưu sự kiện từ xa.";
                return result;
            }
        }

        async Task<RemoteAssignment> GetAssignmentAsync(string baseUrl, string credential, CancellationToken token)
        {
            var eventId = sessionContext.Current?.EventAuthorization?.EventId;
            using (var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v3/device/configuration" + (string.IsNullOrWhiteSpace(eventId) ? "" : "?eventId=" + Uri.EscapeDataString(eventId))))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                await DeviceRequestProof.SignAsync(request,identityRepository,credential,null,token).ConfigureAwait(false);
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if ((int)response.StatusCode != 200) throw new InvalidOperationException("Không thể tải cấu hình từ xa.");
                    var envelope = Read<RemoteEnvelope>(body);
                    var assignment=envelope?.Assignment;
                    if(assignment!=null)ValidateAssignmentManifest(assignment);
                    return assignment;
                }
            }
        }

        static void ValidateAssignmentManifest(RemoteAssignment assignment)
        {
            if(string.IsNullOrWhiteSpace(assignment.ManifestJson)||string.IsNullOrWhiteSpace(assignment.ManifestSha256))throw new InvalidOperationException("Manifest cấu hình thiếu thông tin toàn vẹn.");
            string actual;using(var sha=SHA256.Create())actual=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(assignment.ManifestJson))).Replace("-",string.Empty).ToLowerInvariant();
            if(!string.Equals(actual,assignment.ManifestSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Xác thực toàn vẹn manifest cấu hình thất bại.");
            var payload=Read<RemotePayload>(assignment.ManifestJson);
            if(payload==null)throw new InvalidOperationException("Manifest cấu hình không hợp lệ.");
            assignment.Payload=payload;
            assignment.Frames=payload.Frames??new RemoteFrame[0];
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

        async Task AcknowledgeAsync(string baseUrl, string credential, string assignmentId, string status, string errorCategory,string error, CancellationToken token)
        {
            var body = Json(new ApplyRequest { AssignmentId = assignmentId, Status = status, ErrorCategory=errorCategory,Error = error });
            using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v3/device/configuration"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                var employeeToken = sessionContext.Current?.RemoteSessionToken;
                if (!string.IsNullOrWhiteSpace(employeeToken)) request.Headers.TryAddWithoutValidation("X-Employee-Session", employeeToken);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                await DeviceRequestProof.SignAsync(request,identityRepository,credential,null,token).ConfigureAwait(false);
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Không thể xác nhận trạng thái cấu hình.");
            }
        }

        async Task TryAcknowledgeAsync(string baseUrl, string credential, string assignmentId, string status,string errorCategory, string error, CancellationToken token)
        {
            try { await AcknowledgeAsync(baseUrl, credential, assignmentId, status,errorCategory, error, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Remote acknowledgement is pending for configuration {AssignmentId} with status {Status}.", assignmentId, status);
            }
        }

        async Task ReconcileReadyAcknowledgementAsync(string baseUrl,string credential,RemoteAssignment assignment,bool applied,CancellationToken token)
        {
            if(!string.Equals(assignment.DeploymentState,"ready",StringComparison.OrdinalIgnoreCase))
                await TryAcknowledgeAsync(baseUrl,credential,assignment.Id,"downloading",null,null,token).ConfigureAwait(false);
            await TryAcknowledgeAsync(baseUrl,credential,assignment.Id,"ready",null,null,token).ConfigureAwait(false);
            if(applied)await TryAcknowledgeAsync(baseUrl,credential,assignment.Id,"applied",null,null,token).ConfigureAwait(false);
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

        async Task<Guid> EnsureColorPresetAsync(RemoteColorPreset remote,string credential,CancellationToken token)
        {
            if(remote==null||string.IsNullOrWhiteSpace(remote.PresetVersionId)||string.IsNullOrWhiteSpace(remote.AssetUrl)||
               string.IsNullOrWhiteSpace(remote.ContentSha256)||!string.Equals(remote.AssetFormat,"cube",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Preset màu từ xa thiếu thông tin bắt buộc.");
            if(remote.AssetBytes<=0||remote.AssetBytes>MaxColorPresetBytes||remote.CubeSize<2||remote.CubeSize>128)
                throw new InvalidOperationException("Thông tin preset màu không hợp lệ.");
            var existingAssets=await colorLuts.GetAllAsync(token).ConfigureAwait(false);
            var existingAsset=existingAssets.FirstOrDefault(asset=>asset.Status==ColorLutAssetStatus.Ready&&string.Equals(asset.ContentHashSha256,remote.ContentSha256,StringComparison.OrdinalIgnoreCase));
            if(existingAsset!=null)
            {
                var existingPreset=(await presets.GetAllAsync(token).ConfigureAwait(false)).FirstOrDefault(preset=>preset.LutAssetId==existingAsset.Id);
                if(existingPreset!=null)return existingPreset.Id;
            }
            byte[] bytes;
            using(var request=new HttpRequestMessage(HttpMethod.Get,TrustedAssetUri(remote.AssetUrl)))
            using(var response=await SendFrameRequestAsync(request,credential,token).ConfigureAwait(false))
            {
                if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Không thể tải preset màu từ xa.");
                using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using(var memory=new MemoryStream())
                {
                    var buffer=new byte[81920];int read;long total=0;
                    while((read=await stream.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0)
                    {
                        total+=read;if(total>MaxColorPresetBytes)throw new InvalidOperationException("Preset màu vượt quá giới hạn cho phép.");
                        memory.Write(buffer,0,read);
                    }
                    bytes=memory.ToArray();
                }
            }
            if(bytes.LongLength!=remote.AssetBytes)throw new InvalidOperationException("Kích thước preset màu không khớp.");
            using(var sha=SHA256.Create())
            {
                var actual=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",string.Empty).ToLowerInvariant();
                if(!string.Equals(actual,remote.ContentSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Xác thực toàn vẹn preset màu thất bại.");
            }
            var directory=Path.Combine(options.DataDirectory,"Temp","RemoteColorPresets");Directory.CreateDirectory(directory);
            var temporary=Path.Combine(directory,Safe(remote.PresetVersionId)+"-"+remote.ContentSha256.ToLowerInvariant()+".cube");
            try
            {
                File.WriteAllBytes(temporary,bytes);
                var imported=await colorLuts.ImportAsync(temporary,remote.Name??"Preset màu",token).ConfigureAwait(false);
                var allPresets=await presets.GetAllAsync(token).ConfigureAwait(false);
                var preset=allPresets.FirstOrDefault(value=>value.LutAssetId==imported.Asset.Id);
                if(preset!=null)return preset.Id;
                var now=DateTime.UtcNow;
                preset=new Preset{Id=Guid.NewGuid(),Name=remote.Name??"Preset màu",CaptureCountdownSeconds=3,CreatedAtUtc=now,ModifiedAtUtc=now};
                await presets.SaveAsync(preset,token).ConfigureAwait(false);
                try{await colorLuts.AttachAsync(preset.Id,imported.Asset.Id,token).ConfigureAwait(false);}
                catch{await presets.DeleteAsync(preset.Id,token).ConfigureAwait(false);throw;}
                return preset.Id;
            }
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch{}}
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

        async Task<PhotoEvent> EnsureEventAsync(string remoteId, string name,string remoteLifecycle, CancellationToken token)
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
                    command.CommandText = "UPDATE Events SET RemoteEventId=$remote,Name=$name,LifecycleState=$lifecycle,OpenedAtUtc=CASE WHEN $lifecycle='Open' THEN COALESCE(OpenedAtUtc,$updated) ELSE OpenedAtUtc END,UpdatedAtUtc=$updated WHERE Id=$id";
                    command.Parameters.AddWithValue("$remote", remoteId ?? string.Empty); command.Parameters.AddWithValue("$name", value.Name);command.Parameters.AddWithValue("$lifecycle",RemoteLifecycle(remoteLifecycle)); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", value.Id.ToString()); command.ExecuteNonQuery();
                }
            }
            else if (!string.IsNullOrWhiteSpace(name) && !string.Equals(value.Name, name, StringComparison.Ordinal))
            {
                value.Name = name;
                using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand()) { command.CommandText = "UPDATE Events SET Name=$name,UpdatedAtUtc=$updated WHERE Id=$id"; command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", value.Id.ToString()); command.ExecuteNonQuery(); }
            }
            var lifecycle=RemoteLifecycle(remoteLifecycle);
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText="UPDATE Events SET LifecycleState=$lifecycle,OpenedAtUtc=CASE WHEN $lifecycle='Open' THEN COALESCE(OpenedAtUtc,$updated) ELSE OpenedAtUtc END,UpdatedAtUtc=$updated WHERE Id=$id";
                command.Parameters.AddWithValue("$lifecycle",lifecycle);command.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$id",value.Id.ToString());command.ExecuteNonQuery();
            }
            value.LifecycleState=lifecycle;
            return value;
        }

        void ArchiveRemoteEvent(string remoteId,string remoteLifecycle)
        {
            using (var connection = database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                // Keep sessions, captures and media immutable; only remove the event from active operation.
                var lifecycle=RemoteLifecycle(remoteLifecycle);
                var updated=DateTime.UtcNow.ToString("O");
                using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText = @"UPDATE Events SET Status=CASE WHEN $lifecycle='Archived' THEN 'Archived' ELSE 'Active' END,LifecycleState=$lifecycle,
ClosedAtUtc=CASE WHEN $lifecycle IN ('Closed','Delivered','Archived') THEN COALESCE(ClosedAtUtc,$updated) ELSE ClosedAtUtc END,
DeliveredAtUtc=CASE WHEN $lifecycle IN ('Delivered','Archived') THEN COALESCE(DeliveredAtUtc,$updated) ELSE DeliveredAtUtc END,
ArchivedAtUtc=CASE WHEN $lifecycle='Archived' THEN COALESCE(ArchivedAtUtc,$updated) ELSE ArchivedAtUtc END,
UpdatedAtUtc=$updated,RowVersion=RowVersion+1 WHERE RemoteEventId=$remote";command.Parameters.AddWithValue("$lifecycle",lifecycle);command.Parameters.AddWithValue("$updated",updated);command.Parameters.AddWithValue("$remote",remoteId??string.Empty);command.ExecuteNonQuery();}
                using(var clear=connection.CreateCommand()){clear.Transaction=transaction;clear.CommandText="UPDATE DeviceRuntimeState SET SelectedEventId=NULL,SelectedConfigurationVersionId=NULL,SelectedAtUtc=NULL,UpdatedAtUtc=$updated WHERE SelectedEventId=(SELECT Id FROM Events WHERE RemoteEventId=$remote LIMIT 1)";clear.Parameters.AddWithValue("$updated",updated);clear.Parameters.AddWithValue("$remote",remoteId??string.Empty);clear.ExecuteNonQuery();}
                transaction.Commit();
            }
        }

        void PersistAssignment(RemoteAssignment assignment, Guid localEventId)
        {
            using (var connection = database.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id FROM ConfigurationVersions WHERE EventId=$event ORDER BY VersionNumber DESC LIMIT 1"; command.Parameters.AddWithValue("$event", localEventId.ToString()); var configId = command.ExecuteScalar() as string; if (string.IsNullOrWhiteSpace(configId)) return;
                command.Parameters.Clear(); command.CommandText = @"UPDATE ConfigurationDeployments SET ConfigurationVersionId=$config,State='Ready',ReadyAtUtc=COALESCE(ReadyAtUtc,$now),NextRetryAtUtc=NULL,LastErrorCategory=NULL,LastError=NULL,UpdatedAtUtc=$now WHERE RemoteAssignmentId=$remote"; command.Parameters.AddWithValue("$config", configId); command.Parameters.AddWithValue("$remote", assignment.Id); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Không thể ghi nhận cấu hình đã sẵn sàng.");
            }
        }
        void BeginDeployment(RemoteAssignment assignment)
        {
            var now=DateTime.UtcNow.ToString("O");
            using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                using(var supersede=connection.CreateCommand()){supersede.Transaction=transaction;supersede.CommandText="UPDATE ConfigurationDeployments SET State='Superseded',SupersededAtUtc=$now,UpdatedAtUtc=$now WHERE RemoteEventId=$event AND RemoteAssignmentId<>$remote AND State IN ('Pending','Downloading','Ready','Failed')";supersede.Parameters.AddWithValue("$now",now);supersede.Parameters.AddWithValue("$event",assignment.EventId??string.Empty);supersede.Parameters.AddWithValue("$remote",assignment.Id??string.Empty);supersede.ExecuteNonQuery();}
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;command.CommandText=@"INSERT INTO ConfigurationDeployments(Id,RemoteAssignmentId,RemoteEventId,RemoteConfigurationId,State,ReceivedAtUtc,DownloadStartedAtUtc,UpdatedAtUtc)
VALUES($id,$remote,$event,$configuration,'Downloading',$now,$now,$now)
ON CONFLICT(RemoteAssignmentId) DO UPDATE SET RemoteConfigurationId=excluded.RemoteConfigurationId,State='Downloading',DownloadStartedAtUtc=COALESCE(ConfigurationDeployments.DownloadStartedAtUtc,excluded.DownloadStartedAtUtc),NextRetryAtUtc=NULL,LastErrorCategory=NULL,LastError=NULL,UpdatedAtUtc=excluded.UpdatedAtUtc";
                    command.Parameters.AddWithValue("$id","remote-"+assignment.Id);command.Parameters.AddWithValue("$remote",assignment.Id);command.Parameters.AddWithValue("$event",assignment.EventId??string.Empty);command.Parameters.AddWithValue("$configuration",(object)assignment.ConfigurationId??DBNull.Value);command.Parameters.AddWithValue("$now",now);command.ExecuteNonQuery();
                }
                foreach(var frame in assignment.Frames??new RemoteFrame[0])using(var asset=connection.CreateCommand())
                {
                    asset.Transaction=transaction;asset.CommandText=@"INSERT INTO ConfigurationDeploymentAssets(DeploymentId,AssetType,RemoteAssetId,ExpectedHashSha256,ExpectedBytes,State,UpdatedAtUtc)
VALUES($deployment,'Frame',$asset,$hash,$bytes,'Missing',$now)
ON CONFLICT(DeploymentId,AssetType,RemoteAssetId) DO UPDATE SET ExpectedHashSha256=excluded.ExpectedHashSha256,ExpectedBytes=excluded.ExpectedBytes,State=CASE WHEN ConfigurationDeploymentAssets.State='Verified' AND ConfigurationDeploymentAssets.ExpectedHashSha256=excluded.ExpectedHashSha256 THEN 'Verified' ELSE 'Missing' END,UpdatedAtUtc=excluded.UpdatedAtUtc";
                    asset.Parameters.AddWithValue("$deployment","remote-"+assignment.Id);asset.Parameters.AddWithValue("$asset",RemoteVersionIdentity(frame));asset.Parameters.AddWithValue("$hash",(object)frame.ContentSha256??DBNull.Value);asset.Parameters.AddWithValue("$bytes",frame.AssetBytes);asset.Parameters.AddWithValue("$now",now);asset.ExecuteNonQuery();
                }
                var colorPreset=assignment.Payload?.ColorPreset;
                if(colorPreset!=null)using(var asset=connection.CreateCommand())
                {
                    asset.Transaction=transaction;asset.CommandText=@"INSERT INTO ConfigurationDeploymentAssets(DeploymentId,AssetType,RemoteAssetId,ExpectedHashSha256,ExpectedBytes,State,UpdatedAtUtc)
VALUES($deployment,'Lut',$asset,$hash,$bytes,'Missing',$now)
ON CONFLICT(DeploymentId,AssetType,RemoteAssetId) DO UPDATE SET ExpectedHashSha256=excluded.ExpectedHashSha256,ExpectedBytes=excluded.ExpectedBytes,State=CASE WHEN ConfigurationDeploymentAssets.State='Verified' AND ConfigurationDeploymentAssets.ExpectedHashSha256=excluded.ExpectedHashSha256 THEN 'Verified' ELSE 'Missing' END,UpdatedAtUtc=excluded.UpdatedAtUtc";
                    asset.Parameters.AddWithValue("$deployment","remote-"+assignment.Id);asset.Parameters.AddWithValue("$asset",colorPreset.PresetVersionId??string.Empty);asset.Parameters.AddWithValue("$hash",(object)colorPreset.ContentSha256??DBNull.Value);asset.Parameters.AddWithValue("$bytes",colorPreset.AssetBytes);asset.Parameters.AddWithValue("$now",now);asset.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
        void SetDeploymentAssetState(string remoteAssignmentId,RemoteFrame frame,string state,string errorCategory,string error)
        {
            try{using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="UPDATE ConfigurationDeploymentAssets SET State=$state,LocalPath=CASE WHEN $state='Verified' THEN COALESCE(LocalPath,$path) ELSE LocalPath END,LastErrorCategory=$category,LastError=$error,UpdatedAtUtc=$now WHERE DeploymentId=(SELECT Id FROM ConfigurationDeployments WHERE RemoteAssignmentId=$remote) AND AssetType='Frame' AND RemoteAssetId=$asset";command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$path",DBNull.Value);command.Parameters.AddWithValue("$category",(object)errorCategory??DBNull.Value);command.Parameters.AddWithValue("$error",(object)error??DBNull.Value);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$remote",remoteAssignmentId??string.Empty);command.Parameters.AddWithValue("$asset",RemoteVersionIdentity(frame));command.ExecuteNonQuery();}}
            catch(Exception exception){logger?.LogDebug(exception,"Could not update deployment asset state for {AssignmentId}.",remoteAssignmentId);}
        }
        void SetDeploymentColorPresetState(string remoteAssignmentId,RemoteColorPreset preset,string state,string errorCategory,string error)
        {
            try{using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="UPDATE ConfigurationDeploymentAssets SET State=$state,LastErrorCategory=$category,LastError=$error,UpdatedAtUtc=$now WHERE DeploymentId=(SELECT Id FROM ConfigurationDeployments WHERE RemoteAssignmentId=$remote) AND AssetType='Lut' AND RemoteAssetId=$asset";command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$category",(object)errorCategory??DBNull.Value);command.Parameters.AddWithValue("$error",(object)error??DBNull.Value);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$remote",remoteAssignmentId??string.Empty);command.Parameters.AddWithValue("$asset",preset?.PresetVersionId??string.Empty);command.ExecuteNonQuery();}}
            catch(Exception exception){logger?.LogDebug(exception,"Could not update color preset deployment state for {AssignmentId}.",remoteAssignmentId);}
        }
        void PersistArchiveAssignment(RemoteAssignment assignment,string state)
        {
            try
            {
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE ConfigurationDeployments SET ConfigurationVersionId=(SELECT Id FROM ConfigurationVersions WHERE EventId=(SELECT Id FROM Events WHERE RemoteEventId=$event LIMIT 1) ORDER BY VersionNumber DESC LIMIT 1),State=$state,ReadyAtUtc=CASE WHEN $state IN ('Ready','Applied') THEN COALESCE(ReadyAtUtc,$now) ELSE ReadyAtUtc END,AppliedAtUtc=CASE WHEN $state='Applied' THEN COALESCE(AppliedAtUtc,$now) ELSE AppliedAtUtc END,UpdatedAtUtc=$now WHERE RemoteAssignmentId=$remote";
                    command.Parameters.AddWithValue("$remote", assignment.Id);
                    command.Parameters.AddWithValue("$event", assignment.EventId ?? string.Empty);
                    command.Parameters.AddWithValue("$state",state);
                    command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception exception) { logger?.LogDebug(exception, "Could not persist archive assignment {AssignmentId}.", assignment.Id); }
        }
        string LocalAssignmentState(string remoteId)
        {
            if (string.IsNullOrWhiteSpace(remoteId)) return null;
            try
            {
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT State FROM ConfigurationDeployments WHERE RemoteAssignmentId=$remote LIMIT 1";
                    command.Parameters.AddWithValue("$remote", remoteId);
                    return command.ExecuteScalar() as string;
                }
            }
            catch { return null; }
        }
        bool IsLocalAssignmentSelected(string remoteId)
        {
            try{using(var connection=database.OpenConnection())using(var command=connection.CreateCommand()){command.CommandText="SELECT COUNT(*) FROM ConfigurationDeployments d JOIN DeviceRuntimeState r ON r.SelectedConfigurationVersionId=d.ConfigurationVersionId WHERE d.RemoteAssignmentId=$remote AND d.State IN ('Ready','Applied')";command.Parameters.AddWithValue("$remote",remoteId??string.Empty);return Convert.ToInt32(command.ExecuteScalar())==1;}}catch{return false;}
        }
        void MarkLocalAssignmentApplied(string remoteId) => UpdateLocalAssignment(remoteId, "Applied", null,null, true);
        void MarkLocalAssignmentFailed(string remoteId,string category, string error) => UpdateLocalAssignment(remoteId, "Failed",category,error, false);
        void UpdateLocalAssignment(string remoteId, string state,string errorCategory, string error, bool applied)
        {
            try { using (var c = database.OpenConnection()) using (var q = c.CreateCommand()) { q.CommandText = "UPDATE ConfigurationDeployments SET State=$state,AttemptCount=AttemptCount+CASE WHEN $state='Failed' THEN 1 ELSE 0 END,NextRetryAtUtc=CASE WHEN $state='Failed' THEN $retry ELSE NULL END,AppliedAtUtc=CASE WHEN $applied=1 THEN COALESCE(AppliedAtUtc,$now) ELSE AppliedAtUtc END,LastErrorCategory=$category,LastError=$error,UpdatedAtUtc=$now WHERE RemoteAssignmentId=$remote"; q.Parameters.AddWithValue("$state", state); q.Parameters.AddWithValue("$applied", applied ? 1 : 0); q.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));q.Parameters.AddWithValue("$retry",state=="Failed"?(object)DateTime.UtcNow.AddSeconds(30).ToString("O"):DBNull.Value);q.Parameters.AddWithValue("$category", (object)errorCategory ?? DBNull.Value); q.Parameters.AddWithValue("$error", (object)error ?? DBNull.Value); q.Parameters.AddWithValue("$remote", remoteId); q.ExecuteNonQuery(); } } catch { }
        }

        static int Clamp(int value, int min, int max, int fallback) => value < min || value > max ? fallback : value;
        static int WaitingTimeout(int value)=>new[]{30,60,120,300,600,900}.Contains(value)?value:30;
        static EventPrintPolicyMode ParsePrintPolicyMode(string value)=>Enum.TryParse(value,true,out EventPrintPolicyMode parsed)?parsed:EventPrintPolicyMode.Manual;
        static string ConfigurationErrorCategory(Exception value)=>value is HttpRequestException?"network":value is OperationCanceledException?"timeout":value.Message.IndexOf("hash",StringComparison.OrdinalIgnoreCase)>=0||value.Message.IndexOf("toàn vẹn",StringComparison.OrdinalIgnoreCase)>=0?"checksum":value.Message.IndexOf("kích thước",StringComparison.OrdinalIgnoreCase)>=0||value.Message.IndexOf("ô ảnh",StringComparison.OrdinalIgnoreCase)>=0?"invalid_asset":"local_commit";
        static string SafeError(Exception value){var text=(value?.Message??"Unknown error").Replace('\r',' ').Replace('\n',' ').Trim();return text.Length<=500?text:text.Substring(0,500);}
        static string RemoteLifecycle(string value)=>string.Equals(value,"active",StringComparison.OrdinalIgnoreCase)||string.Equals(value,"open",StringComparison.OrdinalIgnoreCase)?PhotoEventLifecycleStates.Open:string.Equals(value,"scheduled",StringComparison.OrdinalIgnoreCase)?PhotoEventLifecycleStates.Scheduled:string.Equals(value,"closed",StringComparison.OrdinalIgnoreCase)?PhotoEventLifecycleStates.Closed:string.Equals(value,"delivered",StringComparison.OrdinalIgnoreCase)?PhotoEventLifecycleStates.Delivered:string.Equals(value,"archived",StringComparison.OrdinalIgnoreCase)?PhotoEventLifecycleStates.Archived:PhotoEventLifecycleStates.Draft;
        static string Safe(string value) => new string((value ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        static byte[] Hex(string value) { if (value == null || value.Length != 64) throw new InvalidOperationException("Hash frame không hợp lệ."); var bytes = new byte[32]; for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return bytes; }
        static string Json<T>(T value) { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
        static T Read<T>(string value) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value ?? string.Empty))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }

        [DataContract] sealed class RemoteEnvelope { [DataMember(Name = "assignment")] public RemoteAssignment Assignment { get; set; } }
        [DataContract] sealed class RemoteFrameCatalogEnvelope { [DataMember(Name = "frames")] public RemoteFrame[] Frames { get; set; } }
        [DataContract] sealed class RemoteAssignment { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "eventId")] public string EventId { get; set; } [DataMember(Name = "eventName")] public string EventName { get; set; } [DataMember(Name = "eventStatus")] public string EventStatus { get; set; } [DataMember(Name = "configurationId")] public string ConfigurationId { get; set; } [DataMember(Name="deploymentState")] public string DeploymentState { get; set; } [DataMember(Name = "operation")] public string Operation { get; set; } [DataMember(Name = "version")] public int Version { get; set; } [DataMember(Name="manifestJson")] public string ManifestJson { get; set; } [DataMember(Name="manifestSha256")] public string ManifestSha256 { get; set; } [DataMember(Name = "payload")] public RemotePayload Payload { get; set; } [DataMember(Name = "frames")] public RemoteFrame[] Frames { get; set; } }
        [DataContract] sealed class RemotePayload
        {
            [DataMember(Name = "basePriceMinor")] public long BasePriceMinor { get; set; }
            [DataMember(Name = "additionalPrintPriceMinor")] public long AdditionalPrintPriceMinor { get; set; }
            [DataMember(Name = "currency")] public string Currency { get; set; }
            [DataMember(Name = "photoCount")] public int PhotoCount { get; set; }
            [DataMember(Name = "countdownSeconds")] public int CountdownSeconds { get; set; }
            [DataMember(Name = "gifFrameDurationMs")] public int GifFrameDurationMilliseconds { get; set; }
            [DataMember(Name = "waitingTimeoutSeconds")] public int WaitingTimeoutSeconds { get; set; }
            [DataMember(Name = "customerLayoutMode")] public int CustomerLayoutMode { get; set; }
            [DataMember(Name = "imageRotationDegrees")] public int ImageRotationDegrees { get; set; }
            [DataMember(Name = "beauty")] public RemoteBeauty Beauty { get; set; }
            [DataMember(Name = "printPolicy")] public RemotePrintPolicy PrintPolicy { get; set; }
            [DataMember(Name = "colorPreset")] public RemoteColorPreset ColorPreset { get; set; }
            [DataMember(Name="frames")] public RemoteFrame[] Frames { get; set; }
        }
        [DataContract] sealed class RemotePrintPolicy { [DataMember(Name="mode")] public string Mode { get; set; } [DataMember(Name="includedCopies")] public int IncludedCopies { get; set; } [DataMember(Name="maxCopies")] public int MaximumCopies { get; set; } }
        [DataContract] sealed class RemoteColorPreset { [DataMember(Name="presetId")] public string PresetId { get; set; } [DataMember(Name="presetVersionId")] public string PresetVersionId { get; set; } [DataMember(Name="name")] public string Name { get; set; } [DataMember(Name="strengthPercent")] public int StrengthPercent { get; set; } [DataMember(Name="assetUrl")] public string AssetUrl { get; set; } [DataMember(Name="assetFormat")] public string AssetFormat { get; set; } [DataMember(Name="assetBytes")] public long AssetBytes { get; set; } [DataMember(Name="contentSha256")] public string ContentSha256 { get; set; } [DataMember(Name="cubeSize")] public int CubeSize { get; set; } }
        [DataContract] sealed class RemoteBeauty { [DataMember(Name="enabled")] public bool Enabled { get; set; } [DataMember(Name="smoothSkin")] public int SmoothSkin { get; set; } [DataMember(Name="brightenSkin")] public int BrightenSkin { get; set; } [DataMember(Name="skinTone")] public int SkinTone { get; set; } [DataMember(Name="sharpen")] public int Sharpen { get; set; } [DataMember(Name="eyeSize")] public int EyeSize { get; set; } [DataMember(Name="slimFace")] public int SlimFace { get; set; } [DataMember(Name="blemishRemoval")] public int BlemishRemoval { get; set; } [DataMember(Name="darkCircleReduction")] public int DarkCircleReduction { get; set; } }
        [DataContract] sealed class RemoteFrame { [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "frameVersionId")] public string FrameVersionId { get; set; } [DataMember(Name = "name")] public string Name { get; set; } [DataMember(Name = "version")] public int Version { get; set; } [DataMember(Name = "assetUrl")] public string AssetUrl { get; set; } [DataMember(Name = "assetFormat")] public string AssetFormat { get; set; } [DataMember(Name = "assetBytes")] public long AssetBytes { get; set; } [DataMember(Name = "contentSha256")] public string ContentSha256 { get; set; } [DataMember(Name = "pixelWidth")] public int PixelWidth { get; set; } [DataMember(Name = "pixelHeight")] public int PixelHeight { get; set; } [DataMember(Name = "slots")] public RemoteSlot[] Slots { get; set; } }
        [DataContract] sealed class RemoteSlot { [DataMember(Name = "index")] public int Index { get; set; } [DataMember(Name = "x")] public int X { get; set; } [DataMember(Name = "y")] public int Y { get; set; } [DataMember(Name = "width")] public int Width { get; set; } [DataMember(Name = "height")] public int Height { get; set; } }
        [DataContract] sealed class ApplyRequest { [DataMember(Name = "assignmentId")] public string AssignmentId { get; set; } [DataMember(Name = "status")] public string Status { get; set; } [DataMember(Name="errorCategory",EmitDefaultValue=false)] public string ErrorCategory { get; set; } [DataMember(Name = "error", EmitDefaultValue = false)] public string Error { get; set; } }
    }
}
