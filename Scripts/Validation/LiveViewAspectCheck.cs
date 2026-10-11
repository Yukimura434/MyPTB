using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoBooth.Customer.UI.Views;
using PhotoBooth.Color.D3D11;

public class LiveViewFixture : System.ComponentModel.INotifyPropertyChanged
{
 public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
 double rotation;
 public int LiveFrameWidth {get;set;} public int LiveFrameHeight {get;set;}
 public object LiveImage {get;set;} public object LivePreset => null;
 public double LiveViewScaleX {get;set;} = 1; public double LiveViewRotation {get=>rotation;set{rotation=value;PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(nameof(LiveViewRotation)));}}
 public bool HasLiveImage => true; public bool ShowLiveView => true;
 public bool IsModeSelection {get;set;} public bool IsPreview => false;
 public bool IsCountdown {get;set;} public bool IsManualMode => !IsModeSelection && !IsCountdown;
 public int Countdown => 3; public string Status => "Sẵn sàng chụp";
 public int CountdownNumber => 3; public string StatusMessage => "Sẵn sàng chụp";
 public bool HasError => false; public bool IsPrinting => false; public bool IsShutterFlash => false;
 public bool IsInterShotDelay => false; public bool IsSmile => false; public bool IsManualReady => IsManualMode;
 public double CanvasWidth {get;set;} public double CanvasHeight {get;set;}
 public double LiveViewCanvasX => CanvasWidth * .1; public double LiveViewCanvasY => CanvasHeight * .1;
 public double LiveViewDisplayWidth => CanvasWidth * .8; public double LiveViewDisplayHeight => CanvasHeight * .6;
 public string BackgroundPath => null; public double BackgroundDisplayWidth => CanvasWidth;
 public double BackgroundDisplayHeight => CanvasHeight; public double BackgroundCanvasX => 0; public double BackgroundCanvasY => 0;
}
class LiveViewAspectCheck
{
 static int checks; static string output; static Application app;
 [STAThread] static void Main(string[] args){output=args[0];Directory.CreateDirectory(output);app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Dispatcher.BeginInvoke(new Action(async()=>{try{await Run();Console.WriteLine("PASS "+checks+" native aspect/cover/background assertions");}catch(Exception e){Console.WriteLine(e);Environment.ExitCode=1;}finally{app.Shutdown();}}));app.Run();}
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
 static System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject p) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++){var c=VisualTreeHelper.GetChild(p,i);if(c is T)yield return (T)c;foreach(var d in Find<T>(c))yield return d;}}
 static BitmapSource Frame(int width,int height){var visual=new DrawingVisual();using(var d=visual.RenderOpen()){d.DrawRectangle(Brushes.LightSteelBlue,null,new Rect(0,0,width,height));d.DrawEllipse(Brushes.DarkCyan,new Pen(Brushes.White,5),new Point(width/2d,height/2d),height*.23,height*.23);d.DrawRectangle(null,new Pen(Brushes.OrangeRed,20),new Rect(10,10,width-20,height-20));}var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;}
 static async Task Run(){foreach(bool waiting in new[]{false,true})foreach(var size in new[]{new[]{1366,768},new[]{1920,1080},new[]{900,1600},new[]{600,1067}})foreach(var source in new[]{new[]{900,600},new[]{800,600},new[]{960,540}})foreach(int rotation in new[]{0,90,180,270,-90}){
  var data=new LiveViewFixture{CanvasWidth=size[0],CanvasHeight=size[1],LiveFrameWidth=source[0],LiveFrameHeight=source[1],LiveImage=Frame(source[0],source[1]),LiveViewRotation=rotation,LiveViewScaleX=rotation==180?-1:1,IsCountdown=true};
  var view=waiting?(UserControl)new WaitingView():new CaptureView();view.Width=size[0];view.Height=size[1];view.DataContext=data;
  var window=new Window{Content=view,Width=size[0],Height=size[1],WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Left=-20000,Top=-20000};window.Show();window.UpdateLayout();
  var image=Find<LatestJpegImage>(view).Single();for(int i=0;i<40&&image.Source==null;i++)await Task.Delay(20);window.UpdateLayout();Check(image.Source!=null,"decoded synthetic camera frame");
  var viewport=(FrameworkElement)view.FindName(waiting?"LiveViewViewport":"LiveViewPanel");var ratio=size[1]>size[0]?2d/3:1.5;Check(Math.Abs(viewport.ActualWidth/viewport.ActualHeight-ratio)<.004,"oriented viewport "+waiting+" "+rotation);
  var transform=image.TransformToAncestor(view);var origin=transform.Transform(new Point(0,0));var x=transform.Transform(new Point(100,0));var y=transform.Transform(new Point(0,100));var sx=(x-origin).Length;var sy=(y-origin).Length;Check(Math.Abs(sx-sy)<.1,"uniform scale after rotation and mirror");
  var media=transform.TransformBounds(new Rect(0,0,image.ActualWidth,image.ActualHeight));var bounds=viewport.TransformToAncestor(view).TransformBounds(new Rect(0,0,viewport.ActualWidth,viewport.ActualHeight));Check(media.Left<=bounds.Left+1&&media.Top<=bounds.Top+1&&media.Right>=bounds.Right-1&&media.Bottom>=bounds.Bottom-1,"cover without letterboxing");Check(bounds.Left>=-1&&bounds.Top>=-1&&bounds.Right<=size[0]+1&&bounds.Bottom<=size[1]+1,"viewport fits screen");
  if(!waiting){var bg=(Image)view.FindName(size[1]>size[0]?"LivePortraitBackground":"LiveLandscapeBackground");Check(bg.IsVisible&&bg.Source!=null,"PC artwork behind camera");}
  var clip=waiting?viewport:Find<PhotoBooth.Customer.UI.Controls.RoundedClipBorder>(viewport).First();Check(((PhotoBooth.Customer.UI.Controls.RoundedClipBorder)clip).CornerRadius.TopLeft==20,"rounded viewport");
  if(source[0]==900&&rotation==0){data.LiveViewRotation=270;await Task.Delay(30);window.UpdateLayout();Check(Math.Abs(viewport.ActualWidth/viewport.ActualHeight-ratio)<.004,"live rotation changes layout without screen resize");data.LiveViewRotation=0;await Task.Delay(30);window.UpdateLayout();Check(Math.Abs(viewport.ActualWidth/viewport.ActualHeight-ratio)<.004,"live rotation restores landscape");}
  if(source[0]==900&&(rotation==0||rotation==90)){Save(view,(waiting?"waiting":"capture")+"-"+size[0]+"x"+size[1]+"-"+rotation);if(!waiting){data.IsCountdown=false;data.IsModeSelection=false;view.DataContext=null;view.DataContext=data;await Task.Delay(100);window.UpdateLayout();var liveBounds=viewport.TransformToAncestor(view).TransformBounds(new Rect(0,0,viewport.ActualWidth,viewport.ActualHeight));var shutter=(FrameworkElement)view.FindName("ShutterButton");var shutterBounds=shutter.TransformToAncestor(view).TransformBounds(new Rect(0,0,shutter.ActualWidth,shutter.ActualHeight));Check(liveBounds.Bottom+20<=shutterBounds.Top,"shutter outside preview with gap");Save(view,"manual-"+size[0]+"x"+size[1]+"-"+rotation);data.IsModeSelection=true;view.DataContext=null;view.DataContext=data;await Task.Delay(420);window.UpdateLayout();Save(view,"modes-"+size[0]+"x"+size[1]+"-"+rotation);}}
  window.Close();
 }}
 static void Save(FrameworkElement view,string name){var original=view;view=original is WaitingView?(FrameworkElement)new WaitingView():new CaptureView();view.Width=original.Width;view.Height=original.Height;view.DataContext=original.DataContext;foreach(var im in Find<LatestJpegImage>(view))im.Source=((LiveViewFixture)view.DataContext).LiveImage as BitmapSource;view.Measure(new Size(view.Width,view.Height));view.Arrange(new Rect(0,0,view.Width,view.Height));view.UpdateLayout();foreach(var im in Find<LatestJpegImage>(view))im.Source=((LiveViewFixture)view.DataContext).LiveImage as BitmapSource;view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,name+".png")))encoder.Save(stream);}
}
