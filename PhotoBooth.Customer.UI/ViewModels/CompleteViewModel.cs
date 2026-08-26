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
        private readonly ISessionService sessions;
        private readonly ICaptureService captures;
        private readonly ILocalShareService localShare;
        private readonly IQrCodeService qr;
        private readonly IGifAnimationService gifAnimation;
        private readonly ICaptureIntegrityService integrity;
        private readonly ILogger<CompleteViewModel> log;
        private readonly DispatcherTimer countdownTimer;

        private string status = "Ảnh của bạn đã sẵn sàng!";
        private string downloadUrl;
        private byte[] qrSource;
        private bool generating;
        private bool completing;
        private string gifPath;
        private int remainingSeconds;

        public CompleteViewModel(
            CustomerWorkflowStateMachine machine,
            CustomerWorkflowContext context,
            ISessionService sessions,
            ICaptureService captures,
            ILocalShareService localShare,
            IQrCodeService qr,
            IGifAnimationService gifAnimation,
            ICaptureIntegrityService integrity,
            ILogger<CompleteViewModel> log)
        {
            this.machine = machine;
            this.context = context;
            this.sessions = sessions;
            this.captures = captures;
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
                    _ = GenerateShare();
                }
                else countdownTimer.Stop();
            };
        }

        public event EventHandler Completed;

        public string StatusText
        {
            get => status;
            private set => Set(ref status, value);
        }

        public string FinalImagePath => context.Session?.FinalImagePath;

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

        private async Task GenerateShare()
        {
            if (IsGenerating)
            {
                return;
            }

            IsGenerating = true;
            QrSource = null;
            DownloadUrl = null;

            try
            {
                var session = context.Session;
                var captureId = context.CaptureId;
                if (session == null || string.IsNullOrWhiteSpace(captureId))
                {
                    throw new InvalidOperationException("Capture information is incomplete.");
                }

                var name = session.Id.ToString("N") + "." + captureId;
                var output = Path.Combine(session.OutputDirectory, name + ".gif");
                var capture = await captures.GetAsync(session.Id, captureId, CancellationToken.None);
                if (capture == null)
                {
                    throw new InvalidOperationException("Capture was not found in SQLite.");
                }

                // The capture record is the source of truth for one booth turn.
                // Frame composition may use fewer slots, but the GIF must retain
                // every original photo captured in that turn, in position order.
                var motionAssets = (capture.Photos ?? new CapturePhoto[0])
                    .Where(photo => string.Equals(photo.PhotoType, CaptureAssetTypes.MotionPhoto, StringComparison.OrdinalIgnoreCase)||string.Equals(photo.PhotoType, CaptureAssetTypes.Picture, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(photo => photo.Position)
                    .ToList();
                var gifSources = motionAssets.Select(photo => photo.LocalPath).Where(File.Exists).ToList();
                if (gifSources.Count > 0)
                {
                    await gifAnimation.CreateAsync(gifSources, output, context.Settings?.GifFrameDurationMilliseconds ?? 1000, CancellationToken.None);
                    await captures.AddFileAsync(captureId, output, CaptureAssetTypes.Gif, motionAssets.Select(x=>x.Id).ToList(), CancellationToken.None);
                    GifPath = output;
                    log.LogInformation("GIF created with {FrameCount} original frames for capture {CaptureId}", gifSources.Count, captureId);
                }

                capture = await captures.GetAsync(session.Id, captureId, CancellationToken.None);
                if (capture == null)
                {
                    throw new InvalidOperationException("Capture was not found in SQLite.");
                }

                await integrity.ValidateAsync(capture, CancellationToken.None);

                var files = (capture.Photos ?? new CapturePhoto[0])
                    .Where(photo => !string.Equals(photo.PhotoType, CaptureAssetTypes.ShareArchive, StringComparison.OrdinalIgnoreCase))
                    .Select(photo => photo.LocalPath)
                    .Where(File.Exists)
                    .ToList();

                var ticket = await localShare.CreateAsync(
                    session.Id,
                    captureId,
                    files,
                    CancellationToken.None);

                var archiveAsset = await captures.AddFileAsync(captureId, ticket.ZipPath, CaptureAssetTypes.ShareArchive, (capture.Photos ?? new CapturePhoto[0]).Select(x=>x.Id).ToList(), CancellationToken.None);
                ticket.ArchiveAssetId = archiveAsset.Id;

                await captures.UpdateSharePathAsync(
                    captureId,
                    ticket.DownloadUrl.AbsoluteUri,
                    CancellationToken.None);

                DownloadUrl = ticket.DownloadUrl.AbsoluteUri;
                QrSource = await qr.GeneratePngAsync(
                    ticket.DownloadUrl,
                    360,
                    CancellationToken.None);
                StatusText = "Quét QR để tải file ZIP";
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Local Share QR generation failed");
                StatusText = "Không thể tạo liên kết tải qua Wi-Fi: " + exception.Message;
            }
            finally
            {
                IsGenerating = false;
            }
        }

        private async Task Done()
        {
            if (completing) return;
            completing = true;
            countdownTimer.Stop();
            try
            {
                var session = context.Session;
                if (session != null)
                {
                    await sessions.CompleteAsync(session, CancellationToken.None);
                    await Task.Run(() => SessionWorkspace.Cleanup(session));
                }
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Session completion failed");
            }

            context.Session = null;
            context.CaptureId = null;
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
