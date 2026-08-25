using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;
using MediaColor = System.Windows.Media.Color;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class LiveColorState : ObservableObject
    {
        readonly IFeatureFlagService features;readonly IColorLutService colors;readonly ILogger<LiveColorState> log;
        bool enabled;float[] values;int size;double strength=1;
        public LiveColorState(IFeatureFlagService featureFlags,IColorLutService colorLuts,ILogger<LiveColorState> logger){features=featureFlags;colors=colorLuts;log=logger;}
        public bool IsEnabled{get=>enabled;private set=>Set(ref enabled,value);}public float[] Values{get=>values;private set=>Set(ref values,value);}public int Size{get=>size;private set=>Set(ref size,value);}public double Strength{get=>strength;private set=>Set(ref strength,value);}
        public MediaColor DomainMin{get;private set;}=MediaColor.FromScRgb(1,0,0,0);public MediaColor DomainMax{get;private set;}=MediaColor.FromScRgb(1,1,1,1);
        public async Task RefreshAsync(Settings settings,CancellationToken token)
        {
            Disable(null);
            try
            {
                if(!await features.IsEnabledAsync("ColorGpuLiveView",token))return;
                if(await features.IsEnabledAsync("ColorGpuDiagnosticMonochrome",token)){Values=CreateMonochrome();Size=2;Strength=1;IsEnabled=true;log.LogWarning("GPU live color diagnostic monochrome LUT is active");return;}
                if(settings==null||!settings.DefaultPresetId.HasValue)return;
                using(var data=await colors.GetLiveAsync(settings.DefaultPresetId.Value,token))
                {
                    if(data==null||data.Metadata==null||data.Values==null)return;Values=data.Values;Size=data.Metadata.CubeSize;DomainMin=MediaColor.FromScRgb(1,data.Metadata.DomainMinR,data.Metadata.DomainMinG,data.Metadata.DomainMinB);DomainMax=MediaColor.FromScRgb(1,data.Metadata.DomainMaxR,data.Metadata.DomainMaxG,data.Metadata.DomainMaxB);Strength=data.Strength;Raise(nameof(DomainMin));Raise(nameof(DomainMax));IsEnabled=true;
                }
            }
            catch(Exception error){log.LogWarning(error,"GPU live color initialization failed; JPEG/WPF fallback remains active");Disable(null);}
        }
        public void Disable(Exception error){if(error!=null)log.LogError(error,"GPU live color failed; continuing with JPEG/WPF fallback");IsEnabled=false;Values=null;Size=0;}
        static float[] CreateMonochrome(){var result=new float[24];var i=0;for(var b=0;b<2;b++)for(var g=0;g<2;g++)for(var r=0;r<2;r++){var gray=.2126f*r+.7152f*g+.0722f*b;result[i++]=gray;result[i++]=gray;result[i++]=gray;}return result;}
    }
}
