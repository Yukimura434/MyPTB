using System.Windows;
using System.Windows.Controls;
namespace PhotoBooth.Customer.UI.Views
{
 public partial class PrinterConnectionView:UserControl
 {
  public PrinterConnectionView(){InitializeComponent();Loaded+=(s,e)=>ApplyOrientation();SizeChanged+=(s,e)=>ApplyOrientation();}
  void ApplyOrientation()
  {
   var portrait=ActualHeight>ActualWidth*1.08;
   if(portrait)
   {
    PrinterListColumn.Width=new GridLength(1,GridUnitType.Star);PrinterSettingsColumn.Width=new GridLength(0);PrinterListRow.Height=new GridLength(1,GridUnitType.Star);PrinterSettingsRow.Height=new GridLength(1,GridUnitType.Star);
    Grid.SetRow(PrinterListPanel,0);Grid.SetColumn(PrinterListPanel,0);Grid.SetColumnSpan(PrinterListPanel,2);PrinterListPanel.Margin=new Thickness(0,0,0,10);
    Grid.SetRow(PrinterSettingsPanel,1);Grid.SetColumn(PrinterSettingsPanel,0);Grid.SetColumnSpan(PrinterSettingsPanel,2);
   }
   else
   {
    PrinterListColumn.Width=new GridLength(5,GridUnitType.Star);PrinterSettingsColumn.Width=new GridLength(6,GridUnitType.Star);PrinterListRow.Height=new GridLength(1,GridUnitType.Star);PrinterSettingsRow.Height=new GridLength(0);
    Grid.SetRow(PrinterListPanel,0);Grid.SetColumn(PrinterListPanel,0);Grid.SetColumnSpan(PrinterListPanel,1);PrinterListPanel.Margin=new Thickness(0,0,12,0);
    Grid.SetRow(PrinterSettingsPanel,0);Grid.SetColumn(PrinterSettingsPanel,1);Grid.SetColumnSpan(PrinterSettingsPanel,1);
   }
  }
 }
}
