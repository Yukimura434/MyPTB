using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        string path;
        public CapturedPhotoChoice(string path, int number, string picturePath = null) { this.path = path; Number = number; PicturePath = picturePath ?? path; }
        public string Path { get => path; private set => Set(ref path, value); }
        public string PicturePath { get; }
        public int Number { get; }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
        public Preset AppliedPreset { get; private set; }
        public double AppliedStrength { get; private set; }
        public string AppliedPath { get; private set; }
        public void PreviewEffect(string previewPath) => Path = previewPath ?? AppliedPath ?? PicturePath;
        public void CancelPreview() => Path = AppliedPath ?? PicturePath;
        public string CommitEffect(Preset preset, double strength, string appliedPath)
        {
            var previous = AppliedPath; AppliedPreset = preset; AppliedStrength = strength; AppliedPath = appliedPath; Path = appliedPath;
            Raise(nameof(AppliedPreset)); Raise(nameof(AppliedStrength)); Raise(nameof(AppliedPath)); return previous;
        }
        public string ClearEffect()
        {
            var previous=AppliedPath;AppliedPreset=null;AppliedStrength=0;AppliedPath=null;Path=PicturePath;
            Raise(nameof(AppliedPreset));Raise(nameof(AppliedStrength));Raise(nameof(AppliedPath));return previous;
        }
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
        public CapturedPhotoChoice Photo
        {
            get => photo;
            set
            {
                var previous=photo;if(!Set(ref photo,value))return;
                if(previous!=null)previous.PropertyChanged-=PhotoChanged;if(photo!=null)photo.PropertyChanged+=PhotoChanged;
                MediaZoom=1;MediaCenterX=0.5;MediaCenterY=0.5;Raise(nameof(PhotoPath));
            }
        }
        public string PhotoPath => Photo?.Path;
        public double MediaZoom { get=>mediaZoom; set { if(Set(ref mediaZoom,MediaTransformGeometry.Clamp(value,1,2))) Slot.MediaZoom=mediaZoom; } }
        public double MediaCenterX { get=>mediaCenterX; set { if(Set(ref mediaCenterX,MediaTransformGeometry.Clamp(value,0,1))) Slot.MediaCenterX=mediaCenterX; } }
        public double MediaCenterY { get=>mediaCenterY; set { if(Set(ref mediaCenterY,MediaTransformGeometry.Clamp(value,0,1))) Slot.MediaCenterY=mediaCenterY; } }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
        void PhotoChanged(object sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(CapturedPhotoChoice.Path))Raise(nameof(PhotoPath));}
    }

    public sealed class CustomerPresetChoice : ObservableObject
    {
        byte[] previewBytes;
        public Preset Preset { get; set; }
        public string Name => Preset.Name;
        public byte[] PreviewBytes { get => previewBytes; set => Set(ref previewBytes,value); }
    }

    public sealed class FrameSelectionViewModel : ObservableObject
    {
        readonly CustomerWorkflowStateMachine machine;
        readonly CustomerWorkflowContext context;
        readonly IFrameService frames;
        readonly IPresetService presets;
        readonly IColorLutService colorLuts;
        readonly IPrinterService printers;
        readonly IImageCompositionService composer;
        readonly IPresetProcessor presetProcessor;
        readonly IBoothSessionService sessions;
        readonly IDeliverableService deliverables;
        readonly IPrintPipeline printPipeline;
        readonly IVideoService videos;
        readonly ILogger<FrameSelectionViewModel> log;
        readonly IFeatureFlagService features;
        readonly SemaphoreSlim composeGate = new SemaphoreSlim(1, 1);
        readonly AsyncCommand printCommand;
        readonly AsyncCommand applyPresetToPhotoCommand;
        readonly AsyncCommand applyPresetToAllCommand;
        readonly object operationSync = new object();
        readonly object previewCancellationSync = new object();
        readonly HashSet<Task> previewOperations = new HashSet<Task>();
        readonly HashSet<Task> presetOperations = new HashSet<Task>();
        Frame selected;
        FrameSlotChoice selectedSlot;
        CapturedPhotoChoice selectedPhoto;
        CancellationTokenSource previewCancellation;
        Task previewTask = Task.CompletedTask;
        Task loadTask = Task.CompletedTask;
        Task finishTask = Task.CompletedTask;
        Task presetPreviewTask = Task.CompletedTask;
        Task presetThumbnailTask = Task.CompletedTask;
        Task presetApplyTask = Task.CompletedTask;
        bool pageStopping;
        string preview;
        string error;
        bool printing;
        bool printingEnabled;
        int printCopies = 1;
        int zoomPercent = 100;
        bool framesPanelActive = true;
        bool presetBusy;
        double presetStrengthPercent = 50d;
        string presetStatus;
        CustomerPresetChoice selectedPreset;
        CapturedPhotoChoice stagedPhoto;
        string stagedPresetPath;
        CancellationTokenSource presetPreviewCancellation;
        CancellationTokenSource presetThumbnailCancellation;

        public FrameSelectionViewModel(CustomerWorkflowStateMachine m, CustomerWorkflowContext c, IFrameService f,
            IPresetService p, IPrinterService printer, IImageCompositionService compose, IPresetProcessor processor,
            IStorageManager storageManager, IBoothSessionService session, IDeliverableService deliverableService,
            IGifAnimationService gifService, IPrintPipeline pipeline, IVideoService videoService, IFeatureFlagService featureFlags,
            IColorLutService colorLutService, ILogger<FrameSelectionViewModel> logger)
        {
            machine = m; context = c; frames = f; presets = p; printers = printer; composer = compose;
            presetProcessor = processor; sessions = session; deliverables = deliverableService; printPipeline = pipeline; videos = videoService; features = featureFlags; colorLuts=colorLutService; log = logger;
            CancelCommand = new RelayCommand(BackToPreview);
            printCommand = new AsyncCommand(RunFinishTracked, () => CanFinish && !IsPrinting);
            PrintCommand = printCommand;
            RetryCommand = new AsyncCommand(StartPreviewOperation);
            CancelErrorCommand = new RelayCommand(() => ErrorMessage = null);
            SelectPhotoCommand = new ParameterCommand(value => SelectPhoto(value as CapturedPhotoChoice));
            SelectSlotCommand = new ParameterCommand(value => SelectSlot(value as FrameSlotChoice));
            ClearSlotCommand = new RelayCommand(ClearSelectedSlot);
            IncreaseCopiesCommand = new RelayCommand(() => PrintCopies++);
            DecreaseCopiesCommand = new RelayCommand(() => PrintCopies--);
            ZoomInCommand = new RelayCommand(() => ZoomPercent += 25);
            ZoomOutCommand = new RelayCommand(() => ZoomPercent -= 25);
            ResetZoomCommand = new RelayCommand(() => ZoomPercent = 100);
            ShowFramesCommand = new RelayCommand(()=>FramesPanelActive=true);
            ShowPresetsCommand = new RelayCommand(()=>FramesPanelActive=false);
            applyPresetToPhotoCommand = new AsyncCommand(()=>RunPresetApplyTracked(false),()=>CanApplyPreset);
            applyPresetToAllCommand = new AsyncCommand(()=>RunPresetApplyTracked(true),()=>CanApplyPresetToAll);
            ApplyPresetToPhotoCommand=applyPresetToPhotoCommand;ApplyPresetToAllCommand=applyPresetToAllCommand;
            CancelPresetCommand = new RelayCommand(CancelPresetEditing);
            machine.StateChanged += OnWorkflowStateChanged;
        }

        public event EventHandler<string> PrinterConnectionRequired;
        public ObservableCollection<Frame> PinnedFrames { get; } = new ObservableCollection<Frame>();
        public ObservableCollection<CustomerPresetChoice> PinnedPresets { get; } = new ObservableCollection<CustomerPresetChoice>();
        public ObservableCollection<CapturedPhotoChoice> CapturedPhotos { get; } = new ObservableCollection<CapturedPhotoChoice>();
        public ObservableCollection<FrameSlotChoice> FrameSlots { get; } = new ObservableCollection<FrameSlotChoice>();
        public Frame SelectedFrame { get => selected; set { if (IsPresetBusy || !Set(ref selected, value) || value == null) return; context.SelectedFrame = value; BuildSlots(value); QueuePreview(); } }
        public FrameSlotChoice SelectedSlot { get => selectedSlot; private set => Set(ref selectedSlot, value); }
        public CapturedPhotoChoice SelectedPhoto { get => selectedPhoto; private set => Set(ref selectedPhoto, value); }
        public CustomerPresetChoice SelectedPreset
        {
            get=>selectedPreset;
            set
            {
                if(!Set(ref selectedPreset,value))return;
                CancelStagedPresetPreview();
                presetStrengthPercent=50d;Raise(nameof(PresetStrengthPercent));Raise(nameof(PresetStrengthText));
                PresetStatus=value==null?null:(ActivePhoto==null?"Đã chọn “"+value.Name+"”. Chọn một ảnh để xem trước, hoặc áp dụng cho tất cả.":"Đang xem trước “"+value.Name+"” trên ảnh đang chọn.");
                NotifyPresetState();QueuePresetEffectPreview();
            }
        }
        public string PreviewPath { get => preview; private set => Set(ref preview, value); }
        public string ErrorMessage { get => error; private set { Set(ref error, value); Raise(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool IsPrinting { get => printing; private set { Set(ref printing, value); printCommand.NotifyCanExecuteChanged();NotifyPresetState(); } }
        public bool PrintingEnabled { get => printingEnabled; private set => Set(ref printingEnabled, value); }
        public bool FramesPanelActive { get=>framesPanelActive; private set { if(Set(ref framesPanelActive,value)){Raise(nameof(PresetsPanelActive));} } }
        public bool PresetsPanelActive => !FramesPanelActive;
        public bool HasPinnedPresets => PinnedPresets.Count>0;
        public bool IsPresetBusy { get=>presetBusy;private set{if(Set(ref presetBusy,value))NotifyPresetState();} }
        public string PresetStatus { get=>presetStatus;private set=>Set(ref presetStatus,value); }
        public double PresetStrengthPercent
        {
            get=>presetStrengthPercent;
            set
            {
                var normalized=Math.Max(0,Math.Min(100,value));if(!Set(ref presetStrengthPercent,normalized))return;
                Raise(nameof(PresetStrengthText));QueuePresetEffectPreview();
            }
        }
        public string PresetStrengthText=>Math.Round(PresetStrengthPercent)+"%";
        public bool CanApplyPreset=>SelectedPreset!=null&&ActivePhoto!=null&&!IsPrinting&&!IsPresetBusy;
        public bool CanApplyPresetToAll=>SelectedPreset!=null&&CapturedPhotos.Count>0&&!IsPrinting&&!IsPresetBusy;
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
        public ICommand ShowFramesCommand { get; }
        public ICommand ShowPresetsCommand { get; }
        public ICommand ApplyPresetToPhotoCommand { get; }
        public ICommand ApplyPresetToAllCommand { get; }
        public ICommand CancelPresetCommand { get; }

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
                CancelStagedPresetPreview();CancelPresetBackgroundWork();
                PinnedFrames.Clear();PinnedPresets.Clear();CapturedPhotos.Clear();FrameSlots.Clear();
                SelectedPreset=null;FramesPanelActive=true;PresetStatus=null;
                var sourceFiles = GetSourceFiles();
                for (var i = 0; i < sourceFiles.Count; i++) CapturedPhotos.Add(new CapturedPhotoChoice(sourceFiles[i], i + 1));
                var availablePresets=await presets.GetAllAsync(CancellationToken.None);
                foreach(var preset in availablePresets.Where(x=>x.IsPinned).OrderBy(x=>x.Name).ThenBy(x=>x.Id))PinnedPresets.Add(new CustomerPresetChoice{Preset=preset});
                Raise(nameof(HasPinnedPresets));NotifyPresetState();
                presetThumbnailTask=TrackPresetOperation(LoadPresetThumbnails(sourceFiles.FirstOrDefault()));
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
            CancelStagedPresetPreview();
            FrameSlots.Clear();
            var slotPosition = 0;
            foreach (var slot in frame.Slots.OrderBy(x => x.Index))
            {
                var choice = new FrameSlotChoice(slot);
                if (CapturedPhotos.Count > 0)
                    choice.Photo = CapturedPhotos[slotPosition % CapturedPhotos.Count];
                FrameSlots.Add(choice);
                slotPosition++;
            }
            SelectedSlot = FrameSlots.FirstOrDefault(); SelectedPhoto = SelectedSlot?.Photo; UpdateSelectionState(); NotifyAssignmentsChanged();NotifyPresetState();
        }

        void SelectSlot(FrameSlotChoice slot)
        {
            if (slot == null || IsPrinting || IsPresetBusy) return;
            ResetPresetSelectionForTargetChange();
            SelectedSlot=slot;SelectedPhoto=slot.Photo;UpdateSelectionState();NotifyPresetState();
        }

        void SelectPhoto(CapturedPhotoChoice photo)
        {
            if (photo == null || IsPrinting || IsPresetBusy) return;
            ResetPresetSelectionForTargetChange();
            SelectedPhoto=photo;if(FramesPanelActive&&SelectedSlot!=null)Assign(SelectedSlot,photo);UpdateSelectionState();
            NotifyPresetState();
        }

        void Assign(FrameSlotChoice slot, CapturedPhotoChoice photo)
        {
            slot.Photo = photo;
            NotifyAssignmentsChanged();
            QueuePreview();
        }

        void ClearSelectedSlot()
        {
            if (SelectedSlot == null || IsPrinting || IsPresetBusy) return;
            SelectedSlot.Photo = null; NotifyAssignmentsChanged(); QueuePreview();
        }

        void BackToPreview()
        {
            if (IsPrinting || IsPresetBusy) return;
            CancelStagedPresetPreview();CancelPresetBackgroundWork();
            lock (previewCancellationSync) previewCancellation?.Cancel();
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

        CapturedPhotoChoice ActivePhoto=>PresetsPanelActive?SelectedPhoto:(SelectedSlot?.Photo??SelectedPhoto);

        void NotifyPresetState()
        {
            Raise(nameof(CanApplyPreset));Raise(nameof(CanApplyPresetToAll));applyPresetToPhotoCommand?.NotifyCanExecuteChanged();applyPresetToAllCommand?.NotifyCanExecuteChanged();
        }

        async Task LoadPresetThumbnails(string sourcePath)
        {
            if(string.IsNullOrWhiteSpace(sourcePath)||!File.Exists(sourcePath))return;
            var cancellation=new CancellationTokenSource();presetThumbnailCancellation=cancellation;
            try
            {
                foreach(var choice in PinnedPresets.ToList())
                {
                    try
                    {
                        var bytes=await colorLuts.RenderPreviewAsync(choice.Preset.LutAssetId,sourcePath,ColorLutData.DefaultStrength,cancellation.Token);
                        if(cancellation.IsCancellationRequested||bytes==null||!PinnedPresets.Contains(choice))return;choice.PreviewBytes=bytes;
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception e){log.LogWarning(e,"Không thể tạo thumbnail cho preset {PresetId}",choice.Preset.Id);}
                }
            }
            catch(OperationCanceledException){}
            finally{if(ReferenceEquals(presetThumbnailCancellation,cancellation))presetThumbnailCancellation=null;cancellation.Dispose();}
        }

        void QueuePresetEffectPreview()
        {
            if(pageStopping)return;
            var choice=SelectedPreset;var photo=ActivePhoto;
            if(choice==null||photo==null||IsPresetBusy)return;
            presetPreviewCancellation?.Cancel();
            var cancellation=new CancellationTokenSource();presetPreviewCancellation=cancellation;
            presetPreviewTask=TrackPresetOperation(RenderPresetEffectPreview(choice,photo,(float)(PresetStrengthPercent/100d),cancellation));
        }

        Task TrackPresetOperation(Task task)
        {
            lock(operationSync)presetOperations.Add(task);return ObservePresetOperation(task);
        }

        async Task ObservePresetOperation(Task task)
        {
            try{await task;}finally{lock(operationSync)presetOperations.Remove(task);}
        }

        async Task RenderPresetEffectPreview(CustomerPresetChoice choice,CapturedPhotoChoice photo,float strength,CancellationTokenSource cancellation)
        {
            string path=null;
            try
            {
                await Task.Delay(140,cancellation.Token);
                var bytes=await colorLuts.RenderPreviewAsync(choice.Preset.LutAssetId,photo.PicturePath,strength,cancellation.Token);
                if(cancellation.IsCancellationRequested||bytes==null)return;
                var directory=WorkingDirectory();path=Path.Combine(directory,".preset-preview-"+Guid.NewGuid().ToString("N")+".jpg");
                await Task.Run(()=>File.WriteAllBytes(path,bytes));
                if(cancellation.IsCancellationRequested)return;
                if(!ReferenceEquals(presetPreviewCancellation,cancellation)||!ReferenceEquals(SelectedPreset,choice)||!ReferenceEquals(ActivePhoto,photo)){TryDeleteDerived(path);return;}
                if(stagedPhoto!=null&&!ReferenceEquals(stagedPhoto,photo))stagedPhoto.CancelPreview();
                TryDeleteDerived(stagedPresetPath);stagedPhoto=photo;stagedPresetPath=path;path=null;photo.PreviewEffect(stagedPresetPath);
                PresetStatus="Xem trước "+Math.Round(strength*100)+"% trên ảnh "+photo.Number+".";QueuePreview();
            }
            catch(OperationCanceledException){}
            catch(Exception e){Fail(e,"Không thể xem trước preset");}
            finally
            {
                TryDeleteDerived(path);
                if(ReferenceEquals(presetPreviewCancellation,cancellation))presetPreviewCancellation=null;
                cancellation.Dispose();
            }
        }

        async Task ApplyPreset(bool allPhotos)
        {
            var choice=SelectedPreset;var active=ActivePhoto;if(choice==null||(!allPhotos&&active==null))return;
            CancelStagedPresetPreview();IsPresetBusy=true;
            var targets=allPhotos?CapturedPhotos.ToList():new List<CapturedPhotoChoice>{active};
            var generated=new List<KeyValuePair<CapturedPhotoChoice,string>>();
            try
            {
                ErrorMessage=null;var strength=(float)(PresetStrengthPercent/100d);
                PresetStatus=allPhotos?"Đang áp dụng preset cho tất cả ảnh…":"Đang áp dụng preset cho ảnh "+active.Number+"…";
                foreach(var photo in targets)
                {
                    var destination=Path.Combine(WorkingDirectory(),"preset-photo-"+photo.Number+"-"+Guid.NewGuid().ToString("N")+".jpg");
                    await colorLuts.ApplyToFileAsync(choice.Preset.Id,photo.PicturePath,destination,strength,CancellationToken.None);
                    generated.Add(new KeyValuePair<CapturedPhotoChoice,string>(photo,destination));
                }
                foreach(var item in generated)
                {
                    var previous=item.Key.CommitEffect(choice.Preset,strength,item.Value);TryDeleteDerived(previous);
                }
                generated.Clear();PresetStatus=allPhotos?"Đã áp dụng cho tất cả ảnh tĩnh.":"Đã áp dụng cho ảnh "+active.Number+".";QueuePreview();
            }
            catch(Exception e){Fail(e,"Không thể áp dụng preset");}
            finally{foreach(var item in generated)TryDeleteDerived(item.Value);IsPresetBusy=false;}
        }

        async Task RunPresetApplyTracked(bool allPhotos)
        {
            var task=ApplyPreset(allPhotos);lock(operationSync)presetApplyTask=task;
            try{await task;}finally{lock(operationSync)if(ReferenceEquals(presetApplyTask,task))presetApplyTask=Task.CompletedTask;}
        }

        void CancelPresetEditing()
        {
            if(IsPresetBusy)return;
            CancelStagedPresetPreview();
            var photo=ActivePhoto;var appliedPath=photo?.ClearEffect();TryDeleteDerived(appliedPath);
            ResetPresetSelectionForTargetChange();
            PresetStatus=appliedPath==null?"Đã huỷ bản xem trước preset.":"Đã gỡ preset khỏi ảnh "+photo.Number+".";
            QueuePreview();
        }

        void ResetPresetSelectionForTargetChange()
        {
            CancelStagedPresetPreview();
            if(selectedPreset!=null){selectedPreset=null;Raise(nameof(SelectedPreset));}
            presetStrengthPercent=50d;Raise(nameof(PresetStrengthPercent));Raise(nameof(PresetStrengthText));
            PresetStatus=null;NotifyPresetState();
        }

        void CancelStagedPresetPreview()
        {
            presetPreviewCancellation?.Cancel();presetPreviewCancellation=null;
            var photo=stagedPhoto;var path=stagedPresetPath;stagedPhoto=null;stagedPresetPath=null;
            photo?.CancelPreview();TryDeleteDerived(path);
        }

        void CancelPresetBackgroundWork()
        {
            presetPreviewCancellation?.Cancel();presetPreviewCancellation=null;
            presetThumbnailCancellation?.Cancel();presetThumbnailCancellation=null;
        }

        string WorkingDirectory()
        {
            if(context.BoothSession==null)throw new InvalidOperationException("Không tìm thấy phiên chụp hiện tại.");
            var directory=string.IsNullOrWhiteSpace(context.WorkingDirectory)?BoothSessionWorkspace.GetPath(context.BoothSession):context.WorkingDirectory;
            Directory.CreateDirectory(directory);return directory;
        }

        void TryDeleteDerived(string path)
        {
            if(string.IsNullOrWhiteSpace(path)||context.BoothSession==null||!BoothSessionWorkspace.Contains(context.BoothSession,path))return;
            try{if(File.Exists(path))File.Delete(path);}catch(Exception e){log.LogDebug(e,"Không thể xóa ảnh preset tạm {Path}",path);}
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
            _ = StartPreviewOperation();
        }

        Task StartPreviewOperation()
        {
            Task task;
            lock (operationSync)
            {
                if (pageStopping) return Task.CompletedTask;
                task = UpdatePreview();
                previewTask = task;
                previewOperations.Add(task);
            }
            return ObservePreview(task);
        }

        async Task ObservePreview(Task task)
        {
            try { await task; }
            finally { lock (operationSync) previewOperations.Remove(task); }
        }

        public async Task ShutdownAsync()
        {
            Task[] previews;
            Task[] presetTasks;
            Task loading;
            Task finishing;
            Task applyingPreset;
            lock (operationSync)
            {
                pageStopping = true;
                previews = previewOperations.ToArray();
                presetTasks = presetOperations.ToArray();
                loading = loadTask;
                finishing = finishTask;
                applyingPreset = presetApplyTask;
            }
            CancelStagedPresetPreview();CancelPresetBackgroundWork();
            lock (previewCancellationSync) previewCancellation?.Cancel();
            try { await Task.WhenAll(previews.Concat(presetTasks).Concat(new[] { previewTask, loading, presetPreviewTask, presetThumbnailTask, applyingPreset })); }
            catch (OperationCanceledException) { }
            await finishing;
        }

        async Task Compose(bool final, CancellationToken token)
        {
            await composeGate.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested) return;
                if (context.BoothSession == null || SelectedFrame == null) return;
                if (final && !CanFinish) throw new InvalidOperationException("Hãy đặt ảnh vào tất cả các ô trước khi tiếp tục.");
                var all = await presets.GetAllAsync(token);
                context.DefaultPreset = context.Settings?.DefaultPresetId.HasValue == true ? all.FirstOrDefault(x => x.Id == context.Settings.DefaultPresetId.Value) : all.FirstOrDefault(x => x.IsDefault);
                var sourceFiles = GetSourceFiles();
                var workingDirectory = string.IsNullOrWhiteSpace(context.WorkingDirectory) ? BoothSessionWorkspace.GetPath(context.BoothSession) : context.WorkingDirectory;
                Directory.CreateDirectory(workingDirectory);
                var working = new Session { Id = context.BoothSession.Id, StartedAtUtc = context.BoothSession.StartedAtUtc, OutputDirectory = workingDirectory, SessionNumber = context.BoothSession.SessionNumber, FrameIndex = context.BoothSession.FrameIndex, CapturedFiles = sourceFiles };
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
                    if (!string.IsNullOrWhiteSpace(oldPreview) && oldPreview != composed && BoothSessionWorkspace.Contains(context.BoothSession, oldPreview) && File.Exists(oldPreview)) File.Delete(oldPreview);
                    return;
                }
                context.BoothSession.FrameIndex = working.FrameIndex; context.BoothSession.FinalImageId = working.FinalImageId;
                var currentShots = (context.CurrentShots??new List<CapturedShot>()).ToList();
                var promoted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sourceFiles) promoted[source] = BoothSessionWorkspace.PromoteOriginal(context.BoothSession, source);
                foreach (var source in context.CurrentShots.Where(x=>x.HasVideo).Select(x=>x.VideoPath).Where(File.Exists)) promoted[source] = BoothSessionWorkspace.PromoteOriginal(context.BoothSession, source);
                var finalComposite = BoothSessionWorkspace.PromoteFinal(context.BoothSession, composed);
                BoothSessionWorkspace.ReplaceWorkspaceFiles(context.BoothSession, promoted);
                context.CurrentShots = currentShots.Select(x=>new CapturedShot{Id=x.Id,Sequence=x.Sequence,PicturePath=promoted.TryGetValue(x.PicturePath,out var picture)?picture:x.PicturePath,VideoPath=x.HasVideo&&promoted.TryGetValue(x.VideoPath,out var video)?video:x.VideoPath,PictureAssetId=x.PictureAssetId,VideoAssetId=x.VideoAssetId,CapturedAtUtc=x.CapturedAtUtc}).ToList(); sourceFiles = context.CurrentShots.Select(x=>x.PicturePath).ToList();
                PreviewPath = finalComposite; context.BoothSession.FinalImagePath = finalComposite; await sessions.UpdateAsync(context.BoothSession, token);
                log.LogInformation("Frame selected {Frame} ({ImageId}, originals {OriginalCount})", SelectedFrame.Name, context.BoothSession.FinalImageId, sourceFiles.Count);
            }
            finally { composeGate.Release(); }
        }

        List<string> GetSourceFiles()
        {
            var current = context.CurrentShots ?? new List<CapturedShot>();
            var sessionShots = context.BoothSession?.CapturedShots ?? new CapturedShot[0];
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
                ErrorMessage = null; IsPrinting = true;
                lock (previewCancellationSync) previewCancellation?.Cancel();
                if (context.BoothSession != null && context.BoothSession.IsBoothSession && context.BoothSession.Status != BoothSessionStates.Finalizing)
                {
                    context.BoothSession.Status = BoothSessionStates.Finalizing;
                    await sessions.UpdateAsync(context.BoothSession, CancellationToken.None);
                }
                var transformedFrame = FrameWithTransforms();
                var slotShotIds = FrameSlots.ToDictionary(
                    x => x.Slot.Index,
                    x => FindShot(x.Photo?.PicturePath)?.Id);
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
                    var still = context.BoothSession?.FinalImagePath;
                    if (string.IsNullOrWhiteSpace(still) || !File.Exists(still))
                        throw new FileNotFoundException("Ảnh ghép cuối không còn khả dụng.", still);
                    var destination = Path.Combine(Path.GetDirectoryName(still), Guid.NewGuid().ToString("N") + ".mp4");
                    await videos.ComposeAsync(still, transformedFrame, assignments, destination, CancellationToken.None);
                    var days = context.Settings?.BoothSessionRetentionDays ?? 30;
                    var expires = days > 0 ? (DateTime?)DateTime.UtcNow.AddDays(days) : null;
                    var deliverable = await deliverables.CreateWithCompositeVideoAsync(
                        context.BoothSession.Id, SelectedFrame.Id, context.BoothSession.FinalImageId, still,
                        context.CurrentShots, destination, assignments.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        expires, CancellationToken.None);
                    context.DeliverableId = deliverable.Id;
                }
                else if (string.IsNullOrWhiteSpace(context.DeliverableId))
                {
                    var days = context.Settings?.BoothSessionRetentionDays ?? 30; var expires = days > 0 ? (DateTime?)DateTime.UtcNow.AddDays(days) : null;
                    var deliverable = await deliverables.CreateAsync(context.BoothSession.Id, SelectedFrame.Id, context.BoothSession.FinalImageId, context.BoothSession.FinalImagePath, context.CurrentShots, expires, CancellationToken.None); context.DeliverableId = deliverable.Id;
                }
                machine.MoveTo(CustomerWorkflowState.Printing);
                if (context.PrintingEnabled)
                {
                    var profiles = await printers.GetProfilesAsync(CancellationToken.None); var profile = profiles.SingleOrDefault(x => x.IsDefault);
                    if (profile == null || !string.Equals(profile.PrinterId, context.ConnectedPrinterId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Default printer changed. Reconnect the printer.");
                    await printPipeline.ExecuteAsync(context.BoothSession.Id, profile.Id, Math.Max(1, PrintCopies), CancellationToken.None);
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
