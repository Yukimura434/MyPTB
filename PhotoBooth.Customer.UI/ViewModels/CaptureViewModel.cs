using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Pipelines;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;
using PhotoBooth.Customer.UI.Workflow;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class CaptureViewModel : ObservableObject
    {
        readonly CustomerWorkflowStateMachine machine;
        readonly CustomerWorkflowContext context;
        readonly ICameraService cameras;
        readonly ILiveViewService live;
        readonly ISettingsService settings;
        readonly IEventService events;
        readonly IBoothSessionService sessions;
        readonly IPresetService presets;
        readonly ICapturePipeline capturePipeline;
        readonly IVideoService videos;
        readonly ILogger<CaptureViewModel> log;
        readonly ILiveBeautyPreviewPipeline liveBeauty;
        readonly IBeautySettingsService beautySettings;
        readonly ICaptureFocusService captureFocus;
        BeautySettings liveBeautySettings = new BeautySettings();
        bool liveBeautyFailed;
        int previewOutputFrames, previewBeautyFrames;
        long previewBeautyTicks, previewVideoTicks;
        CancellationTokenSource liveCts;
        Task liveLoopTask;
        string liveCameraId;
        CancellationTokenSource workflowCts;
        readonly object workflowSync = new object();
        Task activeWorkflowTask = Task.CompletedTask;
        readonly object processingSync = new object();
        CancellationTokenSource processingCts;
        Task processingTail = Task.CompletedTask;
        byte[] liveImage;
        readonly SemaphoreSlim cameraGate = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim liveGate = new SemaphoreSlim(1, 1);
        int countdown, currentPhoto, totalPhotos, delayRemaining;
        string message = "Waiting for camera…", error;
        bool cameraConnected;
        bool shutterFlash;
        bool manualModeSelected;
        double liveViewScaleX = 1d;
        double liveViewRotation;
        CapturedPhotoItem selectedReviewPhoto;
        MediaPlayer clockPlayer;
        MediaPlayer shutterPlayer;

        public CaptureViewModel(CustomerWorkflowStateMachine m, CustomerWorkflowContext ctx, ICameraService c,
            ILiveViewService l, ISettingsService st, IEventService eventService, IBoothSessionService ss, IPresetService ps,
            ICapturePipeline pipeline, IVideoService videoService, ILiveBeautyPreviewPipeline liveBeautyPreview, IBeautySettingsService beautySettingsService, ICaptureFocusService captureFocusService, ILogger<CaptureViewModel> logger)
        {
            machine = m; context = ctx; cameras = c; live = l; settings = st; events=eventService; sessions = ss;
            presets = ps; capturePipeline = pipeline; videos = videoService; liveBeauty=liveBeautyPreview; beautySettings=beautySettingsService; captureFocus=captureFocusService; log = logger;
            liveBeauty.FrameReady += OnLiveBeautyFrameReady;
            liveBeauty.Failed += OnLiveBeautyFailed;
            beautySettings.SettingsChanged += OnBeautySettingsChanged;
            AutomaticCaptureCommand = new AsyncCommand(() => RunTracked(StartAutomatic), () => IsModeSelection);
            ManualModeCommand = new AsyncCommand(() => RunTracked(PrepareManualMode), () => IsModeSelection);
            ManualShutterCommand = new AsyncCommand(() => RunTracked(CaptureManualShot), () => IsManualReady);
            StartCommand = AutomaticCaptureCommand;
            RetakeCommand = new AsyncCommand(() => RunTracked(Retake), () => machine.State == CustomerWorkflowState.Preview && ReviewPhotos.Any(x => x.IsRetakeSelected));
            SelectFrameCommand = new RelayCommand(() => machine.MoveTo(CustomerWorkflowState.FrameSelection));
            RetryCommand = new AsyncCommand(CheckCamera);
            CancelErrorCommand = new RelayCommand(() => { ErrorMessage = null; machine.RecoverToIdle(); });
            machine.StateChanged += (s, e) => OnStateChanged();
            cameras.CamerasChanged += OnCamerasChanged;
        }

        async void OnCamerasChanged(object sender, EventArgs e)
        {
            try { await RunOnUiAsync(RecoverCamera); }
            catch (Exception error) { log.LogError(error, "Camera change recovery failed"); }
        }

        static Task RunOnUiAsync(Func<Task> action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return action();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception error) { completion.TrySetException(error); }
            }));
            return completion.Task;
        }

        void OnBeautySettingsChanged(object sender, BeautySettingsChangedEventArgs args)
        {
            liveBeautySettings = args.Settings.Clone();
            liveBeautyFailed = false;
            liveBeauty.UpdateSettings(liveBeautySettings);
        }

        void OnLiveBeautyFailed(object sender, LiveBeautyPreviewErrorEventArgs args)
        {
            if (liveBeautyFailed) return;
            liveBeautyFailed = true;
            log.LogWarning(args.Error, "Live Beauty failed; raw Live View and video frames remain active");
        }

        void OnLiveBeautyFrameReady(object sender, LiveBeautyPreviewFrameEventArgs args)
        {
            var frame = args?.Frame;
            var cts = liveCts;
            if (frame?.ImageData == null || cts == null || cts.IsCancellationRequested) return;
            Interlocked.Increment(ref previewOutputFrames);
            if (args.BeautyApplied)
            {
                Interlocked.Increment(ref previewBeautyFrames);
                Interlocked.Add(ref previewBeautyTicks, (long)(args.ProcessingMilliseconds * Stopwatch.Frequency / 1000d));
            }
            var started = Stopwatch.GetTimestamp();
            videos.AddLiveViewFrame(frame.ImageData, frame.TimestampUtc == default(DateTime) ? DateTime.UtcNow : frame.TimestampUtc);
            Interlocked.Add(ref previewVideoTicks, Stopwatch.GetTimestamp() - started);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            Action publish = () => { if (liveCts != null) LiveImage = frame.ImageData; };
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(publish);
            else publish();
        }

        public byte[] LiveImage { get => liveImage; private set { if (Set(ref liveImage, value)) Raise(nameof(HasLiveImage)); } }
        public bool HasLiveImage => LiveImage != null && LiveImage.Length > 0;
        public int LiveFrameWidth { get; private set; }
        public int LiveFrameHeight { get; private set; }
        public ObservableCollection<string> CapturedImages { get; } = new ObservableCollection<string>();
        public ObservableCollection<CapturedPhotoItem> ReviewPhotos { get; } = new ObservableCollection<CapturedPhotoItem>();
        public CapturedPhotoItem SelectedReviewPhoto { get => selectedReviewPhoto; set => Set(ref selectedReviewPhoto, value); }
        public int CountdownNumber { get => countdown; private set => Set(ref countdown, value); }
        public int DelayRemaining { get => delayRemaining; private set => Set(ref delayRemaining, value); }
        public int CurrentPhoto { get => currentPhoto; private set { Set(ref currentPhoto, value); Raise(nameof(ProgressText)); } }
        public int TotalPhotos { get => totalPhotos; private set { Set(ref totalPhotos, value); Raise(nameof(ProgressText)); } }
        public string ProgressText => TotalPhotos > 0 ? "Photo " + Math.Max(1, CurrentPhoto) + " / " + TotalPhotos : "Ready when you are";
        public string StatusMessage { get => message; private set => Set(ref message, value); }
        public string ErrorMessage { get => error; private set { Set(ref error, value); Raise(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool IsIdle => machine.State == CustomerWorkflowState.Idle;
        public bool IsModeSelection => IsIdle && !manualModeSelected;
        public bool IsManualReady => IsIdle && manualModeSelected && context.CurrentShots.Count + context.PendingCaptures.Count < TotalPhotos;
        public bool IsCountdown => machine.State == CustomerWorkflowState.Countdown;
        public bool IsSmile => machine.State == CustomerWorkflowState.Smile;
        public bool IsCapturing => machine.State == CustomerWorkflowState.Capturing;
        public bool IsInterShotDelay => machine.State == CustomerWorkflowState.InterShotDelay;
        public bool IsPreview => machine.State == CustomerWorkflowState.Preview;
        public bool IsPrinting => machine.State == CustomerWorkflowState.Printing;
        public bool IsShutterFlash { get => shutterFlash; private set => Set(ref shutterFlash, value); }
        public bool IsBusy => IsCountdown || IsSmile || IsCapturing || IsInterShotDelay || IsPrinting;
        public bool CameraConnected { get => cameraConnected; private set => Set(ref cameraConnected, value); }
        public double LiveViewScaleX { get => liveViewScaleX; private set => Set(ref liveViewScaleX, value); }
        public double LiveViewRotation { get => liveViewRotation; private set { if (Set(ref liveViewRotation, value)) { Raise(nameof(IsQuarterTurn)); Raise(nameof(RecentThumbnailHeight)); } } }
        public bool IsQuarterTurn => LiveViewRotation == 90d || LiveViewRotation == -90d;
        public double RecentThumbnailHeight => IsQuarterTurn ? 312d : 172d;
        public ICommand StartCommand { get; }
        public ICommand AutomaticCaptureCommand { get; }
        public ICommand ManualModeCommand { get; }
        public ICommand ManualShutterCommand { get; }
        public ICommand RetakeCommand { get; }
        public ICommand SelectFrameCommand { get; }
        public ICommand RetryCommand { get; }
        public ICommand CancelErrorCommand { get; }

        async Task CheckCamera()
        {
            await cameraGate.WaitAsync();
            try
            {
                ErrorMessage = null; StatusMessage = "Looking for camera…";
                var list = await cameras.GetCamerasAsync(CancellationToken.None);
                context.Camera = list.FirstOrDefault(x => x.IsConnected);
                CameraConnected = context.Camera != null;
                if (!CameraConnected) { StatusMessage = "Waiting for camera…"; return; }
                context.CameraDisconnectedPending = false;
                StatusMessage = "Ready";
                await StartLive();
            }
            catch (Exception e) { Fail(e, "Waiting for camera…", false); }
            finally { cameraGate.Release(); }
        }

        async Task RecoverCamera()
        {
            await cameraGate.WaitAsync();
            try
            {
                var list = await cameras.GetCamerasAsync(CancellationToken.None);
                var connected = list.FirstOrDefault(x => x.IsConnected);
                if (connected == null)
                {
                    CameraConnected = false;
                    context.CameraDisconnectedPending = true;
                    await StopLive();
                    var state = machine.State;
                    if (state != CustomerWorkflowState.FrameSelection && state != CustomerWorkflowState.Printing && state != CustomerWorkflowState.Complete)
                    {
                        workflowCts?.Cancel();
                        if (manualModeSelected && state == CustomerWorkflowState.Idle) { manualModeSelected = false; await CleanupTemporary(); RaiseCaptureMode(); }
                        machine.RecoverToIdle();
                        LiveImage = null;
                        StatusMessage = "Waiting for camera…";
                    }
                    return;
                }

                context.Camera = connected;
                context.CameraDisconnectedPending = false;
                if (!CameraConnected)
                {
                    CameraConnected = true;
                    if (machine.State == CustomerWorkflowState.Idle) await StartLive();
                    StatusMessage = "Ready";
                }
            }
            catch (Exception e) { Fail(e, "Waiting for camera…", false); }
            finally { cameraGate.Release(); }
        }

        async Task StartAutomatic()
        {
            if (!CameraConnected) { ErrorMessage = "Camera disconnected"; return; }
            var cameraId = context.Camera?.Id;
            if (string.IsNullOrEmpty(cameraId)) { ErrorMessage = "Camera disconnected"; return; }
            PlayClock();
            workflowCts?.Cancel(); workflowCts?.Dispose(); workflowCts = new CancellationTokenSource();
            var token = workflowCts.Token;
            try
            {
                ErrorMessage = null;
                manualModeSelected = false;
                await PrepareCaptureSession(token);
                for (var i = 1; i <= TotalPhotos; i++)
                {
                    token.ThrowIfCancellationRequested();
                    CurrentPhoto = i;
                    await RunCountdownAndSmileAsync(cameraId, context.Settings.CountdownSeconds, token, i == 1);
                    log.LogInformation("Physical capture and shutter effect starting {Current}/{Total}", i, TotalPhotos);
                    var pending = await CapturePendingWithShutterAsync(context.BoothSession.Id, cameraId, null, token);
                    log.LogInformation("Physical capture completed {Current}/{Total}", i, TotalPhotos);
                    context.PendingCaptures.Add(pending);
                    QueuePendingProcessing(pending);
                    CapturedImages.Clear();
                    foreach (var shot in context.PendingCaptures) CapturedImages.Add(shot.RawPicturePath);
                    log.LogInformation("Capture finished {Current}/{Total}", i, TotalPhotos);

                    if (i < TotalPhotos)
                    {
                        machine.MoveTo(CustomerWorkflowState.InterShotDelay);
                        DelayRemaining = 0; StatusMessage = "Get ready for the next photo…";
                        machine.MoveTo(CustomerWorkflowState.Countdown);
                    }
                }

                await FinalizePendingCapturesAsync(token);
                machine.MoveTo(CustomerWorkflowState.Preview);
                RefreshReviewPhotos();
                StatusMessage = "Looking wonderful!";
            }
            catch (OperationCanceledException)
            {
                await CleanupTemporary();
                machine.RecoverToIdle();
                StatusMessage = "Waiting for camera…";
            }
            catch (Exception e)
            {
                Fail(e, "Capture failed", true);
                await CleanupTemporary();
                machine.RecoverToIdle();
            }
            finally { ReleaseAllAudio(); IsShutterFlash = false; }
        }

        async Task PrepareManualMode()
        {
            if (!CameraConnected || string.IsNullOrEmpty(context.Camera?.Id)) { ErrorMessage = "Camera disconnected"; return; }
            workflowCts?.Cancel(); workflowCts?.Dispose(); workflowCts = new CancellationTokenSource();
            try
            {
                ErrorMessage = null;
                await PrepareCaptureSession(workflowCts.Token);
                manualModeSelected = true;
                CurrentPhoto = 0;
                StatusMessage = "Nhấn Space hoặc nút Chụp để bắt đầu";
                RaiseCaptureMode();
            }
            catch (OperationCanceledException) { manualModeSelected = false; await CleanupTemporary(); machine.RecoverToIdle(); }
            catch (Exception e) { manualModeSelected = false; Fail(e, "Không thể bắt đầu chụp thủ công", true); await CleanupTemporary(); machine.RecoverToIdle(); }
        }

        async Task CaptureManualShot()
        {
            if (!IsManualReady) return;
            var cameraId = context.Camera?.Id;
            if (string.IsNullOrWhiteSpace(cameraId)) { ErrorMessage = "Camera disconnected"; return; }
            var token = workflowCts?.Token ?? CancellationToken.None;
            try
            {
                CurrentPhoto = context.CurrentShots.Count + context.PendingCaptures.Count + 1;
                await RunCountdownAndSmileAsync(cameraId, 3, token);
                log.LogInformation("Manual physical capture and shutter effect starting {Current}/{Total}", CurrentPhoto, TotalPhotos);
                var pending = await CapturePendingWithShutterAsync(context.BoothSession.Id, cameraId, 3, token);
                context.PendingCaptures.Add(pending);
                QueuePendingProcessing(pending);
                CapturedImages.Clear();
                foreach (var shot in context.PendingCaptures) CapturedImages.Add(shot.RawPicturePath);

                if (context.CurrentShots.Count + context.PendingCaptures.Count >= TotalPhotos)
                {
                    manualModeSelected = false;
                    await FinalizePendingCapturesAsync(token);
                    machine.MoveTo(CustomerWorkflowState.Preview);
                    RefreshReviewPhotos();
                    StatusMessage = "Looking wonderful!";
                }
                else
                {
                    machine.RecoverToIdle();
                    StatusMessage = "Nhấn Space hoặc nút Chụp cho ảnh tiếp theo";
                }
            }
            catch (OperationCanceledException) { manualModeSelected = false; await CleanupTemporary(); machine.RecoverToIdle(); StatusMessage = "Waiting for camera…"; }
            catch (Exception e) { manualModeSelected = false; Fail(e, "Capture failed", true); await CleanupTemporary(); machine.RecoverToIdle(); }
            finally { ReleaseAllAudio(); IsShutterFlash = false; RaiseCaptureMode(); }
        }

        async Task PrepareCaptureSession(CancellationToken token)
        {
            context.Settings = await settings.GetAsync(token) ?? new Settings();
            var allPresets = await presets.GetAllAsync(token);
            context.DefaultPreset = context.Settings.DefaultPresetId.HasValue
                ? allPresets.FirstOrDefault(x => x.Id == context.Settings.DefaultPresetId)
                : allPresets.FirstOrDefault(x => x.IsDefault);
            var selectedEvent = await events.GetDefaultAsync(token);
            context.BoothSession = await sessions.StartAsync(selectedEvent.Id, context.DefaultPreset?.Id, token);
            context.DeliverableId = null;
            context.CurrentShots.Clear();
            context.PendingCaptures.Clear();
            await Task.Run(() => BoothSessionWorkspace.Prepare(context.BoothSession), token);
            context.WorkingDirectory = BoothSessionWorkspace.GetPath(context.BoothSession);
            BoothSessionWorkspace.ReplaceWorkspaceFiles(context.BoothSession, new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            await sessions.UpdateAsync(context.BoothSession, token);
            CapturedImages.Clear();
            TotalPhotos = Math.Max(1, Math.Min(8, context.Settings.PhotoCount));
            StartPendingProcessing(token);
        }

        async Task Retake()
        {
            var selected = ReviewPhotos.Where(x => x.IsRetakeSelected).Select(x => x.Position).OrderBy(x => x).ToList();
            if (selected.Count == 0) return;
            var cameraId = context.Camera?.Id;
            if (string.IsNullOrWhiteSpace(cameraId)) { ErrorMessage = "Camera disconnected"; return; }
            workflowCts?.Cancel(); workflowCts?.Dispose(); workflowCts = new CancellationTokenSource();
            var token = workflowCts.Token;
            try
            {
                ErrorMessage = null; TotalPhotos = selected.Count;
                StartPendingProcessing(token);
                for (var sequence = 0; sequence < selected.Count; sequence++)
                {
                    var position = selected[sequence]; CurrentPhoto = sequence + 1;
                    await RunCountdownAndSmileAsync(cameraId, context.Settings?.CountdownSeconds ?? 3, token);
                    log.LogInformation("Physical retake and shutter effect starting {Current}/{Total}", sequence + 1, selected.Count);
                    var pending=await CapturePendingWithShutterAsync(context.BoothSession.Id,cameraId,null,token);
                    log.LogInformation("Physical retake completed {Current}/{Total}", sequence + 1, selected.Count);
                    context.PendingCaptures.Add(pending);
                    QueuePendingProcessing(pending);
                    if (sequence < selected.Count - 1) machine.MoveTo(CustomerWorkflowState.InterShotDelay);
                }
                StatusMessage="Processing retakes…";
                var replacements=await CompletePendingProcessingAsync(token);
                context.PendingCaptures.Clear();
                context.BoothSession=await sessions.GetAsync(context.BoothSession.Id,token);
                await ReplaceCapturesAsync(selected,replacements);
                machine.MoveTo(CustomerWorkflowState.Preview); RefreshReviewPhotos(); StatusMessage = "Retake complete";
            }
            catch (OperationCanceledException) { await CleanupTemporary(); machine.RecoverToIdle(); }
            catch (Exception e) { Fail(e, "Retake failed", true); await CleanupTemporary(); if (machine.State != CustomerWorkflowState.Preview) machine.RecoverToIdle(); }
            finally { ReleaseAllAudio(); IsShutterFlash = false; }
        }
        public async Task ResetToStartAsync()
        {
            workflowCts?.Cancel();
            ReleaseAllAudio();
            Task workflow;
            lock (workflowSync) workflow = activeWorkflowTask;
            await AwaitCompletion(workflow, CancellationToken.None);
            if(context.BoothSession!=null)await CleanupTemporary();
            manualModeSelected=false;ReviewPhotos.Clear();SelectedReviewPhoto=null;CurrentPhoto=0;TotalPhotos=0;CountdownNumber=0;DelayRemaining=0;
            StatusMessage=CameraConnected?"Ready":"Waiting for camera…";machine.RecoverToIdle();
            RaiseCaptureMode();
        }
        public async Task ActivateAsync(){var configured=await settings.GetAsync(CancellationToken.None);liveBeautySettings=await beautySettings.GetAsync(CancellationToken.None)??new BeautySettings();liveBeautyFailed=false;liveBeauty.Reset();liveBeauty.UpdateSettings(liveBeautySettings);LiveViewScaleX=configured?.AutoFlip==true?-1d:1d;LiveViewRotation=configured?.ImageRotationDegrees??0;await CheckCamera();}
        public Task ShutdownAsync() => ShutdownAsync(CancellationToken.None);
        public async Task ShutdownAsync(CancellationToken token)
        {
            workflowCts?.Cancel();
            ReleaseAllAudio();

            Task workflow;
            lock (workflowSync) workflow = activeWorkflowTask;
            await AwaitCompletion(workflow, token);

            // The tracked workflow normally owns cleanup. This remains necessary
            // when Customer mode closes from Preview or another non-capture state.
            if (context.BoothSession != null) await CleanupTemporary();
            await StopLive(token);
        }

        async Task RunTracked(Func<Task> action)
        {
            var task = action();
            lock (workflowSync) activeWorkflowTask = task;
            try { await task; }
            finally
            {
                lock (workflowSync)
                    if (ReferenceEquals(activeWorkflowTask, task)) activeWorkflowTask = Task.CompletedTask;
            }
        }

        static async Task AwaitCompletion(Task task, CancellationToken token)
        {
            if (task == null || task.IsCompleted) { if (task != null) await task; return; }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(task, cancelled.Task);
                if (completed != task) token.ThrowIfCancellationRequested();
                await task;
            }
        }
        async Task RunCountdownAndSmileAsync(string cameraId, int seconds, CancellationToken token, bool clockAlreadyPlaying = false)
        {
            machine.MoveTo(CustomerWorkflowState.Countdown);
            if (!clockAlreadyPlaying) PlayClock();
            try { for (var value = Math.Max(1, seconds); value >= 1; value--) { CountdownNumber = value; await Task.Delay(1000, token); } }
            finally { ReleaseClock(); }
            machine.MoveTo(CustomerWorkflowState.Smile); StatusMessage = "Smile!";
            var minimumSmileTime=Task.Delay(500,token);
            await captureFocus.TryFocusAsync(cameraId,token);
            await minimumSmileTime;
            ReleaseAllAudio();
        }
        async Task<PendingCapture> CapturePendingWithShutterAsync(Guid boothSessionId, string cameraId, int? videoDurationSeconds, CancellationToken token)
        {
            // Start the camera path first. Sound and overlay are deliberately kept
            // outside that critical path and run concurrently with capture/transfer.
            var captureTask = videoDurationSeconds.HasValue
                ? capturePipeline.CapturePendingAsync(boothSessionId, cameraId, context.WorkingDirectory, true, videoDurationSeconds.Value, token)
                : capturePipeline.CapturePendingAsync(boothSessionId, cameraId, context.WorkingDirectory, true, context.Settings?.CountdownSeconds ?? 3, token);
            var shutterTask = ShowShutterAsync(token);
            try { return await captureTask; }
            finally
            {
                try { await shutterTask; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        async Task ShowShutterAsync(CancellationToken token)
        {
            machine.MoveTo(CustomerWorkflowState.Capturing);
            PlayShutter(); IsShutterFlash = true;
            try { await Task.Delay(300, token); }
            finally { IsShutterFlash = false; ReleaseShutter(); }
        }
        void PlayClock() { ReleaseClock(); clockPlayer = CreateAndPlay("clock.mp3"); }
        void PlayShutter() { ReleaseShutter(); shutterPlayer = CreateAndPlay("shutter.mp3"); }
        MediaPlayer CreateAndPlay(string file)
        {
            MediaPlayer player = null;
            try { var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds", file); if (!System.IO.File.Exists(path)) { log.LogWarning("Sound file not found: {Path}", path); return null; } player = new MediaPlayer(); player.Open(new Uri(path, UriKind.Absolute)); player.Play(); return player; }
            catch (Exception e) { log.LogWarning(e, "Unable to play sound {Sound}", file); ReleasePlayer(ref player); return null; }
        }
        void ReleaseClock() { ReleasePlayer(ref clockPlayer); }
        void ReleaseShutter() { ReleasePlayer(ref shutterPlayer); }
        void ReleaseAllAudio() { ReleaseClock(); ReleaseShutter(); }
        static void ReleasePlayer(ref MediaPlayer player)
        {
            var current = player; player = null; if (current == null) return;
            try { current.Stop(); } catch { }
            try { current.Close(); } catch { }
        }
        async Task StartLive()
        {
            await liveGate.WaitAsync();
            try
            {
                await StopLiveCore();
                var camera = context.Camera;
                if (camera == null) return;
                var cts = new CancellationTokenSource();
                try
                {
                    await live.StartAsync(camera.Id, cts.Token);
                    liveCts = cts;
                    liveCameraId = camera.Id;
                    liveLoopTask = LiveLoop(camera.Id, cts.Token);
                }
                catch
                {
                    cts.Cancel();
                    cts.Dispose();
                    throw;
                }
            }
            finally { liveGate.Release(); }
        }
        async Task LiveLoop(string cameraId, CancellationToken token)
        {
            var lastSignature = 0;
            var metrics = Stopwatch.StartNew();
            var requests = 0; var uniqueFrames = 0; var duplicateFrames = 0; var emptyFrames = 0;
            long fetchTicks = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var stageStarted = Stopwatch.GetTimestamp();
                    var frame = await live.GetFrameAsync(cameraId, token);
                    fetchTicks += Stopwatch.GetTimestamp() - stageStarted; requests++;
                    var submitted = false;
                    if (frame?.ImageData != null)
                    {
                        var signature=FrameSignature(frame.ImageData);
                        if(signature!=lastSignature)
                        {
                            lastSignature=signature;uniqueFrames++;
                            if(LiveFrameWidth!=frame.Width||LiveFrameHeight!=frame.Height){LiveFrameWidth=frame.Width;LiveFrameHeight=frame.Height;Raise(nameof(LiveFrameWidth));Raise(nameof(LiveFrameHeight));}
                            liveBeauty.Submit(frame,token);submitted=true;
                        }
                        else duplicateFrames++;
                    }
                    else emptyFrames++;
                    if(metrics.ElapsedMilliseconds>=10000)
                    {
                        var seconds=Math.Max(.001,metrics.Elapsed.TotalSeconds);
                        var outputFrames=Interlocked.Exchange(ref previewOutputFrames,0);var beautyFrames=Interlocked.Exchange(ref previewBeautyFrames,0);var beautyTicks=Interlocked.Exchange(ref previewBeautyTicks,0);var videoTicks=Interlocked.Exchange(ref previewVideoTicks,0);
                        using(var process=Process.GetCurrentProcess())log.LogInformation("Customer Live View metrics {Seconds:F1}s: requests {RequestFps:F1} fps, unique/acquired {AcquiredFps:F1} fps, preview-output {OutputFps:F1} fps, fetch avg {FetchMs:F2} ms, beauty avg {BeautyMs:F2} ms ({BeautyFrames} frames), video-buffer avg {VideoMs:F3} ms, duplicates {Duplicates}, empty {Empty}, managed {ManagedMb:F1} MB, private {PrivateMb:F1} MB",seconds,requests/seconds,uniqueFrames/seconds,outputFrames/seconds,AverageMilliseconds(fetchTicks,requests),AverageMilliseconds(beautyTicks,beautyFrames),beautyFrames,AverageMilliseconds(videoTicks,outputFrames),duplicateFrames,emptyFrames,GC.GetTotalMemory(false)/1048576d,process.PrivateMemorySize64/1048576d);
                        metrics.Restart();requests=uniqueFrames=duplicateFrames=emptyFrames=0;fetchTicks=0;
                    }
                    if(!submitted)await Task.Delay(1,token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { log.LogWarning(e, "Live View unavailable; retrying"); try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; } }
            }
        }

        async Task FinalizePendingCapturesAsync(CancellationToken token)
        {
            StatusMessage="Processing photos…";
            var finalized=await CompletePendingProcessingAsync(token);
            context.PendingCaptures.Clear();
            context.BoothSession=await sessions.GetAsync(context.BoothSession.Id,token);
            context.CurrentShots.AddRange(finalized);
            CapturedImages.Clear();
            foreach(var shot in context.CurrentShots)CapturedImages.Add(shot.PicturePath);
        }

        void StartPendingProcessing(CancellationToken workflowToken)
        {
            lock(processingSync)
            {
                if(processingTail!=null&&!processingTail.IsCompleted)throw new InvalidOperationException("A previous capture-processing batch is still active.");
                if(processingTail?.IsFaulted==true)log.LogDebug(processingTail.Exception,"Previous capture-processing batch ended with an observed error");
                processingCts?.Dispose();
                processingCts=CancellationTokenSource.CreateLinkedTokenSource(workflowToken);
                processingTail=Task.CompletedTask;
            }
        }

        void QueuePendingProcessing(PendingCapture pending)
        {
            if(pending==null)throw new ArgumentNullException(nameof(pending));
            lock(processingSync)
            {
                if(processingCts==null)throw new InvalidOperationException("The capture-processing batch has not been started.");
                var previous=processingTail;
                var token=processingCts.Token;
                processingTail=ProcessPendingAfterAsync(previous,pending,token);
            }
        }

        async Task ProcessPendingAfterAsync(Task previous,PendingCapture pending,CancellationToken token)
        {
            await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            log.LogInformation("Deferred image/video processing starting for capture {CaptureId}",pending.Id);
            await capturePipeline.ProcessPendingAsync(pending,token).ConfigureAwait(false);
            log.LogInformation("Deferred image/video processing completed for capture {CaptureId}",pending.Id);
        }

        async Task<IReadOnlyList<CapturedShot>> CompletePendingProcessingAsync(CancellationToken token)
        {
            Task tail;
            CancellationTokenSource cts;
            lock(processingSync){tail=processingTail??Task.CompletedTask;cts=processingCts;}
            await tail.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result=await capturePipeline.CommitProcessedAsync(context.BoothSession.Id,context.PendingCaptures.ToList(),token).ConfigureAwait(false);
            lock(processingSync)
            {
                if(ReferenceEquals(processingTail,tail)){processingTail=Task.CompletedTask;processingCts=null;}
            }
            cts?.Dispose();
            return result;
        }

        async Task CancelPendingProcessingAsync()
        {
            Task tail;
            CancellationTokenSource cts;
            lock(processingSync){tail=processingTail??Task.CompletedTask;cts=processingCts;}
            try{cts?.Cancel();}catch(ObjectDisposedException){}
            try{await tail.ConfigureAwait(false);}
            catch(OperationCanceledException){}
            catch(Exception exception){log.LogDebug(exception,"Deferred capture processing stopped during cleanup");}
            lock(processingSync)
            {
                if(ReferenceEquals(processingTail,tail)){processingTail=Task.CompletedTask;processingCts=null;}
            }
            cts?.Dispose();
        }
        static double AverageMilliseconds(long ticks,int count)=>count<=0?0d:ticks*1000d/Stopwatch.Frequency/count;
        static int FrameSignature(byte[] data){unchecked{var hash=data.Length;var step=Math.Max(1,data.Length/32);for(var i=0;i<data.Length;i+=step)hash=(hash*397)^data[i];return hash;}}
        async Task StopLive(CancellationToken token = default(CancellationToken))
        {
            await liveGate.WaitAsync(token);
            try { await StopLiveCore(token); }
            finally { liveGate.Release(); }
        }
        async Task StopLiveCore(CancellationToken token = default(CancellationToken))
        {
            var cts = liveCts;
            var loop = liveLoopTask;
            var cameraId = liveCameraId;
            liveCts = null;
            liveLoopTask = null;
            liveCameraId = null;
            try
            {
                if (cts != null) cts.Cancel();
                if (loop != null) await AwaitCompletion(loop, token);
                if (!string.IsNullOrWhiteSpace(cameraId)) await live.StopAsync(cameraId, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { }
            catch (Exception error) { log.LogWarning(error,"Live View stop failed"); }
            finally
            {
                liveBeauty.Reset();
                LiveImage = null;
                videos.ClearLiveViewFrames();
                cts?.Dispose();
            }
        }
        async Task CleanupTemporary() { await CancelPendingProcessingAsync();var session=context.BoothSession;if(session==null)return;await capturePipeline.DiscardPendingAsync(context.PendingCaptures.ToList(),"Customer flow ended before deferred processing completed.",CancellationToken.None);context.PendingCaptures.Clear();BoothSessionWorkspace.ReplaceWorkspaceFiles(session,new System.Collections.Generic.Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));await sessions.AbandonAsync(session,"Customer flow ended before a deliverable was committed.",CancellationToken.None);await Task.Run(()=>BoothSessionWorkspace.Cleanup(session));context.CurrentShots.Clear();context.WorkingDirectory=null;CapturedImages.Clear();context.BoothSession=null; }
        async Task ReplaceCapturesAsync(System.Collections.Generic.IReadOnlyList<int> positions,System.Collections.Generic.IReadOnlyList<CapturedShot> replacements)
        {
            if(positions==null||replacements==null||positions.Count!=replacements.Count)throw new ArgumentException("Retake positions and captures must have matching counts.");
            var previous=new System.Collections.Generic.List<CapturedShot>(positions.Count);
            var changes=new System.Collections.Generic.Dictionary<string,CapturedShot>(StringComparer.Ordinal);
            for(var index=0;index<positions.Count;index++)
            {
                var position=positions[index];if(position<0||position>=context.CurrentShots.Count)throw new ArgumentOutOfRangeException(nameof(positions));
                var old=context.CurrentShots[position];var replacement=replacements[index];replacement.Sequence=old.Sequence;previous.Add(old);changes.Add(old.Id,replacement);
            }
            await sessions.ReplaceCapturedShotsAsync(context.BoothSession.Id,changes,CancellationToken.None);
            context.BoothSession = await sessions.GetAsync(context.BoothSession.Id, CancellationToken.None);
            for(var index=0;index<positions.Count;index++)context.CurrentShots[positions[index]]=replacements[index];
            foreach(var old in previous)
            {
                try { if (BoothSessionWorkspace.Contains(context.BoothSession, old.PicturePath) && System.IO.File.Exists(old.PicturePath)) System.IO.File.Delete(old.PicturePath); } catch { }
                try { if (BoothSessionWorkspace.Contains(context.BoothSession, old.VideoPath) && System.IO.File.Exists(old.VideoPath)) System.IO.File.Delete(old.VideoPath); } catch { }
            }
            CapturedImages.Clear(); foreach (var shot in context.CurrentShots) CapturedImages.Add(shot.PicturePath);
        }
        void RefreshReviewPhotos()
        {
            ReviewPhotos.Clear();
            for (var i = 0; i < context.CurrentShots.Count; i++) ReviewPhotos.Add(new CapturedPhotoItem(i, context.CurrentShots[i].PicturePath, () => ((AsyncCommand)RetakeCommand).NotifyCanExecuteChanged()));
            SelectedReviewPhoto = ReviewPhotos.FirstOrDefault();
        }
        void OnStateChanged()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(OnStateChanged)); return; }
            RaiseState(); RaiseCaptureMode(); ((AsyncCommand)RetakeCommand).NotifyCanExecuteChanged();
            if (machine.State == CustomerWorkflowState.Idle && context.CameraDisconnectedPending) { CameraConnected = false; LiveImage = null; StatusMessage = "Waiting for camera…"; }
        }
        void RaiseState() { Raise(nameof(IsIdle)); Raise(nameof(IsCountdown)); Raise(nameof(IsSmile)); Raise(nameof(IsCapturing)); Raise(nameof(IsInterShotDelay)); Raise(nameof(IsPreview)); Raise(nameof(IsPrinting)); Raise(nameof(IsBusy)); Raise(nameof(ProgressText)); }
        void RaiseCaptureMode() { Raise(nameof(IsModeSelection)); Raise(nameof(IsManualReady)); ((AsyncCommand)AutomaticCaptureCommand).NotifyCanExecuteChanged(); ((AsyncCommand)ManualModeCommand).NotifyCanExecuteChanged(); ((AsyncCommand)ManualShutterCommand).NotifyCanExecuteChanged(); }
        public bool RequestManualCapture()
        {
            if (!ManualShutterCommand.CanExecute(null)) return false;
            ManualShutterCommand.Execute(null);
            return true;
        }
        void Fail(Exception e, string friendly, bool showError) { log.LogError(e, friendly); ErrorMessage = showError ? friendly : null; StatusMessage = friendly; }
    }
    public sealed class CapturedPhotoItem : ObservableObject
    {
        readonly Action changed; bool selected;
        public CapturedPhotoItem(int position, string path, Action selectionChanged) { Position = position; Path = path; changed = selectionChanged; }
        public int Position { get; } public int Number => Position + 1; public string Path { get; }
        public bool IsRetakeSelected { get => selected; set { if (Set(ref selected, value)) changed?.Invoke(); } }
    }
}
