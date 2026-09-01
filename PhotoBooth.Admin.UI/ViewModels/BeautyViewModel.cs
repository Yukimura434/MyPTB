using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class BeautyViewModel : PageViewModel
    {
        readonly IBeautySettingsService service; readonly ILogger<BeautyViewModel> log;
        bool enabled,busy,loading,hasUnsavedChanges;int smooth,brighten,tone,sharpen,eyeSize,slimFace;string message,runtimeStatus;
        public BeautyViewModel(IBeautySettingsService settings,ILogger<BeautyViewModel> logger){service=settings;log=logger;SaveCommand=new AsyncCommand(_=>Save());ResetCommand=new AsyncCommand(_=>Reset());ReloadCommand=new AsyncCommand(_=>Load());_=Load();}
        public override string Title=>"Beauty";
        public bool Enabled{get=>enabled;set{if(Set(ref enabled,value)){Raise(nameof(IsEditorEnabled));MarkDirty();}}} public bool IsBusy{get=>busy;private set=>Set(ref busy,value);}
        public bool IsEditorEnabled=>Enabled&&!IsBusy;
        public bool HasUnsavedChanges{get=>hasUnsavedChanges;private set=>Set(ref hasUnsavedChanges,value);}
        public int SmoothSkin{get=>smooth;set{if(Set(ref smooth,Clamp(value)))MarkDirty();}} public int BrightenSkin{get=>brighten;set{if(Set(ref brighten,Clamp(value)))MarkDirty();}}
        public int SkinTone{get=>tone;set{if(Set(ref tone,Clamp(value)))MarkDirty();}} public int Sharpen{get=>sharpen;set{if(Set(ref sharpen,Clamp(value)))MarkDirty();}}
        public int EyeSize{get=>eyeSize;set{if(Set(ref eyeSize,Clamp(value)))MarkDirty();}} public int SlimFace{get=>slimFace;set{if(Set(ref slimFace,Clamp(value)))MarkDirty();}}
        public string Message{get=>message;private set=>Set(ref message,value);} public string RuntimeStatus{get=>runtimeStatus;private set=>Set(ref runtimeStatus,value);}
        public ICommand SaveCommand{get;} public ICommand ResetCommand{get;} public ICommand ReloadCommand{get;}
        public Task RefreshAsync()=>Load();
        async Task Load(){IsBusy=true;loading=true;try{Apply(await service.GetAsync(CancellationToken.None));HasUnsavedChanges=false;var assets=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","Beauty");RuntimeStatus=File.Exists(Path.Combine(assets,"frontface.xml"))&&File.Exists(Path.Combine(assets,"lbfmodel.yaml"))?"OpenCV model assets: ready":"OpenCV model assets: unavailable (capture remains fail-open)";Message=null;}catch(Exception e){Fail(e,"Không thể tải cấu hình Beauty");}finally{loading=false;IsBusy=false;Raise(nameof(IsEditorEnabled));}}
        async Task Save(){IsBusy=true;Raise(nameof(IsEditorEnabled));try{await service.SaveAsync(Current(),CancellationToken.None);HasUnsavedChanges=false;Message=Enabled?"Đã bật Beauty; Live View áp dụng ngay.":"Đã tắt Beauty; Live View trở về ảnh gốc ngay.";}catch(Exception e){Fail(e,"Không thể lưu cấu hình Beauty");}finally{IsBusy=false;Raise(nameof(IsEditorEnabled));}}
        async Task Reset(){Enabled=false;SmoothSkin=BrightenSkin=SkinTone=Sharpen=EyeSize=SlimFace=0;await Save();}
        BeautySettings Current()=>new BeautySettings{Enabled=Enabled,SmoothSkin=SmoothSkin,BrightenSkin=BrightenSkin,SkinTone=SkinTone,Sharpen=Sharpen,EyeSize=EyeSize,SlimFace=SlimFace};
        void Apply(BeautySettings x){x=x??new BeautySettings();Enabled=x.Enabled;SmoothSkin=x.SmoothSkin;BrightenSkin=x.BrightenSkin;SkinTone=x.SkinTone;Sharpen=x.Sharpen;EyeSize=x.EyeSize;SlimFace=x.SlimFace;}
        void MarkDirty(){if(loading)return;HasUnsavedChanges=true;Message="Có thay đổi chưa lưu; nhấn Lưu để áp dụng cho ảnh chụp và Live View.";}
        void Fail(Exception e,string text){log.LogError(e,text);Message=text;} static int Clamp(int x)=>Math.Max(0,Math.Min(100,x));
    }
}
