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
   var portrait=ActualHeight>ActualWidth;
   if(portrait)
   {
    LandscapeBackdrop.Visibility=Visibility.Collapsed;PortraitBackdrop.Visibility=Visibility.Visible;
    PreviewColumn.Width=new GridLength(1,GridUnitType.Star);QrColumn.Width=new GridLength(0);PreviewRow.Height=new GridLength(11,GridUnitType.Star);QrRow.Height=new GridLength(10,GridUnitType.Star);
    Grid.SetRow(GifPanel,0);Grid.SetColumn(GifPanel,0);Grid.SetColumnSpan(GifPanel,2);GifPanel.Margin=new Thickness(0,0,0,16);
    Grid.SetRow(QrPanel,1);Grid.SetColumn(QrPanel,0);Grid.SetColumnSpan(QrPanel,2);
    QrCodeSurface.Padding=new Thickness(18);QrCodeSurface.HorizontalAlignment=HorizontalAlignment.Center;QrCodeSurface.VerticalAlignment=VerticalAlignment.Center;QrCodeSurface.MaxWidth=360;QrCodeSurface.MaxHeight=360;DoneButton.Width=double.NaN;
   }
   else
   {
    LandscapeBackdrop.Visibility=Visibility.Visible;PortraitBackdrop.Visibility=Visibility.Collapsed;
    PreviewColumn.Width=new GridLength(1,GridUnitType.Star);QrColumn.Width=new GridLength(System.Math.Min(540, ActualWidth * .4));PreviewRow.Height=new GridLength(1,GridUnitType.Star);QrRow.Height=new GridLength(0);
    Grid.SetRow(GifPanel,0);Grid.SetColumn(GifPanel,0);Grid.SetColumnSpan(GifPanel,1);GifPanel.Margin=new Thickness(0,0,14,0);
    Grid.SetRow(QrPanel,0);Grid.SetColumn(QrPanel,1);Grid.SetColumnSpan(QrPanel,1);
    QrCodeSurface.Padding=new Thickness(18);QrCodeSurface.HorizontalAlignment=HorizontalAlignment.Center;QrCodeSurface.VerticalAlignment=VerticalAlignment.Center;QrCodeSurface.MaxWidth=390;QrCodeSurface.MaxHeight=390;DoneButton.Width=double.NaN;
   }
  }
 }
}
