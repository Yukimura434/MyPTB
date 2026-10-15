using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PhotoBooth.Core.Models;
using Frame = PhotoBooth.Core.Models.Frame;
using PhotoBooth.Core.Services;
namespace PhotoBooth.Admin.UI.Views
{
 public sealed class FrameQrEditorWindow : Window
 {
  readonly Frame frame; readonly Func<FrameQrSettings,Task> save; readonly IQrCodeService qrService;
  FrameQrSettings draft; readonly Canvas canvas=new Canvas{Background=Brushes.White,ClipToBounds=true};
  readonly Image photo=new Image{Stretch=Stretch.Fill}; readonly Border qr=new Border{BorderBrush=Brushes.OrangeRed,BorderThickness=new Thickness(2),Background=Brushes.White};
  readonly Slider size=new Slider{Minimum=10,Maximum=35,TickFrequency=1,IsSnapToTickEnabled=true};
  readonly CheckBox enabled=new CheckBox{Content="Hiển thị QR trên ảnh"};
  readonly TextBlock sizeText=new TextBlock(),status=new TextBlock{TextWrapping=TextWrapping.Wrap};
  readonly TextBox x=new TextBox(),y=new TextBox(); bool updating,dragging,resizing; Point start; FrameQrSettings startSettings;
  readonly Grid layout=new Grid(); readonly StackPanel controls=new StackPanel(); readonly Viewbox preview=new Viewbox{Stretch=Stretch.Uniform,Margin=new Thickness(20)};
  static Brush Ink=new SolidColorBrush(System.Windows.Media.Color.FromRgb(241,245,255)),Panel=new SolidColorBrush(System.Windows.Media.Color.FromRgb(27,36,50));
  public FrameQrEditorWindow(Frame selected,Func<FrameQrSettings,Task> persist,IQrCodeService service)
  {
   frame=selected;save=persist;qrService=service;var initial=frame.PrintedQr??new FrameQrSettings();draft=new FrameQrSettings{Enabled=initial.Enabled,X=initial.X,Y=initial.Y,Size=initial.Size};
   Title="QR trên ảnh";Width=1040;Height=800;MinWidth=480;MinHeight=620;Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(17,23,34));Foreground=Ink;WindowStartupLocation=WindowStartupLocation.CenterOwner;
   var root=new DockPanel{Margin=new Thickness(24)};Content=root;
   var heading=new TextBlock{Text=Title,FontSize=24,FontWeight=FontWeights.Bold,Margin=new Thickness(0,0,0,18)};DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
   var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);bottom.Children.Add(status);
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};bottom.Children.Add(actions);
   actions.Children.Add(Button("Hủy thay đổi",()=>Close()));var saveButton=Button("Lưu cho tất cả khung",async()=>{try{IsEnabled=false;await save(draft.Normalize((int)canvas.Width,(int)canvas.Height));DialogResult=true;}catch{status.Text="Không thể lưu. Vui lòng thử lại.";}finally{IsEnabled=true;}});actions.Children.Add(saveButton);
   root.Children.Add(new ScrollViewer{Content=layout,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});layout.ColumnDefinitions.Add(new ColumnDefinition());layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(300)});layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   preview.Child=canvas;preview.MaxHeight=580;layout.Children.Add(preview);Grid.SetColumn(controls,1);layout.Children.Add(controls);
   canvas.Width=Math.Max(1,frame.PixelWidth);canvas.Height=Math.Max(1,frame.PixelHeight);canvas.Children.Add(photo);canvas.Children.Add(qr);LoadPhoto(frame.SourcePath,false);
   controls.Children.Add(Button("Nhập ảnh mẫu",()=>{var picker=new OpenFileDialog{Filter="Ảnh PNG/JPEG|*.png;*.jpg;*.jpeg"};if(picker.ShowDialog(this)==true)LoadPhoto(picker.FileName,true);}));
   enabled.Foreground=Ink;enabled.Margin=new Thickness(0,16,0,16);controls.Children.Add(enabled);enabled.Checked+=(_,__)=>Toggle();enabled.Unchecked+=(_,__)=>Toggle();
   sizeText.Foreground=Ink;controls.Children.Add(sizeText);controls.Children.Add(size);size.ValueChanged+=(_,__)=>{if(updating)return;draft.Size=size.Value/150;Refresh();};
   AddCoordinate("Ngang (%)",x,true);AddCoordinate("Dọc (%)",y,false);
   controls.Children.Add(new TextBlock{Text="Kéo QR để di chuyển; kéo góc dưới phải để đổi kích thước.\nKích thước minh họa với cạnh dài 150 mm. QR thật dùng mã nhận ảnh của lượt chụp.\nÁp dụng cho tất cả khung. Ảnh nhập chỉ dùng xem trước.",TextWrapping=TextWrapping.Wrap,Foreground=Ink,Margin=new Thickness(0,20,0,0)});
   qr.MouseLeftButtonDown+=(_,e)=>{if(!draft.Enabled)return;start=e.GetPosition(canvas);startSettings=draft.Normalize((int)canvas.Width,(int)canvas.Height);var p=e.GetPosition(qr);resizing=p.X>qr.Width*0.8&&p.Y>qr.Height*0.8;dragging=true;qr.CaptureMouse();e.Handled=true;};
   qr.MouseMove+=(_,e)=>{if(!dragging)return;var p=e.GetPosition(canvas);if(resizing)draft.Size=startSettings.Size+(p.X-start.X)/Math.Max(canvas.Width,canvas.Height);else{draft.X=startSettings.X+(p.X-start.X)/canvas.Width;draft.Y=startSettings.Y+(p.Y-start.Y)/canvas.Height;}Refresh();};qr.MouseLeftButtonUp+=(_,__)=>{dragging=false;qr.ReleaseMouseCapture();};qr.LostMouseCapture+=(_,__)=>dragging=false;
   SizeChanged+=(_,__)=>{bool portrait=ActualWidth<760;Grid.SetColumn(controls,portrait?0:1);Grid.SetRow(controls,portrait?1:0);layout.ColumnDefinitions[1].Width=portrait?new GridLength(0):new GridLength(300);preview.MaxHeight=portrait?400:580;};
   Loaded+=async(_,__)=>{try{var bytes=await qrService.GeneratePngAsync(new Uri("https://example.com/q/preview"),240,CancellationToken.None);var image=new Image{Source=Decode(bytes),Stretch=Stretch.Fill};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);var grid=new Grid();grid.Children.Add(image);grid.Children.Add(new Border{Width=12,Height=12,Background=Brushes.OrangeRed,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom});qr.Child=grid;}catch{status.Text="Không thể tạo mã xem trước.";}};
   status.Text="Áp dụng từ lượt chụp tiếp theo. Nên in thử và quét trước khi sử dụng.";Refresh();
  }
  void AddCoordinate(string label,TextBox input,bool horizontal){controls.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,12,0,6),Foreground=Ink});input.Background=Panel;input.Foreground=Ink;input.BorderBrush=Ink;input.Padding=new Thickness(10);input.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'><Border Background='{TemplateBinding Background}' BorderBrush='#8196B2' BorderThickness='1' Padding='{TemplateBinding Padding}'><ScrollViewer Name='PART_ContentHost'/></Border></ControlTemplate>");controls.Children.Add(input);input.LostKeyboardFocus+=(_,__)=>{if(double.TryParse(input.Text,out var value)){if(horizontal)draft.X=value/100;else draft.Y=value/100;}Refresh();};}
  Button Button(string label,Action click){var b=new Button{Content=label,Margin=new Thickness(0,8,8,8),Padding=new Thickness(14,10,14,10),Background=Panel,Foreground=Ink,BorderBrush=Ink};var template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Name='surface' Background='{TemplateBinding Background}' BorderBrush='#8196B2' BorderThickness='1' CornerRadius='8' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='surface' Property='Background' Value='#263D64'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='surface' Property='BorderBrush' Value='#B5CEFF'/><Setter TargetName='surface' Property='BorderThickness' Value='3'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='surface' Property='Background' Value='#315DAE'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#C4D2E8'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");b.Template=template;b.Click+=(_,__)=>click();return b;}
  void Toggle(){if(updating)return;draft.Enabled=enabled.IsChecked==true;Refresh();}
  void Refresh(){updating=true;draft=draft.Normalize((int)canvas.Width,(int)canvas.Height);enabled.IsChecked=draft.Enabled;size.Value=draft.Size*150;sizeText.Text="Kích thước QR "+Math.Round(draft.Size*150)+" mm";x.Text=Math.Round(draft.X*100).ToString();y.Text=Math.Round(draft.Y*100).ToString();size.IsEnabled=x.IsEnabled=y.IsEnabled=draft.Enabled;qr.Visibility=draft.Enabled?Visibility.Visible:Visibility.Hidden;qr.Width=qr.Height=draft.Size*Math.Max(canvas.Width,canvas.Height);Canvas.SetLeft(qr,draft.X*canvas.Width);Canvas.SetTop(qr,draft.Y*canvas.Height);updating=false;}
  string importedPath;
  public string PersistPreviewImage(string previous) { if(importedPath==null)return previous; var directory=Path.Combine(Environment.GetEnvironmentVariable("PHOTOBOOTH_DATA_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PhotoBooth","Data"),"QrPreview"); Directory.CreateDirectory(directory); var target=Path.Combine(directory,Guid.NewGuid().ToString("N")+Path.GetExtension(importedPath)); File.Copy(importedPath,target); return target; }
  void LoadPhoto(string path,bool sample){if(string.IsNullOrWhiteSpace(path)){status.Text="Nhập ảnh để xem trước. Cài đặt áp dụng cho tất cả khung.";return;}try{var image=Decode(File.ReadAllBytes(path));canvas.Width=image.PixelWidth;canvas.Height=image.PixelHeight;photo.Width=canvas.Width;photo.Height=canvas.Height;photo.Source=image;if(sample)importedPath=path;Refresh();}catch{status.Text="Không đọc được ảnh. Hãy chọn ảnh PNG hoặc JPEG khác.";}}
  static BitmapImage Decode(byte[] bytes){var image=new BitmapImage();using(var stream=new MemoryStream(bytes)){image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();}return image;}
 }
}
