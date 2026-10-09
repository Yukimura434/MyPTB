using System.Windows;
using System.Windows.Controls;
using PhotoBooth.Admin.UI.ViewModels;
namespace PhotoBooth.Admin.UI.Views
{
 public partial class HomeView : UserControl
 {
  public HomeView(){InitializeComponent();Loaded+=async(_,__)=>{if(DataContext is HomeViewModel vm)await vm.RefreshLivePresetAsync();};SizeChanged+=(_,__)=>ArrangeBooth();}
  void ArrangeBooth(){var narrow=ActualWidth<1050;BoothLayout.ColumnDefinitions[0].Width=new GridLength(narrow?1:13,GridUnitType.Star);BoothLayout.ColumnDefinitions[1].Width=new GridLength(narrow?0:18);BoothLayout.ColumnDefinitions[2].Width=narrow?new GridLength(0):new GridLength(7,GridUnitType.Star);Grid.SetColumn(BoothControls,narrow?0:2);Grid.SetRow(BoothControls,narrow?1:0);BoothControls.Margin=narrow?new Thickness(0,16,0,0):new Thickness(0);CameraCard.Height=narrow?420:580;}
  void OnLiveFramePresented(object sender,System.EventArgs e){(DataContext as HomeViewModel)?.ReportFramePresented();}
 }
}
