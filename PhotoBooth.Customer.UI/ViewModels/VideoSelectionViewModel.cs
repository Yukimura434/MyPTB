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
    public sealed class VideoSelectionViewModel : ObservableObject
    {
        readonly CustomerWorkflowStateMachine machine; readonly CustomerWorkflowContext context;
        readonly IImageCompositionService composer; readonly IVideoService videos;
        readonly ICaptureService captures; readonly IPrinterService printers; readonly IPrintPipeline printPipeline;
        readonly ILogger<VideoSelectionViewModel> log; readonly AsyncCommand finishCommand;
        readonly object lifecycleSync = new object();
        CancellationTokenSource lifecycleCts = new CancellationTokenSource();
        Task loadTask = Task.CompletedTask;
        Task finishTask = Task.CompletedTask;
        int lifecycleGeneration;
        FrameSlotChoice selectedSlot; CapturedPhotoChoice selectedPhoto; string error; bool busy; int copies = 1;

        public VideoSelectionViewModel(CustomerWorkflowStateMachine machine, CustomerWorkflowContext context,
            IImageCompositionService composer, IVideoService videos, ICaptureService captures,
            IPrinterService printers, IPrintPipeline printPipeline, ILogger<VideoSelectionViewModel> log)
        {
            this.machine=machine;this.context=context;this.composer=composer;this.videos=videos;this.captures=captures;this.printers=printers;this.printPipeline=printPipeline;this.log=log;
            SelectSlotCommand=new ParameterCommand(x=>SelectSlot(x as FrameSlotChoice)); SelectPhotoCommand=new ParameterCommand(x=>SelectPhoto(x as CapturedPhotoChoice));
            ClearSlotCommand=new RelayCommand(ClearSelectedSlot);
            BackCommand=new RelayCommand(BackToFrameSelection);
            IncreaseCopiesCommand=new RelayCommand(()=>PrintCopies++);DecreaseCopiesCommand=new RelayCommand(()=>PrintCopies--);
            finishCommand=new AsyncCommand(BeginFinish,()=>CanFinish&&!IsBusy);FinishCommand=finishCommand;
            machine.StateChanged+=(s,e)=>{if(machine.State==CustomerWorkflowState.VideoSelection)BeginLoad();};
            if(machine.State==CustomerWorkflowState.VideoSelection)BeginLoad();
        }
        public ObservableCollection<FrameSlotChoice> FrameSlots{get;}=new ObservableCollection<FrameSlotChoice>();
        public ObservableCollection<CapturedPhotoChoice> Videos{get;}=new ObservableCollection<CapturedPhotoChoice>();
        public Frame SelectedFrame=>context.SelectedFrame;
        public FrameSlotChoice SelectedSlot{get=>selectedSlot;private set=>Set(ref selectedSlot,value);}
        public CapturedPhotoChoice SelectedPhoto{get=>selectedPhoto;private set=>Set(ref selectedPhoto,value);}
        public string ErrorMessage{get=>error;private set{Set(ref error,value);Raise(nameof(HasError));}}
        public bool HasError=>!string.IsNullOrWhiteSpace(ErrorMessage);
        public bool IsBusy{get=>busy;private set{Set(ref busy,value);finishCommand.NotifyCanExecuteChanged();}}
        public int PrintCopies{get=>copies;set=>Set(ref copies,Math.Max(1,Math.Min(99,value)));}
        public bool CanFinish=>FrameSlots.Count>0&&FrameSlots.All(x=>x.Photo!=null);
        public string AssignmentStatus=>FrameSlots.Count==0?string.Empty:FrameSlots.Count(x=>x.Photo!=null)+" / "+FrameSlots.Count+" ô đã có video";
        public string Guidance=>SelectedSlot==null?"Chọn một ô trên frame":SelectedPhoto==null?"Đã chọn ô "+SelectedSlot.Number+" — chọn video bên phải":"Video "+SelectedPhoto.Number+" đang được chọn — chạm ô để đặt";
        public ICommand SelectSlotCommand{get;} public ICommand SelectPhotoCommand{get;} public ICommand ClearSlotCommand{get;} public ICommand BackCommand{get;} public ICommand FinishCommand{get;} public ICommand IncreaseCopiesCommand{get;} public ICommand DecreaseCopiesCommand{get;}

        void BeginLoad()
        {
            CancellationToken token; int generation;
            lock(lifecycleSync)
            {
                lifecycleCts.Cancel(); lifecycleCts.Dispose(); lifecycleCts=new CancellationTokenSource();
                token=lifecycleCts.Token; generation=++lifecycleGeneration;
                loadTask=Load(token,generation);
            }
        }
        Task Load(CancellationToken token,int generation)
        {
            try
            {
                IsBusy=true;ErrorMessage=null;FrameSlots.Clear();Videos.Clear();PrintCopies=1;
                if(SelectedFrame==null)throw new InvalidOperationException("Frame đã chọn không còn khả dụng.");
                var shots=(context.CurrentShots??new List<CapturedShot>()).Where(x=>x.HasVideo&&File.Exists(x.VideoPath)&&File.Exists(x.PicturePath)).ToList();
                if(shots.Count==0)throw new InvalidOperationException("Lượt chụp hiện tại không có cặp ảnh và video hợp lệ. Hãy quay lại và chụp lại ảnh.");
                for(var i=0;i<shots.Count;i++){token.ThrowIfCancellationRequested();if(generation!=lifecycleGeneration)return Task.CompletedTask;Videos.Add(new CapturedPhotoChoice(shots[i].VideoPath,i+1,shots[i].PicturePath));}
                foreach(var slot in SelectedFrame.Slots.OrderBy(x=>x.Index))FrameSlots.Add(new FrameSlotChoice(slot));
                for(var i=0;i<FrameSlots.Count&&Videos.Count>0;i++)FrameSlots[i].Photo=Videos[i%Videos.Count];
                SelectedSlot=FrameSlots.FirstOrDefault();SelectedPhoto=null;Selection();Changed();
            }catch(OperationCanceledException) when(token.IsCancellationRequested){}
            catch(Exception e){if(generation==lifecycleGeneration)Fail(e,"Không thể tải video");}
            finally{if(generation==lifecycleGeneration)IsBusy=false;}
            return Task.CompletedTask;
        }
        void SelectSlot(FrameSlotChoice value){if(value==null||IsBusy)return;SelectedSlot=value;if(SelectedPhoto!=null)value.Photo=SelectedPhoto;Selection();Changed();}
        void SelectPhoto(CapturedPhotoChoice value){if(value==null||IsBusy)return;SelectedPhoto=value;if(SelectedSlot!=null)SelectedSlot.Photo=value;Selection();Changed();}
        void ClearSelectedSlot(){if(SelectedSlot==null||IsBusy)return;SelectedSlot.Photo=null;Changed();}
        void BackToFrameSelection(){lock(lifecycleSync){++lifecycleGeneration;lifecycleCts.Cancel();}machine.MoveTo(CustomerWorkflowState.FrameSelection);}
        void Selection(){foreach(var x in FrameSlots)x.IsSelected=x==SelectedSlot;foreach(var x in Videos)x.IsSelected=x==SelectedPhoto;Raise(nameof(Guidance));}
        void Changed(){Raise(nameof(CanFinish));Raise(nameof(AssignmentStatus));Raise(nameof(Guidance));finishCommand.NotifyCanExecuteChanged();}
        public async Task ResetAsync()
        {
            Task pendingLoad,pendingFinish;
            lock(lifecycleSync)
            {
                ++lifecycleGeneration;lifecycleCts.Cancel();pendingLoad=loadTask;pendingFinish=finishTask;
            }
            try{await Task.WhenAll(pendingLoad,pendingFinish);}catch(OperationCanceledException){}
            FrameSlots.Clear();Videos.Clear();SelectedSlot=null;SelectedPhoto=null;ErrorMessage=null;IsBusy=false;Changed();
        }
        Task BeginFinish()
        {
            CancellationToken token;int generation;
            lock(lifecycleSync){token=lifecycleCts.Token;generation=lifecycleGeneration;}
            var task=Finish(token,generation);
            lock(lifecycleSync)finishTask=task;
            return task;
        }
        async Task Finish(CancellationToken token,int generation)
        {
            try
            {
                IsBusy=true;ErrorMessage=null;if(!CanFinish)throw new InvalidOperationException("Hãy đặt video vào tất cả các ô.");
                var assignments=FrameSlots.ToDictionary(x=>x.Slot.Index,x=>x.Photo.Path);
                var pictureAssignments=FrameSlots.ToDictionary(x=>x.Slot.Index,x=>x.Photo.PicturePath);
                var still=context.Session?.FinalImagePath;if(string.IsNullOrWhiteSpace(still)||!File.Exists(still))throw new FileNotFoundException("Ảnh composite tĩnh không còn khả dụng.",still);
                var destination=Path.Combine(Path.GetDirectoryName(still),Path.GetFileNameWithoutExtension(still)+".mp4");
                var rawWorking=new Session{Id=context.Session.Id,StartedAtUtc=context.Session.StartedAtUtc,OutputDirectory=context.WorkingDirectory??context.Session.OutputDirectory,SessionNumber=context.Session.SessionNumber,FrameIndex=context.Session.FrameIndex};
                var transformedFrame=FrameWithTransforms();
                var rawStill=await composer.ComposeAsync(rawWorking,transformedFrame,null,false,pictureAssignments,token);
                try{await videos.ComposeAsync(rawStill,transformedFrame,assignments,destination,token);}
                finally{try{if(File.Exists(rawStill))File.Delete(rawStill);}catch{}}
                token.ThrowIfCancellationRequested();if(generation!=lifecycleGeneration)return;
                if(string.IsNullOrWhiteSpace(context.CaptureId))
                {
                    var days=context.Settings?.SessionRetentionDays??30;var expires=days>0?(DateTime?)DateTime.UtcNow.AddDays(days):null;
                    var capture=await captures.CreateWithCompositeVideoAsync(context.Session.Id,SelectedFrame.Id,context.Session.FinalImageId,still,context.CurrentShots,destination,assignments.Values.ToList(),expires,token);context.CaptureId=capture.Id;
                }
                machine.MoveTo(CustomerWorkflowState.Printing);
                if(context.PrintingEnabled){var profiles=await printers.GetProfilesAsync(token);var profile=profiles.SingleOrDefault(x=>x.IsDefault);if(profile==null||!string.Equals(profile.PrinterId,context.ConnectedPrinterId,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Máy in mặc định đã thay đổi.");await printPipeline.ExecuteAsync(context.Session.Id,profile.Id,PrintCopies,token);}
                machine.MoveTo(CustomerWorkflowState.Complete);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){}
            catch(Exception e){if(machine.State==CustomerWorkflowState.Printing)machine.MoveTo(CustomerWorkflowState.VideoSelection);Fail(e,e.Message);}
            finally{if(generation==lifecycleGeneration)IsBusy=false;}
        }
        void Fail(Exception e,string message){log.LogError(e,message);ErrorMessage=message;}
        Frame FrameWithTransforms()=>new Frame{Id=SelectedFrame.Id,Name=SelectedFrame.Name,SourcePath=SelectedFrame.SourcePath,ThumbnailPath=SelectedFrame.ThumbnailPath,
            PixelWidth=SelectedFrame.PixelWidth,PixelHeight=SelectedFrame.PixelHeight,IsPinned=SelectedFrame.IsPinned,CreatedAtUtc=SelectedFrame.CreatedAtUtc,
            EventId=SelectedFrame.EventId,Slots=FrameSlots.Select(x=>x.Slot).ToList()};
    }
}
