using System.Windows;
using System.Windows.Controls;
using PhotoBooth.Customer.UI.ViewModels;
namespace PhotoBooth.Customer.UI.Views
{
 public partial class CompleteView:UserControl
 {
  public CompleteView(){InitializeComponent();Loaded+=(s,e)=>ApplyOrientation();SizeChanged+=(s,e)=>ApplyOrientation();}
  void ApplyOrientation()
  {
   var shell=Window.GetWindow(this)?.DataContext as CustomerShellViewModel;
   var portrait=shell!=null&&shell.IsPortraitMode;
   if(portrait)
   {
    PreviewColumn.Width=new GridLength(1,GridUnitType.Star);QrColumn.Width=new GridLength(0);PreviewRow.Height=new GridLength(3,GridUnitType.Star);QrRow.Height=new GridLength(2,GridUnitType.Star);
    Grid.SetRow(GifPanel,0);Grid.SetColumn(GifPanel,0);Grid.SetColumnSpan(GifPanel,2);GifPanel.Margin=new Thickness(0,0,0,12);
    Grid.SetRow(QrPanel,1);Grid.SetColumn(QrPanel,0);Grid.SetColumnSpan(QrPanel,2);
   }
   else
   {
    PreviewColumn.Width=new GridLength(1,GridUnitType.Star);QrColumn.Width=new GridLength(360);PreviewRow.Height=new GridLength(1,GridUnitType.Star);QrRow.Height=new GridLength(0);
    Grid.SetRow(GifPanel,0);Grid.SetColumn(GifPanel,0);Grid.SetColumnSpan(GifPanel,1);GifPanel.Margin=new Thickness(0,0,14,0);
    Grid.SetRow(QrPanel,0);Grid.SetColumn(QrPanel,1);Grid.SetColumnSpan(QrPanel,1);
   }
  }
 }
}
