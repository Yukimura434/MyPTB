using System;using System.IO;using System.Linq;using System.Reflection;using System.Threading;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;using PhotoBooth.Core.Models;using PhotoBooth.Core.Services;using PhotoBooth.Customer.UI.ViewModels;using PhotoBooth.Customer.UI.Workflow;using PhotoBooth.Admin.UI.ViewModels;using PhotoBooth.Admin.UI.Services;
using CustomerWindow=PhotoBooth.Customer.UI.MainWindow;
class ApprovedUxEntry { [STAThread]static void Main(string[] args) => UnifiedUxCheck.ApprovedUxMain(args); }
partial class UnifiedUxCheck
{
 public static void ApprovedUxMain(string[] args){output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};foreach(var theme in new[]{"DarkTheme","Icons"})app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/MiuPhotoBooth;component/Themes/"+theme+".xaml",UriKind.Relative)));app.Resources.Add("ByteImage",new PhotoBooth.Admin.UI.Converters.ByteArrayImageConverter());var dt=new DataTemplate(typeof(HomeViewModel)){VisualTree=new FrameworkElementFactory(typeof(PhotoBooth.Admin.UI.Views.HomeView))};app.Resources.Add(new DataTemplateKey(typeof(HomeViewModel)),dt);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());app.Dispatcher.BeginInvoke(new Action(async()=>{try{await RunApprovedUx();Console.WriteLine("ALL APPROVED UX CHECKS PASS: "+checks);}catch(Exception e){Console.WriteLine(e);Environment.ExitCode=1;}finally{app.Shutdown();}}));app.Run();}

 static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
 static void Bounds(UserControl view,string name,string[] keys) { var lines=new System.Collections.Generic.List<string>();foreach(var key in keys){var el=(FrameworkElement)view.FindName(key);var point=el.TransformToAncestor(view).Transform(new Point());lines.Add(key+":"+point.X+","+point.Y+","+el.ActualWidth+","+el.ActualHeight);}File.WriteAllLines(Path.Combine(output,name+".bounds"),lines);}
 sealed class SaveFailure : ISettingsService { readonly ISettingsService inner;public bool Fail;public SaveFailure(ISettingsService source){inner=source;}public Task<Settings>GetAsync(CancellationToken t)=>inner.GetAsync(t);public Task SaveAsync(Settings value,CancellationToken t){if(Fail)throw new IOException("Synthetic save failure");return inner.SaveAsync(value,t);} }
 static void Visible(UserControl root,string name){var e=(FrameworkElement)root.FindName(name);var r=e.TransformToAncestor(root).TransformBounds(new Rect(0,0,e.ActualWidth,e.ActualHeight));Check(e.Visibility==Visibility.Visible&&r.Width>20&&r.Height>20&&r.Left>=0&&r.Top>=0&&r.Right<=root.ActualWidth+1&&r.Bottom<=root.ActualHeight+1,name+" remains in viewport");}
 static double Luminance(Color color) { Func<byte,double> linear = value => { double n=value/255d; return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4); }; return .2126*linear(color.R)+.7152*linear(color.G)+.0722*linear(color.B); }
 static async Task ButtonStates(UserControl view,Button button,string name)
 {
  var style=button.Style;var tag=button.Tag;
  foreach(var state in new[]{"normal","IsMouseOver","IsKeyboardFocused","disabled"})
  {
   if(state=="IsMouseOver"||state=="IsKeyboardFocused") { var markup=System.Windows.Markup.XamlWriter.Save(style).Replace("UIElement."+state,"FrameworkElement.Tag");button.Style=(Style)System.Windows.Markup.XamlReader.Parse(markup);button.Tag=true; }
   else button.IsEnabled=state!="disabled";
   foreach(var size in new[]{new[]{1366,768},new[]{600,1067}})await Render(view,name+"-"+state+"-"+size[0],size[0],size[1]);
   var body=(Border)(button.Template.FindName("Body",button) ?? button.Template.FindName("ButtonBorder",button));var fg=((SolidColorBrush)button.Foreground).Color;var bg=((SolidColorBrush)body.Background).Color;double a=Luminance(fg),b=Luminance(bg);var ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);Check(ratio>=4.5,name+" "+state+" text contrast "+ratio.ToString("F2")+":1");
   button.Style=style;button.Tag=tag;button.ClearValue(UIElement.IsEnabledProperty);
  }
 }
 static async Task RunApprovedUx()
 {
  var camera=new Camera();var live=new Live();var p=await Provider(camera,live,new Auth());
  var login=p.GetRequiredService<BoothLoginViewModel>();var loginView=new PhotoBooth.Customer.UI.Views.BoothLoginView{DataContext=login};
  await p.GetRequiredService<IInterfaceAssetService>().ImportAsync(Path.GetFullPath("Docs/Mockups/x66-customer-ux/assets/portrait.png"),none);
  var editor=p.GetRequiredService<InterfaceViewModel>();await editor.Initialization;
  var settings=p.GetRequiredService<ISettingsService>();var before=await settings.GetAsync(none);editor.BackgroundZoom=140;editor.IsPortraitMode=true;editor.BackgroundZoom=180;editor.IsPortraitMode=false;Check(editor.BackgroundZoom==140,"orientation keeps landscape draft");Check((await settings.GetAsync(none)).GetWaitingScreen(false,null).BackgroundZoom==before.GetWaitingScreen(false,null).BackgroundZoom,"editing does not persist");editor.DiscardCommand.Execute(null);Check(!editor.HasChanges,"discard restores landscape only");editor.IsPortraitMode=true;Check(editor.BackgroundZoom==180,"discard does not discard other orientation");editor.SaveLiveViewCommand.Execute(null);await Wait(()=>editor.IsReady,"save finished");Check(!editor.HasChanges,"save marks draft clean");Check((await settings.GetAsync(none)).PortraitWaitingScreen.BackgroundZoom==180,"save persists selected orientation");editor.BackgroundZoom=100;editor.ShowLiveView=false;editor.SaveLiveViewCommand.Execute(null);await Wait(()=>editor.IsReady,"reset fixture save");
  editor.Assets[0].Name="Studio nhẹ nhàng";var editorView=new PhotoBooth.Admin.UI.Views.InterfaceView{DataContext=editor};
  var vm=p.GetRequiredService<FrameSelectionViewModel>();
  var photo=Path.GetFullPath("Docs/Mockups/x66-customer-ux/assets/portrait.png");
  for(int i=0;i<4;i++)vm.CapturedPhotos.Add(new CapturedPhotoChoice(Path.GetFullPath("Scripts/Validation/Fixtures/ApprovedUx/photo-"+i+".png"),i+1));
  var slots=new System.Collections.Generic.List<FrameSlot>();for(int i=0;i<4;i++)slots.Add(new FrameSlot{Index=i,X=i%2==0?30:311,Y=i/2==0?30:426,Width=259,Height=366});
  for(int i=0;i<4;i++)vm.PinnedFrames.Add(new PhotoBooth.Core.Models.Frame{Id=Guid.NewGuid(),Name=new[]{"Crème","Hoa anh đào","Đêm dịu dàng","Ngày trong xanh"}[i],SourcePath=Path.GetFullPath("Scripts/Validation/Fixtures/ApprovedUx/frame-"+i+".png"),ThumbnailPath=Path.GetFullPath("Scripts/Validation/Fixtures/ApprovedUx/thumb-"+i+".png"),PixelWidth=600,PixelHeight=900,Slots=slots});
  vm.SelectedFrame=vm.PinnedFrames[0];for(int i=0;i<vm.FrameSlots.Count;i++)vm.FrameSlots[i].Photo=vm.CapturedPhotos[i%4];
  var frameView=new PhotoBooth.Customer.UI.Views.FrameSelectionView{DataContext=vm};
  foreach(var size in new[]{new[]{1366,768},new[]{900,1600},new[]{600,1067},new[]{1920,1080},new[]{800,1280},new[]{1280,800}}){
   await Render(loginView,"login-"+size[0]+"x"+size[1],size[0],size[1]); Bounds(loginView,"login-"+size[0]+"x"+size[1], new[]{"LoginBody","LoginCard","CodeBox","Assist","Intro","Title","Helper","CodeLabel","SubmitButton"});
   await Render(frameView,"frames-"+size[0]+"x"+size[1],size[0],size[1]); Bounds(frameView,"frames-"+size[0]+"x"+size[1],new[]{"WorkspaceGrid","PreviewPanel","Tools","ToolContent","FrameList","Footer"});
   await Render(editorView,"editor-"+size[0]+"x"+size[1],size[0],size[1]); Bounds(editorView,"editor-"+size[0]+"x"+size[1],new[]{"Header","Workspace","PreviewSurface","PreviewCanvas","Tools","ToolContent","AssetList","Footer"});
  }
  Check(!login.CanSubmit,"empty code disabled");login.Code="MIU2026A";Check(login.CanSubmit,"eight character code enabled");
  typeof(PhotoBooth.Customer.UI.Views.BoothLoginView).GetMethod("ShowKeyboard",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(loginView,null);
  foreach(var size in new[]{new[]{1366,768},new[]{900,1600},new[]{600,1067}})await Render(loginView,"keyboard-"+size[0]+"x"+size[1],size[0],size[1]);

  Visible(loginView,"CodeBox");Visible(loginView,"SubmitButton");
  var box=(TextBox)loginView.FindName("CodeBox");box.Text="ABCDEFGH";box.Select(2,3);var key=new Button{Tag='9'};typeof(PhotoBooth.Customer.UI.Views.BoothLoginView).GetMethod("Key_Click",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(loginView,new object[]{key,new RoutedEventArgs()});Check(login.Code=="AB9FGH","touch key replaces selection without losing suffix");key.Tag='⌫';typeof(PhotoBooth.Customer.UI.Views.BoothLoginView).GetMethod("Key_Click",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(loginView,new object[]{key,new RoutedEventArgs()});Check(login.Code=="ABFGH","touch backspace removes correct character");

  // State renders supplement the unmodified 18 reference comparisons.
  var asset=editor.Assets[0];editor.SelectedBackground=asset;editor.IsPortraitMode=false;editor.IsPortraitMode=true;Check(editor.SelectedAsset?.Id==asset.Id,"orientation rebind retains imported draft selection");editor.SaveLiveViewCommand.Execute(null);await Wait(()=>editor.IsReady,"asset save complete");
  var failing=new SaveFailure(settings){Fail=true};var failureVm=new InterfaceViewModel(p.GetRequiredService<IInterfaceAssetService>(),new Files(),failing,Microsoft.Extensions.Logging.Abstractions.NullLogger<InterfaceViewModel>.Instance);await failureVm.Initialization;failureVm.IsPortraitMode=true;Check(failureVm.SelectedAsset?.Id==asset.Id,"new editor restores imported background");failureVm.BackgroundZoom=170;failureVm.SaveLiveViewCommand.Execute(null);await Wait(()=>failureVm.IsReady,"failed save handled");Check(failureVm.HasChanges&&failureVm.HasError,"failed save preserves draft and exposes error");Check((await settings.GetAsync(none)).PortraitWaitingScreen.BackgroundZoom==100,"failed save leaves persisted profile unchanged");failing.Fail=false;failureVm.SaveLiveViewCommand.Execute(null);await Wait(()=>failureVm.IsReady,"retry save completes");Check(!failureVm.HasChanges&&!failureVm.HasError,"save retry clears error and dirty state");
  foreach(var mode in new[]{false,true}){failureVm.IsPortraitMode=mode;failureVm.UseDefaultCommand.Execute(null);Check(failureVm.SelectedAsset==null,"default is a draft selection");}
  var header=new PhotoBooth.Customer.UI.Controls.CustomerHeader{IsManagerEntranceEnabled=true};var entranceShell=p.GetRequiredService<CustomerShellViewModel>();typeof(CustomerShellViewModel).GetProperty("CurrentPage").SetValue(entranceShell,p.GetRequiredService<BoothLoginViewModel>());var host=new CustomerWindow{DataContext=entranceShell,Content=header,Width=900,Height=600,Left=-15000,Top=-15000};int entries=0;host.RequestManagerAccessAsync=()=>{entries++;return Task.CompletedTask;};host.Show();await Task.Delay(40);var staff=Descendants(header).OfType<Button>().Single();for(int i=0;i<4;i++)staff.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(entries==0,"new header requires five staff presses");staff.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(entries==1,"fifth new-header press uses existing manager gate");host.Close();

  editor.IsPortraitMode=true;editor.ShowLiveView=true;Descendants(editorView).OfType<RadioButton>().Single(x=>Equals(x.Tag,"live")).IsChecked=true;await Render(editorView,"editor-live-portrait",900,1600);await Render(editorView,"editor-live-small",600,1067);
  Descendants(editorView).OfType<RadioButton>().Single(x=>Equals(x.Tag,"adjust")).IsChecked=true;await Render(editorView,"editor-adjust-small",600,1067);
  await editor.RefreshAsync();Check(editor.HasChanges&&editor.ShowLiveView,"returning to editor keeps unsaved active draft");
  Descendants(frameView).OfType<RadioButton>().Single(x=>Equals(x.Tag,"photos")).IsChecked=true;await Render(frameView,"frames-photos-small",600,1067);
  Descendants(frameView).OfType<RadioButton>().Single(x=>Equals(x.Tag,"colors")).IsChecked=true;await Render(frameView,"frames-colors-empty-small",600,1067);
  typeof(PhotoBooth.Customer.UI.Views.FrameSelectionView).GetMethod("Continue_Click",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(frameView,new object[]{frameView,new RoutedEventArgs()});await Render(frameView,"frames-confirm-portrait",900,1600);Check(((Border)frameView.FindName("Confirmation")).Visibility==Visibility.Visible,"confirmation shown before output command");
  var tab=(RadioButton)editorView.FindName("PortraitTab");
  foreach(var state in new[]{"IsMouseOver","IsKeyboardFocused"}){var original=tab.Template;var markup=System.Windows.Markup.XamlWriter.Save(original).Replace("Property=\"UIElement."+state+"\"","Property=\"FrameworkElement.Tag\"");tab.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(markup);tab.Tag=true;await Render(editorView,"editor-"+state,900,1600);tab.Tag=null;tab.Template=original;}
  editorView.IsEnabled=false;await Render(editorView,"editor-disabled",600,1067);editorView.IsEnabled=true;
  login.Code="MIU2026A";typeof(PhotoBooth.Customer.UI.Views.BoothLoginView).GetMethod("HideKeyboard_Click",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(loginView,new object[]{loginView,new RoutedEventArgs()});
  await ButtonStates(loginView,(Button)loginView.FindName("SubmitButton"),"login-button");
  ((Border)frameView.FindName("Confirmation")).Visibility=Visibility.Collapsed;
  await ButtonStates(frameView,(Button)frameView.FindName("FinishButton"),"frames-button");
  await ButtonStates(editorView,(Button)editorView.FindName("SaveButton"),"editor-button");
  await vm.ShutdownAsync();Check(camera.Connects==0&&live.Starts==0,"presentation never acquires camera");
 }
}
