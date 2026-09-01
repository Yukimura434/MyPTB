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

        public CompleteViewModel(
            CustomerWorkflowStateMachine machine,
            CustomerWorkflowContext context,
            IBoothSessionService sessions,
            IEventService events,
            IDeliverableService deliverables,
            ILocalShareService localShare,
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
            this.qr = qr;
            this.gifAnimation = gifAnimation;
            this.integrity = integrity;
            this.log = log;

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
                    StartCountdown();
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
            private set => Set(ref qrSource, value);
        }

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
            IsGenerating = true;
            QrSource = null;
            DownloadUrl = null;
            GifPath = null;
            StatusText = "Ảnh của bạn đã sẵn sàng!";
            shareTask = GenerateShare(generation, session, deliverableId, gifFrameDuration);
        }

        private void InvalidateShareUi()
        {
            Interlocked.Increment(ref shareGeneration);
            IsGenerating = false;
        }

        private bool IsCurrentShare(long generation) =>
            generation == Interlocked.Read(ref shareGeneration) && machine.State == CustomerWorkflowState.Complete;

        private async Task GenerateShare(long generation, BoothSession session, string deliverableId, int gifFrameDuration)
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
                    throw new InvalidOperationException("Deliverable was not found in SQLite.");
                }

                // The deliverable record is the source of truth for one booth turn.
                // Frame composition may use fewer slots, but the GIF must retain
                // every final per-shot Picture captured in that turn, in position
                // order. PicturePath already contains full Beauty followed by LUT,
                // so GIF generation must not retouch these frames a second time.
                var gifAssets = (deliverable.Assets ?? new DeliverableAsset[0])
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
                    throw new InvalidOperationException("Deliverable was not found in SQLite.");
                }

                await integrity.ValidateAsync(deliverable, CancellationToken.None);

                try
                {
                    await ExportEventMedia(session, deliverable);
                }
                catch (Exception exception)
                {
                    exportError = exception;
                    log.LogError(exception, "Event media export failed for booth session {SessionId}", session.Id);
                }

                var files = (deliverable.Assets ?? new DeliverableAsset[0])
                    .Where(asset => !string.Equals(asset.Role, DeliverableAssetRoles.ShareArchive, StringComparison.OrdinalIgnoreCase))
                    .Select(photo => photo.LocalPath)
                    .Where(File.Exists)
                    .ToList();

                var ticket = await localShare.CreateAsync(
                    session.Id,
                    deliverableId,
                    files,
                    CancellationToken.None);

                await deliverables.UpdateSharePathAsync(
                    deliverableId,
                    ticket.DownloadUrl.AbsoluteUri,
                    CancellationToken.None);

                var qrSource = await qr.GeneratePngAsync(
                    ticket.DownloadUrl,
                    360,
                    CancellationToken.None);
                if (IsCurrentShare(generation))
                {
                    DownloadUrl = ticket.DownloadUrl.AbsoluteUri;
                    QrSource = qrSource;
                    StatusText = exportError == null
                        ? "Quét QR để xem và tải ảnh"
                        : "Quét QR để tải ảnh · Không thể sao chép vào thư mục Event";
                }
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

        private async Task ExportEventMedia(BoothSession session, Deliverable deliverable)
        {
            if (session?.EventId == null || deliverable == null) return;
            var photoEvent = (await events.GetAllAsync(CancellationToken.None))
                .FirstOrDefault(value => value.Id == session.EventId.Value);
            if (photoEvent == null || string.IsNullOrWhiteSpace(photoEvent.OutputDirectory)) return;

            await Task.Run(() =>
            {
                var turnFolderName = string.IsNullOrWhiteSpace(session.DisplayCode)
                    ? session.Id.ToString("N")
                    : SanitizeFolderName(session.DisplayCode);
                var turnFolder = Path.Combine(Path.GetFullPath(photoEvent.OutputDirectory), turnFolderName);
                Directory.CreateDirectory(turnFolder);
                foreach (var asset in (deliverable.Assets ?? new DeliverableAsset[0])
                    .Where(value => !string.Equals(value.Role, DeliverableAssetRoles.ShareArchive, StringComparison.OrdinalIgnoreCase))
                    .Where(value => !string.IsNullOrWhiteSpace(value.LocalPath) && File.Exists(value.LocalPath)))
                {
                    var destination = Path.Combine(turnFolder, Path.GetFileName(asset.LocalPath));
                    CopyAtomically(asset.LocalPath, destination);
                }
                log.LogInformation("Exported event media for booth session {SessionId} to {OutputDirectory}", session.Id, turnFolder);
            });
        }

        private static string SanitizeFolderName(string value)
        {
            foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '-');
            return string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;
        }

        private static void CopyAtomically(string source, string destination)
        {
            var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, true);
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private async Task Done()
        {
            if (completing) return;
            completing = true;
            countdownTimer.Stop();
            InvalidateShareUi();
            try
            {
                var session = context.BoothSession;
                if (session != null)
                {
                    await sessions.CompleteAsync(session, CancellationToken.None);
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
