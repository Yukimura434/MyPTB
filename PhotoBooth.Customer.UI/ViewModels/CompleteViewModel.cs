using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;
using PhotoBooth.Customer.UI.Workflow;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class CompleteViewModel : ObservableObject
    {
        private readonly CustomerWorkflowStateMachine machine;
        private readonly CustomerWorkflowContext context;
        private readonly IBoothSessionService sessions;
        private readonly IEventService events;
        private readonly IDeliverableService deliverables;
        private readonly ILocalShareService localShare;
        private readonly IRemoteDeliveryQueue remoteDelivery;
        private readonly IEventExportQueue eventExport;
        private readonly IRevenueSyncService revenueSync;
        private readonly IQrCodeService qr;
        private readonly IGifAnimationService gifAnimation;
        private readonly IDeliverableIntegrityService integrity;
        private readonly ILogger<CompleteViewModel> log;
        private readonly DispatcherTimer countdownTimer;

        private string status = "Ảnh của bạn đã sẵn sàng!";
        private string downloadUrl;
        private byte[] qrSource;
        private bool generating;
        private bool completing;
        private string gifPath;
        private int remainingSeconds;
        private long shareGeneration;
        private Task shareTask = Task.CompletedTask;
        private Uri remoteGalleryUrl;

        public CompleteViewModel(
            CustomerWorkflowStateMachine machine,
            CustomerWorkflowContext context,
            IBoothSessionService sessions,
            IEventService events,
            IDeliverableService deliverables,
            ILocalShareService localShare,
            IRemoteDeliveryQueue remoteDelivery,
            IEventExportQueue eventExport,
            IRevenueSyncService revenueSync,
            IQrCodeService qr,
            IGifAnimationService gifAnimation,
            IDeliverableIntegrityService integrity,
            ILogger<CompleteViewModel> log)
        {
            this.machine = machine;
            this.context = context;
            this.sessions = sessions;
            this.events = events;
            this.deliverables = deliverables;
            this.localShare = localShare;
            this.remoteDelivery = remoteDelivery;
            this.eventExport = eventExport;
            this.revenueSync = revenueSync;
            this.qr = qr;
            this.gifAnimation = gifAnimation;
            this.integrity = integrity;
            this.log = log;
            remoteDelivery.GalleryReady += RemoteGalleryReady;
            remoteDelivery.DeliveryFailed += RemoteDeliveryFailed;

            DoneCommand = new AsyncCommand(Done);
            countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            countdownTimer.Tick += async (sender, args) =>
            {
                if (RemainingSeconds > 0) RemainingSeconds--;
                if (RemainingSeconds <= 0) await Done();
            };
            machine.StateChanged += (sender, args) =>
            {
                if (machine.State == CustomerWorkflowState.Complete)
                {
                    countdownTimer.Stop();
                    RemainingSeconds = 0;
                    StartShareGeneration();
                }
                else
                {
                    countdownTimer.Stop();
                    InvalidateShareUi();
                }
            };
        }

        public event EventHandler Completed;

        public string StatusText
        {
            get => status;
            private set => Set(ref status, value);
        }

        public string FinalImagePath => context.BoothSession?.FinalImagePath;

        public string DownloadUrl
        {
            get => downloadUrl;
            private set => Set(ref downloadUrl, value);
        }

        public byte[] QrSource
        {
            get => qrSource;
            private set { if (Set(ref qrSource, value)) Raise(nameof(HasQr)); }
        }

        public bool HasQr => QrSource != null;

        public bool IsGenerating
        {
            get => generating;
            private set => Set(ref generating, value);
        }

        public string GifPath { get => gifPath; private set => Set(ref gifPath, value); }
        public int RemainingSeconds { get => remainingSeconds; private set => Set(ref remainingSeconds, value); }

        public ICommand DoneCommand { get; }

        private void StartShareGeneration()
        {
            var generation = Interlocked.Increment(ref shareGeneration);
            var session = context.BoothSession;
            var deliverableId = context.DeliverableId;
            var gifFrameDuration = context.Settings?.GifFrameDurationMilliseconds ?? 1000;
            var createGif = context.Settings?.CreateGif != false && context.Authorization?.IsReusableSelfBooth != true && context.Authorization?.IsEvent != true;
            var shareMode = context.Authorization?.UsesAccessCodeSettings == true ? ShareDeliveryModes.CloudOnly : ShareDeliveryModes.Normalize(context.Settings?.ShareDeliveryMode);
            IsGenerating = true;
            QrSource = null;
            DownloadUrl = null;
            GifPath = null;
            remoteGalleryUrl = null;
            StatusText = "Đang chuẩn bị mã nhận ảnh…";
            shareTask = GenerateShare(generation, session, deliverableId, gifFrameDuration, createGif, shareMode);
        }

        private void InvalidateShareUi()
        {
            Interlocked.Increment(ref shareGeneration);
            IsGenerating = false;
        }

        private bool IsCurrentShare(long generation) =>
            generation == Interlocked.Read(ref shareGeneration) && machine.State == CustomerWorkflowState.Complete;

        private async Task GenerateShare(long generation, BoothSession session, string deliverableId, int gifFrameDuration, bool createGif, string shareMode)
        {
            Exception exportError = null;
            try
            {
                if (session == null || string.IsNullOrWhiteSpace(deliverableId))
                {
                    throw new InvalidOperationException("Booth session or deliverable information is incomplete.");
                }

                var output = Path.Combine(session.OutputDirectory, "Final", Guid.NewGuid().ToString("N") + ".gif");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var deliverable = await deliverables.GetAsync(session.Id, deliverableId, CancellationToken.None);
                if (deliverable == null)
                {
                    throw new InvalidOperationException("Không tìm thấy dữ liệu ảnh của lượt chụp.");
                }
                if (context.RecoveringDelivery)
                {
                    StatusText = "Đang tiếp tục tải ảnh của lượt chụp trước…";
                    await remoteDelivery.EnqueueAsync(deliverable, CancellationToken.None);
                    await remoteDelivery.RetryPendingAsync(deliverable.Id, CancellationToken.None);
                    return;
                }

                // The deliverable record is the source of truth for one booth turn.
                // Frame composition may use fewer slots, but the GIF must retain
                // every final per-shot Picture captured in that turn, in position
                // order. PicturePath already contains full Beauty followed by LUT,
                // so GIF generation must not retouch these frames a second time.
                var gifAssets = !createGif
                    ? new System.Collections.Generic.List<DeliverableAsset>()
                    : (deliverable.Assets ?? new DeliverableAsset[0])
                    .Where(asset => string.Equals(asset.Role, DeliverableAssetRoles.OriginalPicture, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(photo => photo.Position)
                    .ToList();
                var gifSources = gifAssets.Select(photo => photo.LocalPath).Where(File.Exists).ToList();
                if (gifSources.Count > 0)
                {
                    await gifAnimation.CreateAsync(gifSources, output, gifFrameDuration, CancellationToken.None);
                    await deliverables.AddAssetAsync(deliverableId, output, DeliverableAssetRoles.Gif, gifAssets.Select(x=>x.Id).ToList(), CancellationToken.None);
                    if (IsCurrentShare(generation)) GifPath = output;
                    log.LogInformation("GIF created with {FrameCount} original frames for deliverable {DeliverableId}", gifSources.Count, deliverableId);
                }

                deliverable = await deliverables.GetAsync(session.Id, deliverableId, CancellationToken.None);
                if (deliverable == null)
                {
                    throw new InvalidOperationException("Không tìm thấy dữ liệu ảnh của lượt chụp.");
                }

                await integrity.ValidateAsync(deliverable, CancellationToken.None);

                if (!string.Equals(shareMode, ShareDeliveryModes.LanOnly, StringComparison.Ordinal))
                {
                    var prepared = await remoteDelivery.PrepareGalleryAsync(session.Id, deliverableId, CancellationToken.None);
                    if (prepared.Succeeded && prepared.GalleryUrl != null)
                    {
                        remoteGalleryUrl = prepared.GalleryUrl;
                        await deliverables.UpdateSharePathAsync(deliverableId, prepared.GalleryUrl.AbsoluteUri, CancellationToken.None);
                        var source=await qr.GeneratePngAsync(prepared.GalleryUrl,360,CancellationToken.None);
                        if (IsCurrentShare(generation)) { DownloadUrl=prepared.GalleryUrl.AbsoluteUri; QrSource=source; StatusText="Quét mã để nhận ảnh. Ảnh đang được tải lên và sẽ tự cập nhật."; StartCountdown(); }
                    }
                    else if (IsCurrentShare(generation))
                        StatusText="Ảnh đã được lưu trên máy. Ứng dụng sẽ thử tải lại khi có kết nối.";
                }

                if (!string.Equals(shareMode, ShareDeliveryModes.LanOnly, StringComparison.Ordinal)) try
                {
                    await remoteDelivery.EnqueueAsync(deliverable, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    // Cloud delivery is best-effort and must never break the customer turn.
                    log.LogWarning(exception, "Could not enqueue remote delivery for {DeliverableId}; Local Share remains available.", deliverableId);
                }

                try
                {
                    await QueueEventExport(session, deliverable);
                }
                catch (Exception exception)
                {
                    exportError = exception;
                    log.LogError(exception, "Event media export failed for booth session {SessionId}", session.Id);
                }

                if (string.Equals(shareMode,ShareDeliveryModes.LanOnly,StringComparison.Ordinal) || (shareMode==ShareDeliveryModes.CloudWithLanFallback && remoteGalleryUrl==null && context.Authorization?.UsesAccessCodeSettings!=true))
                    await ShowLanShareAsync(generation,session,deliverable,exportError);
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Local Share QR generation failed");
                if (IsCurrentShare(generation)) StatusText = "Không thể tạo liên kết tải qua Wi-Fi: " + exception.Message;
            }
            finally
            {
                if (IsCurrentShare(generation)) IsGenerating = false;
            }
        }

        async Task ShowLanShareAsync(long generation,BoothSession session,Deliverable deliverable,Exception exportError=null)
        {
            var files=RemoteDeliveryAssetPolicy.Select(deliverable.Assets).Select(x=>x.LocalPath).Where(File.Exists).ToList();
            var ticket=await localShare.CreateAsync(session.Id,deliverable.Id,files,CancellationToken.None);
            await deliverables.UpdateSharePathAsync(deliverable.Id,ticket.DownloadUrl.AbsoluteUri,CancellationToken.None);
            var source=await qr.GeneratePngAsync(ticket.DownloadUrl,360,CancellationToken.None);
            if(IsCurrentShare(generation)){remoteGalleryUrl=null;DownloadUrl=ticket.DownloadUrl.AbsoluteUri;QrSource=source;StartCountdown();StatusText=exportError==null?"Quét QR để xem và tải ảnh trong mạng Wi-Fi":"Quét QR để tải ảnh trong mạng Wi-Fi · Chưa thể xếp hàng xuất ảnh sự kiện";}
        }

        async void RemoteDeliveryFailed(object sender,RemoteDeliveryFailedEventArgs args)
        {
            try
            {
                if(context.Authorization?.UsesAccessCodeSettings==true||args==null||!string.Equals(args.DeliverableId,context.DeliverableId,StringComparison.Ordinal)||ShareDeliveryModes.Normalize(context.Settings?.ShareDeliveryMode)!=ShareDeliveryModes.CloudWithLanFallback)return;
                var session=context.BoothSession;if(session==null)return;var deliverable=await deliverables.GetAsync(session.Id,args.DeliverableId,CancellationToken.None);if(deliverable==null)return;
                await ShowLanShareAsync(Interlocked.Read(ref shareGeneration),session,deliverable);
            }
            catch(Exception exception){log.LogWarning(exception,"Could not activate LAN fallback for {DeliverableId}.",args?.DeliverableId);}
        }

        private async void RemoteGalleryReady(object sender, RemoteGalleryReadyEventArgs args)
        {
            try
            {
                if (args?.GalleryUrl == null || !string.Equals(args.DeliverableId, context.DeliverableId, StringComparison.Ordinal)) return;
                var source = await qr.GeneratePngAsync(args.GalleryUrl, 360, CancellationToken.None);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!string.Equals(args.DeliverableId, context.DeliverableId, StringComparison.Ordinal) || machine.State != CustomerWorkflowState.Complete) return;
                    remoteGalleryUrl = args.GalleryUrl;
                    DownloadUrl = args.GalleryUrl.AbsoluteUri;
                    QrSource = source;
                    if (!countdownTimer.IsEnabled) StartCountdown();
                    StatusText = "Quét QR để nhận ảnh";
                });
            }
            catch (Exception exception) { log.LogWarning(exception, "Could not update the completed screen with the remote gallery."); }
        }

        private async Task QueueEventExport(BoothSession session, Deliverable deliverable)
        {
            if (session?.EventId == null || deliverable == null) return;
            var photoEvent = (await events.GetAllAsync(CancellationToken.None))
                .FirstOrDefault(value => value.Id == session.EventId.Value);
            if (photoEvent == null || string.IsNullOrWhiteSpace(photoEvent.OutputDirectory)) return;
            await eventExport.EnqueueAsync(photoEvent,session,deliverable,CancellationToken.None);
            log.LogInformation("Queued durable Event export for booth session {SessionId}.",session.Id);
        }

        private async Task Done()
        {
            if (completing) return;
            completing = true;
            countdownTimer.Stop();
            InvalidateShareUi();
            try
            {
                await shareTask; // Persist all delivery jobs before clearing the guest UI/session.
                var session = context.BoothSession;
                if (session != null)
                {
                    await sessions.CompleteAsync(session, CancellationToken.None);
                    try { await revenueSync.SynchronizeAsync(CancellationToken.None); }
                    catch (Exception exception) { log.LogWarning(exception, "Revenue report remains queued for synchronization."); }
                    await Task.Run(() => BoothSessionWorkspace.Cleanup(session));
                }
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Booth-session completion failed");
            }

            context.BoothSession = null;
            context.DeliverableId = null;
            context.WorkingDirectory = null;
            context.CurrentShots.Clear();
            context.SelectedFrame = null;
            QrSource = null;
            DownloadUrl = null;
            GifPath = null;
            Raise(nameof(FinalImagePath));
            machine.MoveTo(CustomerWorkflowState.Idle);
            completing = false;
            Completed?.Invoke(this, EventArgs.Empty);
        }

        private void StartCountdown()
        {
            RemainingSeconds = Math.Max(1, context.Settings?.WaitingTimeoutSeconds ?? 30);
            countdownTimer.Stop();
            countdownTimer.Start();
        }

    }
}
