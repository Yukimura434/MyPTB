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
        bool enabled,busy;int smooth,brighten,tone,sharpen,eyeSize,slimFace;string message,runtimeStatus;
        public BeautyViewModel(IBeautySettingsService settings,ILogger<BeautyViewModel> logger){service=settings;log=logger;SaveCommand=new AsyncCommand(_=>Save());ResetCommand=new AsyncCommand(_=>Reset());ReloadCommand=new AsyncCommand(_=>Load());_=Load();}
        public override string Title=>"Beauty";
        public bool Enabled{get=>enabled;set=>Set(ref enabled,value);} public bool IsBusy{get=>busy;private set=>Set(ref busy,value);}
        public int SmoothSkin{get=>smooth;set=>Set(ref smooth,Clamp(value));} public int BrightenSkin{get=>brighten;set=>Set(ref brighten,Clamp(value));}
        public int SkinTone{get=>tone;set=>Set(ref tone,Clamp(value));} public int Sharpen{get=>sharpen;set=>Set(ref sharpen,Clamp(value));}
        public int EyeSize{get=>eyeSize;set=>Set(ref eyeSize,Clamp(value));} public int SlimFace{get=>slimFace;set=>Set(ref slimFace,Clamp(value));}
        public string Message{get=>message;private set=>Set(ref message,value);} public string RuntimeStatus{get=>runtimeStatus;private set=>Set(ref runtimeStatus,value);}
        public ICommand SaveCommand{get;} public ICommand ResetCommand{get;} public ICommand ReloadCommand{get;}
        async Task Load(){IsBusy=true;try{Apply(await service.GetAsync(CancellationToken.None));var assets=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","Beauty");RuntimeStatus=File.Exists(Path.Combine(assets,"frontface.xml"))&&File.Exists(Path.Combine(assets,"lbfmodel.yaml"))?"OpenCV model assets: ready":"OpenCV model assets: unavailable (capture remains fail-open)";Message=null;}catch(Exception e){Fail(e,"Không thể tải cấu hình Beauty");}finally{IsBusy=false;}}
        async Task Save(){IsBusy=true;try{await service.SaveAsync(Current(),CancellationToken.None);Message="Đã lưu cấu hình Beauty";}catch(Exception e){Fail(e,"Không thể lưu cấu hình Beauty");}finally{IsBusy=false;}}
        async Task Reset(){Enabled=false;SmoothSkin=BrightenSkin=SkinTone=Sharpen=EyeSize=SlimFace=0;await Save();}
        BeautySettings Current()=>new BeautySettings{Enabled=Enabled,SmoothSkin=SmoothSkin,BrightenSkin=BrightenSkin,SkinTone=SkinTone,Sharpen=Sharpen,EyeSize=EyeSize,SlimFace=SlimFace};
        void Apply(BeautySettings x){x=x??new BeautySettings();Enabled=x.Enabled;SmoothSkin=x.SmoothSkin;BrightenSkin=x.BrightenSkin;SkinTone=x.SkinTone;Sharpen=x.Sharpen;EyeSize=x.EyeSize;SlimFace=x.SlimFace;}
        void Fail(Exception e,string text){log.LogError(e,text);Message=text;} static int Clamp(int x)=>Math.Max(0,Math.Min(100,x));
    }
}
