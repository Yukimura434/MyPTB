using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.ViewModels;

namespace PhotoBooth.Admin.UI.Views
{
 public partial class HomeView:UserControl
 {
  INotifyPropertyChanged observed;
  public HomeView(){InitializeComponent();DataContextChanged+=(s,e)=>Observe(e.NewValue as INotifyPropertyChanged);}
  void Observe(INotifyPropertyChanged value){if(observed!=null)observed.PropertyChanged-=OnHomeChanged;observed=value;if(observed!=null)observed.PropertyChanged+=OnHomeChanged;ApplyReportedSize();}
  void OnHomeChanged(object sender,PropertyChangedEventArgs e){if(e.PropertyName=="Resolution")ApplyReportedSize();}
  void ApplyReportedSize(){var vm=DataContext as ViewModels.HomeViewModel;if(vm==null)return;var parts=(vm.Resolution??"").Split('×');int width,height;if(parts.Length==2&&int.TryParse(parts[0].Trim(),out width)&&int.TryParse(parts[1].Trim(),out height)){GpuLiveColor.FrameWidth=width;GpuLiveColor.FrameHeight=height;}}
  async void HomeView_OnIsVisibleChanged(object sender,DependencyPropertyChangedEventArgs e)
  {
   if(!IsVisible)return;
   try
   {
    var services=((App)Application.Current).Services;if(services==null)return;
    var state=services.GetRequiredService<LiveColorState>();var settings=await services.GetRequiredService<ISettingsService>().GetAsync(CancellationToken.None);await state.RefreshAsync(settings,CancellationToken.None);
    GpuLiveColor.LutValues=state.Values;GpuLiveColor.LutSize=state.Size;GpuLiveColor.DomainMin=state.DomainMin;GpuLiveColor.DomainMax=state.DomainMax;GpuLiveColor.Strength=state.Strength;GpuLiveColor.Visibility=state.IsEnabled?Visibility.Visible:Visibility.Collapsed;CpuLiveView.Visibility=state.IsEnabled?Visibility.Collapsed:Visibility.Visible;
   }
   catch{GpuLiveColor.Visibility=Visibility.Collapsed;CpuLiveView.Visibility=Visibility.Visible;}
  }
  void GpuLiveColor_OnFailed(object sender,PhotoBooth.Color.D3D11.LiveColorFailedEventArgs e){GpuLiveColor.Visibility=Visibility.Collapsed;CpuLiveView.Visibility=Visibility.Visible;}
  private void ComboBox_SelectionChanged(object sender,SelectionChangedEventArgs e) { }
 }
}
