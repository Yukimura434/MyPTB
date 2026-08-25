using System.Windows;
using PhotoBooth.Admin.UI.ViewModels;
namespace PhotoBooth.Admin.UI
{
 public partial class MainWindow : Window
 {
  public MainWindow(){InitializeComponent();SizeChanged+=(s,e)=>ApplyOrientation();Loaded+=(s,e)=>ApplyOrientation();}
  void ApplyOrientation(){if(ActualHeight>ActualWidth*1.08&&DataContext is MainViewModel vm)vm.IsMenuExpanded=false;}
 }
}
