using System.Windows.Controls;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI.Views
{
 public partial class HomeView:UserControl
 {
  public HomeView(){InitializeComponent();}
  private void OnLiveFramePresented(object sender,System.EventArgs e){(DataContext as HomeViewModel)?.ReportFramePresented();}
  private void ComboBox_SelectionChanged(object sender,SelectionChangedEventArgs e) { }
 }
}
