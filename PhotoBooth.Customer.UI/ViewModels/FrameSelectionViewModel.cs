using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Pipelines;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;
using PhotoBooth.Customer.UI.Workflow;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class CapturedPhotoChoice : ObservableObject
    {
        bool selected;
        public CapturedPhotoChoice(string path, int number, string picturePath = null) { Path = path; Number = number; PicturePath = picturePath ?? path; }
        public string Path { get; }
        public string PicturePath { get; }
        public int Number { get; }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
    }

    public sealed class FrameSlotChoice : ObservableObject
    {
        CapturedPhotoChoice photo;
        bool selected;
        double mediaZoom = 1d, mediaCenterX = 0.5d, mediaCenterY = 0.5d;
        public FrameSlotChoice(FrameSlot slot) { Slot = new FrameSlot { Id=slot.Id, Index=slot.Index, X=slot.X, Y=slot.Y, Width=slot.Width, Height=slot.Height }; }
        public FrameSlot Slot { get; }
        public int Number => Slot.Index + 1;
        public double X => Slot.X;
        public double Y => Slot.Y;
        public double Width => Slot.Width;
        public double Height => Slot.Height;
        public CapturedPhotoChoice Photo { get => photo; set { if (Set(ref photo, value)) { MediaZoom=1;MediaCenterX=0.5;MediaCenterY=0.5;Raise(nameof(PhotoPath)); } } }
        public string PhotoPath => Photo?.Path;
        public double MediaZoom { get=>mediaZoom; set { if(Set(ref mediaZoom,MediaTransformGeometry.Clamp(value,1,2))) Slot.MediaZoom=mediaZoom; } }
        public double MediaCenterX { get=>mediaCenterX; set { if(Set(ref mediaCenterX,MediaTransformGeometry.Clamp(value,0,1))) Slot.MediaCenterX=mediaCenterX; } }
        public double MediaCenterY { get=>mediaCenterY; set { if(Set(ref mediaCenterY,MediaTransformGeometry.Clamp(value,0,1))) Slot.MediaCenterY=mediaCenterY; } }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
    }

    public sealed class FrameSelectionViewModel : ObservableObject
    {
        readonly CustomerWorkflowStateMachine machine;
        readonly CustomerWorkflowContext context;
        readonly IFrameService frames;
        readonly IPresetService presets;
        readonly IPrinterService printers;
        readonly IImageCompositionService composer;
        readonly IPresetProcessor presetProcessor;
        readonly ISessionService sessions;
        readonly ICaptureService captures;
        readonly IPrintPipeline printPipeline;
        readonly IVideoService videos;
        readonly ILogger<FrameSelectionViewModel> log;
        readonly IFeatureFlagService features;
        readonly SemaphoreSlim composeGate = new SemaphoreSlim(1, 1);
        readonly AsyncCommand printCommand;
        readonly object operationSync = new object();
        readonly object previewCancellationSync = new object();
        readonly HashSet<Task> previewOperations = new HashSet<Task>();
        Frame selected;
        FrameSlotChoice selectedSlot;
        CapturedPhotoChoice selectedPhoto;
        CancellationTokenSource previewCancellation;
        Task previewTask = Task.CompletedTask;
        Task loadTask = Task.CompletedTask;
        Task finishTask = Task.CompletedTask;
        bool pageStopping;
        string preview;
        string error;
        bool printing;
        bool printingEnabled;
        int printCopies = 1;
        int zoomPercent = 100;

        public FrameSelectionViewModel(CustomerWorkflowStateMachine m, CustomerWorkflowContext c, IFrameService f,
            IPresetService p, IPrinterService printer, IImageCompositionService compose, IPresetProcessor processor,
            IStorageManager storageManager, ISessionService session, ICaptureService captureService,
            IGifAnimationService gifService, IPrintPipeline pipeline, IVideoService videoService, IFeatureFlagService featureFlags, ILogger<FrameSelectionViewModel> logger)
        {
            machine = m; context = c; frames = f; presets = p; printers = printer; composer = compose;
            presetProcessor = processor; sessions = session; captures = captureService; printPipeline = pipeline; videos = videoService; features = featureFlags; log = logger;
            CancelCommand = new RelayCommand(BackToPreview);
            printCommand = new AsyncCommand(RunFinishTracked, () => CanFinish && !IsPrinting);
            PrintCommand = printCommand;
            RetryCommand = new AsyncCommand(UpdatePreview);
            CancelErrorCommand = new RelayCommand(() => ErrorMessage = null);
            SelectPhotoCommand = new ParameterCommand(value => SelectPhoto(value as CapturedPhotoChoice));
            SelectSlotCommand = new ParameterCommand(value => SelectSlot(value as FrameSlotChoice));
            ClearSlotCommand = new RelayCommand(ClearSelectedSlot);
            IncreaseCopiesCommand = new RelayCommand(() => PrintCopies++);
            DecreaseCopiesCommand = new RelayCommand(() => PrintCopies--);
            ZoomInCommand = new RelayCommand(() => ZoomPercent += 25);
            ZoomOutCommand = new RelayCommand(() => ZoomPercent -= 25);
            ResetZoomCommand = new RelayCommand(() => ZoomPercent = 100);
            machine.StateChanged += OnWorkflowStateChanged;
        }

        public event EventHandler<string> PrinterConnectionRequired;
        public ObservableCollection<Frame> PinnedFrames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<CapturedPhotoChoice> CapturedPhotos { get; } = new ObservableCollection<CapturedPhotoChoice>();
        public ObservableCollection<FrameSlotChoice> FrameSlots { get; } = new ObservableCollection<FrameSlotChoice>();
        public Frame SelectedFrame { get => selected; set { if (!Set(ref selected, value) || value == null) return; context.SelectedFrame = value; BuildSlots(value); QueuePreview(); } }
        public FrameSlotChoice SelectedSlot { get => selectedSlot; private set => Set(ref selectedSlot, value); }
        public CapturedPhotoChoice SelectedPhoto { get => selectedPhoto; private set => Set(ref selectedPhoto, value); }
        public string PreviewPath { get => preview; private set => Set(ref preview, value); }
        public string ErrorMessage { get => error; private set { Set(ref error, value); Raise(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool IsPrinting { get => printing; private set { Set(ref printing, value); printCommand.NotifyCanExecuteChanged(); } }
        public bool PrintingEnabled { get => printingEnabled; private set => Set(ref printingEnabled, value); }
        public int PrintCopies { get => printCopies; set => Set(ref printCopies, Math.Max(1, Math.Min(99, value))); }
        public int ZoomPercent
        {
            get => zoomPercent;
            set
            {
                if (!Set(ref zoomPercent, Math.Max(100, Math.Min(300, value)))) return;
                Raise(nameof(ZoomScale));
                Raise(nameof(ZoomText));
            }
        }
        public double ZoomScale => ZoomPercent / 100d;
        public string ZoomText => ZoomPercent + "%";
        public bool CanFinish => FrameSlots.Count > 0 && FrameSlots.All(x => x.Photo != null);
        public string AssignmentStatus => FrameSlots.Count == 0 ? string.Empty : FrameSlots.Count(x => x.Photo != null) + " / " + FrameSlots.Count + " ô đã có ảnh";
        public string Guidance => SelectedSlot == null ? "Chọn một ô trên frame" : SelectedPhoto == null ? "Đã chọn ô " + SelectedSlot.Number + " — hãy chọn ảnh bên phải" : "Ảnh " + SelectedPhoto.Number + " đang được chọn — chạm ô để đặt ảnh";
        public ICommand CancelCommand { get; }
        public ICommand PrintCommand { get; }
        public ICommand RetryCommand { get; }
        public ICommand CancelErrorCommand { get; }
        public ICommand SelectPhotoCommand { get; }
        public ICommand SelectSlotCommand { get; }
        public ICommand ClearSlotCommand { get; }
        public ICommand IncreaseCopiesCommand { get; }
        public ICommand DecreaseCopiesCommand { get; }
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ResetZoomCommand { get; }

        void OnWorkflowStateChanged(object sender, EventArgs e)
        {
            if (machine.State == CustomerWorkflowState.Preview)
            {
                lock (operationSync) pageStopping = false;
                return;
            }
            if (machine.State == CustomerWorkflowState.FrameSelection) StartLoad();
        }

        void StartLoad()
        {
            lock (operationSync)
            {
                if (pageStopping) return;
                loadTask = Load();
            }
        }

        async Task Load()
        {
            try
            {
                ErrorMessage = null; PrintCopies = 1; ZoomPercent = 100; PrintingEnabled = context.PrintingEnabled;
                PinnedFrames.Clear(); CapturedPhotos.Clear(); FrameSlots.Clear();
                var sourceFiles = GetSourceFiles();
                for (var i = 0; i < sourceFiles.Count; i++) CapturedPhotos.Add(new CapturedPhotoChoice(sourceFiles[i], i + 1));
                var usable = (await frames.GetAllAsync(CancellationToken.None)).Where(x => x.Slots != null && x.Slots.Count > 0 && x.PixelWidth > 0 && x.PixelHeight > 0 && !string.IsNullOrWhiteSpace(x.SourcePath) && File.Exists(x.SourcePath)).OrderByDescending(x => x.CreatedAtUtc).ToList();
                var values = usable.Where(x => x.IsPinned).Take(10).ToList();
                if (values.Count == 0 && usable.Count > 0) values.Add(usable[0]);
                foreach (var frame in values) PinnedFrames.Add(frame);
                if (values.Count == 0) { ErrorMessage = "Không tìm thấy frame hợp lệ. Hãy nhập lại frame trong Admin."; return; }
                SelectedFrame = context.Settings?.DefaultFrameId.HasValue == true ? values.FirstOrDefault(x => x.Id == context.Settings.DefaultFrameId.Value) ?? values[0] : values[0];
            }
            catch (Exception e) { Fail(e, "Không thể tải frame"); }
        }

        void BuildSlots(Frame frame)
        {
            FrameSlots.Clear();
            foreach (var slot in frame.Slots.OrderBy(x => x.Index)) FrameSlots.Add(new FrameSlotChoice(slot));
            SelectedSlot = FrameSlots.FirstOrDefault(); SelectedPhoto = null; UpdateSelectionState(); NotifyAssignmentsChanged();
        }

        void SelectSlot(FrameSlotChoice slot)
        {
            if (slot == null || IsPrinting) return;
            SelectedSlot = slot; if (SelectedPhoto != null) Assign(slot, SelectedPhoto); UpdateSelectionState();
        }

        void SelectPhoto(CapturedPhotoChoice photo)
        {
            if (photo == null || IsPrinting) return;
            SelectedPhoto = photo; if (SelectedSlot != null) Assign(SelectedSlot, photo); UpdateSelectionState();
        }

        void Assign(FrameSlotChoice slot, CapturedPhotoChoice photo)
        {
            slot.Photo = photo;
            NotifyAssignmentsChanged();
            QueuePreview();
        }

        void ClearSelectedSlot()
        {
            if (SelectedSlot == null || IsPrinting) return;
            SelectedSlot.Photo = null; NotifyAssignmentsChanged(); QueuePreview();
        }

        void BackToPreview()
        {
            if (IsPrinting) return;
            previewCancellation?.Cancel();
            machine.MoveTo(CustomerWorkflowState.Preview);
        }

        void UpdateSelectionState()
        {
            foreach (var slot in FrameSlots) slot.IsSelected = slot == SelectedSlot;
            foreach (var photo in CapturedPhotos) photo.IsSelected = photo == SelectedPhoto;
            Raise(nameof(Guidance));
        }

        void NotifyAssignmentsChanged()
        {
            Raise(nameof(CanFinish)); Raise(nameof(AssignmentStatus)); Raise(nameof(Guidance)); printCommand.NotifyCanExecuteChanged();
        }

        async Task UpdatePreview()
        {
            var cancellation = new CancellationTokenSource();
            lock (previewCancellationSync)
            {
                previewCancellation?.Cancel();
                previewCancellation = cancellation;
            }
            try { ErrorMessage = null; await Compose(false, cancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Fail(e, "Không thể tạo bản xem trước"); }
            finally
            {
                lock (previewCancellationSync)
                    if (ReferenceEquals(previewCancellation, cancellation)) previewCancellation = null;
                cancellation.Dispose();
            }
        }

        void QueuePreview()
        {
            Task task;
            lock (operationSync)
            {
                if (pageStopping) return;
                task = UpdatePreview();
                previewTask = task;
                previewOperations.Add(task);
            }
            _ = ObservePreview(task);
        }

        async Task ObservePreview(Task task)
        {
            try { await task; }
            finally { lock (operationSync) previewOperations.Remove(task); }
        }

        public async Task ShutdownAsync()
        {
            Task[] previews;
            Task loading;
            Task finishing;
            lock (operationSync)
            {
                pageStopping = true;
                previews = previewOperations.ToArray();
                loading = loadTask;
                finishing = finishTask;
            }
            lock (previewCancellationSync) previewCancellation?.Cancel();
            try { await Task.WhenAll(previews.Concat(new[] { previewTask, loading })); }
            catch (OperationCanceledException) { }
            await finishing;
        }

        async Task Compose(bool final, CancellationToken token)
        {
            await composeGate.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested) return;
                if (context.Session == null || SelectedFrame == null) return;
                if (final && !CanFinish) throw new InvalidOperationException("Hãy đặt ảnh vào tất cả các ô trước khi tiếp tục.");
                var all = await presets.GetAllAsync(token);
                context.DefaultPreset = context.Settings?.DefaultPresetId.HasValue == true ? all.FirstOrDefault(x => x.Id == context.Settings.DefaultPresetId.Value) : all.FirstOrDefault(x => x.IsDefault);
                var sourceFiles = GetSourceFiles();
                var workingDirectory = string.IsNullOrWhiteSpace(context.WorkingDirectory) ? SessionWorkspace.GetPath(context.Session) : context.WorkingDirectory;
                Directory.CreateDirectory(workingDirectory);
                var working = new Session { Id = context.Session.Id, StartedAtUtc = context.Session.StartedAtUtc, OutputDirectory = workingDirectory, SessionNumber = context.Session.SessionNumber, FrameIndex = context.Session.FrameIndex, CapturedFiles = sourceFiles };
                var assignments = FrameSlots.Where(x => x.Photo != null).ToDictionary(x => x.Slot.Index, x => x.Photo.Path);
                var composed = await composer.ComposeAsync(working, FrameWithTransforms(), context.DefaultPreset, final, assignments, token);
                if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(composed)) return;
                if (final && context.DefaultPreset != null)
                {
                    var temp = Path.Combine(workingDirectory, "preset-" + Guid.NewGuid().ToString("N") + ".png");
                    await presetProcessor.ProcessAsync(composed, context.DefaultPreset, temp, token);
                    await Task.Run(() => { File.Copy(temp, composed, true); File.Delete(temp); }, token);
                }
                if (!final)
                {
                    var oldPreview = PreviewPath; PreviewPath = composed;
                    if (!string.IsNullOrWhiteSpace(oldPreview) && oldPreview != composed && SessionWorkspace.Contains(context.Session, oldPreview) && File.Exists(oldPreview)) File.Delete(oldPreview);
                    return;
                }
                context.Session.FrameIndex = working.FrameIndex; context.Session.FinalImageId = working.FinalImageId;
                var currentShots = (context.CurrentShots??new List<CapturedShot>()).ToList();
                var promoted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sourceFiles) promoted[source] = SessionWorkspace.Promote(context.Session, source);
                foreach (var source in context.CurrentShots.Where(x=>x.HasVideo).Select(x=>x.VideoPath).Where(File.Exists)) promoted[source] = SessionWorkspace.Promote(context.Session, source);
                var finalComposite = SessionWorkspace.Promote(context.Session, composed);
                SessionWorkspace.ReplaceWorkspaceFiles(context.Session, promoted);
                context.CurrentShots = currentShots.Select(x=>new CapturedShot{Id=x.Id,Sequence=x.Sequence,PicturePath=promoted.TryGetValue(x.PicturePath,out var picture)?picture:x.PicturePath,VideoPath=x.HasVideo&&promoted.TryGetValue(x.VideoPath,out var video)?video:x.VideoPath,CapturedAtUtc=x.CapturedAtUtc}).ToList(); sourceFiles = context.CurrentShots.Select(x=>x.PicturePath).ToList();
                PreviewPath = finalComposite; context.Session.FinalImagePath = finalComposite; await sessions.UpdateAsync(context.Session, token);
                log.LogInformation("Frame selected {Frame} ({ImageId}, originals {OriginalCount})", SelectedFrame.Name, context.Session.FinalImageId, sourceFiles.Count);
            }
            finally { composeGate.Release(); }
        }

        List<string> GetSourceFiles()
        {
            var current = context.CurrentShots ?? new List<CapturedShot>();
            var sessionShots = context.Session?.CapturedShots ?? new CapturedShot[0];
            return (current.Count > 0 ? current : sessionShots.ToList()).Select(x=>x.PicturePath).Where(File.Exists).ToList();
        }

        Frame FrameWithTransforms() => new Frame { Id=SelectedFrame.Id, Name=SelectedFrame.Name, SourcePath=SelectedFrame.SourcePath,
            ThumbnailPath=SelectedFrame.ThumbnailPath, PixelWidth=SelectedFrame.PixelWidth, PixelHeight=SelectedFrame.PixelHeight,
            IsPinned=SelectedFrame.IsPinned, CreatedAtUtc=SelectedFrame.CreatedAtUtc, EventId=SelectedFrame.EventId,
            Slots=FrameSlots.Select(x=>x.Slot).ToList() };

        async Task Finish()
        {
            try
            {
                ErrorMessage = null; IsPrinting = true; previewCancellation?.Cancel();
                var transformedFrame = FrameWithTransforms();
                var slotShotIds = FrameSlots.ToDictionary(
                    x => x.Slot.Index,
                    x => FindShot(x.Photo?.Path)?.Id);
                await Compose(true, CancellationToken.None);
                if (await features.IsEnabledAsync("Video", CancellationToken.None))
                {
                    var assignments = new Dictionary<int, string>();
                    foreach (var slot in slotShotIds)
                    {
                        var shot = context.CurrentShots.FirstOrDefault(x => string.Equals(x.Id, slot.Value, StringComparison.Ordinal));
                        if (shot == null || !shot.HasVideo || !File.Exists(shot.VideoPath))
                            throw new InvalidOperationException("Video tương ứng với ảnh ở ô " + (slot.Key + 1) + " không còn khả dụng.");
                        assignments[slot.Key] = shot.VideoPath;
                    }
                    var still = context.Session?.FinalImagePath;
                    if (string.IsNullOrWhiteSpace(still) || !File.Exists(still))
                        throw new FileNotFoundException("Ảnh ghép cuối không còn khả dụng.", still);
                    var destination = Path.Combine(Path.GetDirectoryName(still), Path.GetFileNameWithoutExtension(still) + ".mp4");
                    await videos.ComposeAsync(still, transformedFrame, assignments, destination, CancellationToken.None);
                    var days = context.Settings?.SessionRetentionDays ?? 30;
                    var expires = days > 0 ? (DateTime?)DateTime.UtcNow.AddDays(days) : null;
                    var capture = await captures.CreateWithCompositeVideoAsync(
                        context.Session.Id, SelectedFrame.Id, context.Session.FinalImageId, still,
                        context.CurrentShots, destination, assignments.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        expires, CancellationToken.None);
                    context.CaptureId = capture.Id;
                }
                else if (string.IsNullOrWhiteSpace(context.CaptureId))
                {
                    var days = context.Settings?.SessionRetentionDays ?? 30; var expires = days > 0 ? (DateTime?)DateTime.UtcNow.AddDays(days) : null;
                    var capture = await captures.CreateAsync(context.Session.Id, SelectedFrame.Id, context.Session.FinalImageId, context.Session.FinalImagePath, context.CurrentShots, expires, CancellationToken.None); context.CaptureId = capture.Id;
                }
                machine.MoveTo(CustomerWorkflowState.Printing);
                if (context.PrintingEnabled)
                {
                    var profiles = await printers.GetProfilesAsync(CancellationToken.None); var profile = profiles.SingleOrDefault(x => x.IsDefault);
                    if (profile == null || !string.Equals(profile.PrinterId, context.ConnectedPrinterId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Default printer changed. Reconnect the printer.");
                    await printPipeline.ExecuteAsync(context.Session.Id, profile.Id, Math.Max(1, PrintCopies), CancellationToken.None);
                }
                machine.MoveTo(CustomerWorkflowState.Complete);
            }
            catch (Exception e)
            {
                if (machine.State == CustomerWorkflowState.Printing) machine.MoveTo(CustomerWorkflowState.FrameSelection);
                Fail(e, e.Message);
            }
            finally { IsPrinting = false; }
        }

        async Task RunFinishTracked()
        {
            var task = Finish();
            lock (operationSync) finishTask = task;
            try { await task; }
            finally
            {
                lock (operationSync)
                    if (ReferenceEquals(finishTask, task)) finishTask = Task.CompletedTask;
            }
        }

        CapturedShot FindShot(string picturePath)
        {
            if (string.IsNullOrWhiteSpace(picturePath)) return null;
            return (context.CurrentShots ?? new List<CapturedShot>()).FirstOrDefault(
                x => string.Equals(Path.GetFullPath(x.PicturePath), Path.GetFullPath(picturePath), StringComparison.OrdinalIgnoreCase));
        }

        void Fail(Exception e, string text) { log.LogError(e, text); ErrorMessage = text; }
    }
}
