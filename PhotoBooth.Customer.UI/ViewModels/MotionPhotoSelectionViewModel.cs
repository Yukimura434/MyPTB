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
    public sealed class MotionPhotoSelectionViewModel : ObservableObject
    {
        readonly CustomerWorkflowStateMachine machine; readonly CustomerWorkflowContext context;
        readonly IImageCompositionService composer; readonly IMotionPhotoService motionPhotos;
        readonly ICaptureService captures; readonly IPrinterService printers; readonly IPrintPipeline printPipeline;
        readonly ILogger<MotionPhotoSelectionViewModel> log; readonly AsyncCommand finishCommand;
        FrameSlotChoice selectedSlot; CapturedPhotoChoice selectedPhoto; string previewPath; string error; bool busy; int copies = 1;

        public MotionPhotoSelectionViewModel(CustomerWorkflowStateMachine machine, CustomerWorkflowContext context,
            IImageCompositionService composer, IMotionPhotoService motionPhotos, ICaptureService captures,
            IPrinterService printers, IPrintPipeline printPipeline, ILogger<MotionPhotoSelectionViewModel> log)
        {
            this.machine=machine;this.context=context;this.composer=composer;this.motionPhotos=motionPhotos;this.captures=captures;this.printers=printers;this.printPipeline=printPipeline;this.log=log;
            SelectSlotCommand=new ParameterCommand(x=>SelectSlot(x as FrameSlotChoice)); SelectPhotoCommand=new ParameterCommand(x=>SelectPhoto(x as CapturedPhotoChoice));
            ClearSlotCommand=new RelayCommand(()=>{if(SelectedSlot!=null){SelectedSlot.Photo=null;Changed();_=Preview();}});
            BackCommand=new RelayCommand(()=>machine.MoveTo(CustomerWorkflowState.FrameSelection));
            IncreaseCopiesCommand=new RelayCommand(()=>PrintCopies++);DecreaseCopiesCommand=new RelayCommand(()=>PrintCopies--);
            finishCommand=new AsyncCommand(Finish,()=>CanFinish&&!IsBusy);FinishCommand=finishCommand;
            machine.StateChanged+=(s,e)=>{if(machine.State==CustomerWorkflowState.MotionPhotoSelection)_=Load();};
            if(machine.State==CustomerWorkflowState.MotionPhotoSelection)_=Load();
        }
        public ObservableCollection<FrameSlotChoice> FrameSlots{get;}=new ObservableCollection<FrameSlotChoice>();
        public ObservableCollection<CapturedPhotoChoice> MotionPhotos{get;}=new ObservableCollection<CapturedPhotoChoice>();
        public Frame SelectedFrame=>context.SelectedFrame;
        public FrameSlotChoice SelectedSlot{get=>selectedSlot;private set=>Set(ref selectedSlot,value);}
        public CapturedPhotoChoice SelectedPhoto{get=>selectedPhoto;private set=>Set(ref selectedPhoto,value);}
        public string PreviewPath{get=>previewPath;private set=>Set(ref previewPath,value);}
        public string ErrorMessage{get=>error;private set{Set(ref error,value);Raise(nameof(HasError));}}
        public bool HasError=>!string.IsNullOrWhiteSpace(ErrorMessage);
        public bool IsBusy{get=>busy;private set{Set(ref busy,value);finishCommand.NotifyCanExecuteChanged();}}
        public int PrintCopies{get=>copies;set=>Set(ref copies,Math.Max(1,Math.Min(99,value)));}
        public bool CanFinish=>FrameSlots.Count>0&&FrameSlots.All(x=>x.Photo!=null);
        public string AssignmentStatus=>FrameSlots.Count==0?string.Empty:FrameSlots.Count(x=>x.Photo!=null)+" / "+FrameSlots.Count+" ô đã có Motion Photo";
        public string Guidance=>SelectedSlot==null?"Chọn một ô trên frame":SelectedPhoto==null?"Đã chọn ô "+SelectedSlot.Number+" — chọn Motion Photo bên phải":"Motion Photo "+SelectedPhoto.Number+" đang được chọn — chạm ô để đặt";
        public ICommand SelectSlotCommand{get;} public ICommand SelectPhotoCommand{get;} public ICommand ClearSlotCommand{get;} public ICommand BackCommand{get;} public ICommand FinishCommand{get;} public ICommand IncreaseCopiesCommand{get;} public ICommand DecreaseCopiesCommand{get;}

        async Task Load()
        {
            try
            {
                ErrorMessage=null;FrameSlots.Clear();MotionPhotos.Clear();PrintCopies=1;
                if(SelectedFrame==null)throw new InvalidOperationException("Frame đã chọn không còn khả dụng.");
                var files=(context.CurrentShots??new List<CapturedShot>()).Where(x=>x.HasMotionPhoto).Select(x=>x.MotionPhotoPath).Where(File.Exists).ToList();
                if(files.Count==0)throw new InvalidOperationException("Lượt chụp hiện tại không có Motion Photo hợp lệ. Hãy quay lại và chụp lại ảnh.");
                var previewDirectory=Path.Combine(context.WorkingDirectory??context.Session.OutputDirectory,"MotionPreview");
                for(var i=0;i<files.Count;i++){var video=await motionPhotos.CreatePreviewVideoAsync(files[i],previewDirectory,CancellationToken.None);MotionPhotos.Add(new CapturedPhotoChoice(files[i],i+1,video));}
                foreach(var slot in SelectedFrame.Slots.OrderBy(x=>x.Index))FrameSlots.Add(new FrameSlotChoice(slot));
                for(var i=0;i<FrameSlots.Count&&MotionPhotos.Count>0;i++)FrameSlots[i].Photo=MotionPhotos[i%MotionPhotos.Count];
                SelectedSlot=FrameSlots.FirstOrDefault();SelectedPhoto=null;Changed();await Preview();
            }catch(Exception e){Fail(e,"Không thể tải Motion Photo");}
        }
        void SelectSlot(FrameSlotChoice value){if(value==null||IsBusy)return;SelectedSlot=value;if(SelectedPhoto!=null)value.Photo=SelectedPhoto;Selection();Changed();_=Preview();}
        void SelectPhoto(CapturedPhotoChoice value){if(value==null||IsBusy)return;SelectedPhoto=value;if(SelectedSlot!=null)SelectedSlot.Photo=value;Selection();Changed();_=Preview();}
        void Selection(){foreach(var x in FrameSlots)x.IsSelected=x==SelectedSlot;foreach(var x in MotionPhotos)x.IsSelected=x==SelectedPhoto;Raise(nameof(Guidance));}
        void Changed(){Raise(nameof(CanFinish));Raise(nameof(AssignmentStatus));Raise(nameof(Guidance));finishCommand.NotifyCanExecuteChanged();}
        async Task Preview()
        {
            if(context.Session==null||SelectedFrame==null)return;
            var map=FrameSlots.Where(x=>x.Photo!=null).ToDictionary(x=>x.Slot.Index,x=>x.Photo.Path);
            var working=new Session{Id=context.Session.Id,StartedAtUtc=context.Session.StartedAtUtc,OutputDirectory=context.WorkingDirectory??context.Session.OutputDirectory,SessionNumber=context.Session.SessionNumber,FrameIndex=context.Session.FrameIndex,CapturedShots=context.CurrentShots,CapturedFiles=context.CurrentShots.Select(x=>x.PicturePath).ToList()};
            var next=await composer.ComposeAsync(working,SelectedFrame,context.DefaultPreset,false,map,CancellationToken.None);
            var old=PreviewPath;PreviewPath=next;try{if(!string.IsNullOrWhiteSpace(old)&&old!=next&&File.Exists(old))File.Delete(old);}catch{}
        }
        async Task Finish()
        {
            try
            {
                IsBusy=true;ErrorMessage=null;if(!CanFinish)throw new InvalidOperationException("Hãy đặt Motion Photo vào tất cả các ô.");
                var assignments=FrameSlots.ToDictionary(x=>x.Slot.Index,x=>x.Photo.Path);
                var still=context.Session?.FinalImagePath;if(string.IsNullOrWhiteSpace(still)||!File.Exists(still))throw new FileNotFoundException("Ảnh composite tĩnh không còn khả dụng.",still);
                var destination=Path.Combine(Path.GetDirectoryName(still),Path.GetFileNameWithoutExtension(still)+"_MP.jpg");
                var rawWorking=new Session{Id=context.Session.Id,StartedAtUtc=context.Session.StartedAtUtc,OutputDirectory=context.WorkingDirectory??context.Session.OutputDirectory,SessionNumber=context.Session.SessionNumber,FrameIndex=context.Session.FrameIndex};
                var rawStill=await composer.ComposeAsync(rawWorking,SelectedFrame,null,false,assignments,CancellationToken.None);
                try{await motionPhotos.ComposeAsync(rawStill,SelectedFrame,assignments,destination,CancellationToken.None);}
                finally{try{if(File.Exists(rawStill))File.Delete(rawStill);}catch{}}
                if(string.IsNullOrWhiteSpace(context.CaptureId))
                {
                    var days=context.Settings?.SessionRetentionDays??30;var expires=days>0?(DateTime?)DateTime.UtcNow.AddDays(days):null;
                    var capture=await captures.CreateWithMotionCompositeAsync(context.Session.Id,SelectedFrame.Id,context.Session.FinalImageId,still,context.CurrentShots,destination,assignments.Values.ToList(),expires,CancellationToken.None);context.CaptureId=capture.Id;
                }
                machine.MoveTo(CustomerWorkflowState.Printing);
                if(context.PrintingEnabled){var profiles=await printers.GetProfilesAsync(CancellationToken.None);var profile=profiles.SingleOrDefault(x=>x.IsDefault);if(profile==null||!string.Equals(profile.PrinterId,context.ConnectedPrinterId,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Máy in mặc định đã thay đổi.");await printPipeline.ExecuteAsync(context.Session.Id,profile.Id,PrintCopies,CancellationToken.None);}
                machine.MoveTo(CustomerWorkflowState.Complete);
            }
            catch(Exception e){if(machine.State==CustomerWorkflowState.Printing)machine.MoveTo(CustomerWorkflowState.MotionPhotoSelection);Fail(e,e.Message);}
            finally{IsBusy=false;}
        }
        void Fail(Exception e,string message){log.LogError(e,message);ErrorMessage=message;}
    }
}
