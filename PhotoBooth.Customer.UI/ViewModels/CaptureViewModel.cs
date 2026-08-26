using System;
using System.Collections.ObjectModel;
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
        readonly ISessionService sessions;
        readonly IPresetService presets;
        readonly ICapturePipeline capturePipeline;
        readonly IMotionPhotoService motionPhotos;
        readonly ILogger<CaptureViewModel> log;
        readonly LiveColorState liveColor;
        CancellationTokenSource liveCts;
        CancellationTokenSource workflowCts;
        byte[] liveImage;
        readonly SemaphoreSlim cameraGate = new SemaphoreSlim(1, 1);
        int countdown, currentPhoto, totalPhotos, delayRemaining;
        string message = "Waiting for camera…", error;
        bool cameraConnected;
        bool shutterFlash;
        double liveViewScaleX = 1d;
        CapturedPhotoItem selectedReviewPhoto;
        MediaPlayer clockPlayer;
        MediaPlayer shutterPlayer;

        public CaptureViewModel(CustomerWorkflowStateMachine m, CustomerWorkflowContext ctx, ICameraService c,
            ILiveViewService l, ISettingsService st, ISessionService ss, IPresetService ps,
            ICapturePipeline pipeline, IMotionPhotoService motionPhotoService, LiveColorState liveColorState, ILogger<CaptureViewModel> logger)
        {
            machine = m; context = ctx; cameras = c; live = l; settings = st; sessions = ss;
            presets = ps; capturePipeline = pipeline; motionPhotos = motionPhotoService; liveColor=liveColorState; log = logger;
            StartCommand = new AsyncCommand(Start, () => machine.State == CustomerWorkflowState.Idle);
            RetakeCommand = new AsyncCommand(Retake, () => machine.State == CustomerWorkflowState.Preview && ReviewPhotos.Any(x => x.IsRetakeSelected));
            SelectFrameCommand = new RelayCommand(() => machine.MoveTo(CustomerWorkflowState.FrameSelection));
            RetryCommand = new AsyncCommand(CheckCamera);
            CancelErrorCommand = new RelayCommand(() => { ErrorMessage = null; machine.RecoverToIdle(); });
            machine.StateChanged += (s, e) => OnStateChanged();
            cameras.CamerasChanged += async (s, e) => await RecoverCamera();
        }

        public byte[] LiveImage { get => liveImage; private set => Set(ref liveImage, value); }
        public int LiveFrameWidth { get; private set; }
        public int LiveFrameHeight { get; private set; }
        public LiveColorState LiveColor => liveColor;
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
        public ICommand StartCommand { get; }
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

        async Task Start()
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
                context.Settings = await settings.GetAsync(token) ?? new Settings();
                var allPresets = await presets.GetAllAsync(token);
                context.DefaultPreset = context.Settings.DefaultPresetId.HasValue
                    ? allPresets.FirstOrDefault(x => x.Id == context.Settings.DefaultPresetId)
                    : allPresets.FirstOrDefault(x => x.IsDefault);
                context.Session = await sessions.GetDefaultAsync(token);
                context.CaptureId = null;
                context.CurrentShots.Clear();
                await Task.Run(() => SessionWorkspace.Prepare(context.Session), token);
                context.WorkingDirectory = SessionWorkspace.GetPath(context.Session);
                SessionWorkspace.ReplaceWorkspaceFiles(context.Session, new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                await sessions.UpdateAsync(context.Session, token);
                CapturedImages.Clear();
                TotalPhotos = Math.Max(1, Math.Min(8, context.Settings.PhotoCount));
                for (var i = 1; i <= TotalPhotos; i++)
                {
                    token.ThrowIfCancellationRequested();
                    CurrentPhoto = i;
                    await RunCountdownAndSmileAsync(context.Settings.CountdownSeconds, token, i == 1);
                    log.LogInformation("Physical capture and shutter effect starting {Current}/{Total}", i, TotalPhotos);
                    context.Session = await CaptureWithShutterAsync(context.Session.Id, cameraId, token);
                    log.LogInformation("Physical capture completed {Current}/{Total}", i, TotalPhotos);
                    var newest = context.Session.CapturedShots?.LastOrDefault();
                    if (newest == null) throw new InvalidOperationException("Camera did not return a captured shot.");
                    context.CurrentShots.Add(newest);
                    CapturedImages.Clear();
                    foreach (var shot in context.CurrentShots) CapturedImages.Add(shot.PicturePath);
                    log.LogInformation("Capture finished {Current}/{Total}", i, TotalPhotos);

                    if (i < TotalPhotos)
                    {
                        machine.MoveTo(CustomerWorkflowState.InterShotDelay);
                        DelayRemaining = 0; StatusMessage = "Get ready for the next photo…";
                        machine.MoveTo(CustomerWorkflowState.Countdown);
                    }
                }

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
                for (var sequence = 0; sequence < selected.Count; sequence++)
                {
                    var position = selected[sequence]; CurrentPhoto = sequence + 1;
                    await RunCountdownAndSmileAsync(context.Settings?.CountdownSeconds ?? 3, token);
                    log.LogInformation("Physical retake and shutter effect starting {Current}/{Total}", sequence + 1, selected.Count);
                    context.Session = await CaptureWithShutterAsync(context.Session.Id, cameraId, token);
                    log.LogInformation("Physical retake completed {Current}/{Total}", sequence + 1, selected.Count);
                    var newest = context.Session.CapturedShots?.LastOrDefault();
                    if (newest == null) throw new InvalidOperationException("Camera did not return the replacement shot.");
                    await ReplaceCaptureAsync(position, newest);
                    if (sequence < selected.Count - 1) machine.MoveTo(CustomerWorkflowState.InterShotDelay);
                }
                machine.MoveTo(CustomerWorkflowState.Preview); RefreshReviewPhotos(); StatusMessage = "Retake complete";
            }
            catch (OperationCanceledException) { await CleanupTemporary(); machine.RecoverToIdle(); }
            catch (Exception e) { Fail(e, "Retake failed", true); await CleanupTemporary(); if (machine.State != CustomerWorkflowState.Preview) machine.RecoverToIdle(); }
            finally { ReleaseAllAudio(); IsShutterFlash = false; }
        }
        public async Task ResetToStartAsync(){workflowCts?.Cancel();ReleaseAllAudio();if(context.Session!=null)await CleanupTemporary();ReviewPhotos.Clear();SelectedReviewPhoto=null;CurrentPhoto=0;TotalPhotos=0;CountdownNumber=0;DelayRemaining=0;StatusMessage=CameraConnected?"Ready":"Waiting for camera…";machine.RecoverToIdle();}
        public async Task ActivateAsync(){var configured=await settings.GetAsync(CancellationToken.None);LiveViewScaleX=configured?.AutoFlip==true?-1d:1d;await liveColor.RefreshAsync(configured,CancellationToken.None);await CheckCamera();}
        public async Task ShutdownAsync(){workflowCts?.Cancel();ReleaseAllAudio();if(context.Session!=null)await CleanupTemporary();await StopLive();}
        async Task RunCountdownAndSmileAsync(int seconds, CancellationToken token, bool clockAlreadyPlaying = false)
        {
            machine.MoveTo(CustomerWorkflowState.Countdown);
            if (!clockAlreadyPlaying) PlayClock();
            try { for (var value = Math.Max(1, seconds); value >= 1; value--) { CountdownNumber = value; await Task.Delay(1000, token); } }
            finally { ReleaseClock(); }
            machine.MoveTo(CustomerWorkflowState.Smile); StatusMessage = "Smile!";
            await Task.Delay(500, token);
            ReleaseAllAudio();
        }
        async Task<Session> CaptureWithShutterAsync(Guid sessionId, string cameraId, CancellationToken token)
        {
            // Start the camera path first. Sound and overlay are deliberately kept
            // outside that critical path and run concurrently with capture/transfer.
            var captureTask = capturePipeline.ExecuteAsync(sessionId, cameraId, context.WorkingDirectory, token);
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
        async Task StartLive() { await StopLive(); var camera = context.Camera; if (camera == null) return; liveCts = new CancellationTokenSource(); await live.StartAsync(camera.Id, liveCts.Token); _ = LiveLoop(camera.Id, liveCts.Token); }
        async Task LiveLoop(string cameraId, CancellationToken token) { while (!token.IsCancellationRequested) try { var frame = await live.GetFrameAsync(cameraId, token); if (frame?.ImageData != null) { LiveFrameWidth=frame.Width;LiveFrameHeight=frame.Height;Raise(nameof(LiveFrameWidth));Raise(nameof(LiveFrameHeight));LiveImage = frame.ImageData; motionPhotos.AddLiveViewFrame(frame.ImageData, frame.TimestampUtc == default(DateTime) ? DateTime.UtcNow : frame.TimestampUtc); } await Task.Delay(40, token); } catch (OperationCanceledException) { break; } catch (Exception e) { log.LogWarning(e, "Live View unavailable; retrying"); try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; } } }
        async Task StopLive() { var c = liveCts; if (c == null) return; c.Cancel(); liveCts = null; if (context.Camera != null) try { await live.StopAsync(context.Camera.Id, CancellationToken.None); } catch { } }
        async Task CleanupTemporary() { var session=context.Session;if(session==null)return;SessionWorkspace.ReplaceWorkspaceFiles(session,new System.Collections.Generic.Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));await sessions.UpdateAsync(session,CancellationToken.None);await Task.Run(()=>SessionWorkspace.Cleanup(session));context.CurrentShots.Clear();context.WorkingDirectory=null;CapturedImages.Clear();context.Session=null; }
        async Task ReplaceCaptureAsync(int position, CapturedShot newest)
        {
            if (position < 0 || position >= context.CurrentShots.Count) throw new ArgumentOutOfRangeException(nameof(position));
            var old = context.CurrentShots[position];
            newest.Sequence = old.Sequence;
            await sessions.ReplaceCapturedShotAsync(context.Session.Id, old.Id, newest, CancellationToken.None);
            context.Session = await sessions.GetAsync(context.Session.Id, CancellationToken.None);
            context.CurrentShots[position] = newest;
            try { if (SessionWorkspace.Contains(context.Session, old.PicturePath) && System.IO.File.Exists(old.PicturePath)) System.IO.File.Delete(old.PicturePath); } catch { }
            try { if (SessionWorkspace.Contains(context.Session, old.MotionPhotoPath) && System.IO.File.Exists(old.MotionPhotoPath)) System.IO.File.Delete(old.MotionPhotoPath); } catch { }
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
            RaiseState(); ((AsyncCommand)StartCommand).NotifyCanExecuteChanged(); ((AsyncCommand)RetakeCommand).NotifyCanExecuteChanged();
            if (machine.State == CustomerWorkflowState.Idle && context.CameraDisconnectedPending) { CameraConnected = false; LiveImage = null; StatusMessage = "Waiting for camera…"; }
        }
        void RaiseState() { Raise(nameof(IsIdle)); Raise(nameof(IsCountdown)); Raise(nameof(IsSmile)); Raise(nameof(IsCapturing)); Raise(nameof(IsInterShotDelay)); Raise(nameof(IsPreview)); Raise(nameof(IsPrinting)); Raise(nameof(IsBusy)); Raise(nameof(ProgressText)); }
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
