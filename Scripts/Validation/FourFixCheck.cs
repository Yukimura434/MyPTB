using System;using System.IO;using System.Linq;using System.Reflection;using System.Threading;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using System.Windows.Threading;using Microsoft.Extensions.DependencyInjection;using PhotoBooth.Core.Models;using PhotoBooth.Core.Services;using PhotoBooth.Core.Persistence;using PhotoBooth.Customer.UI.ViewModels;using PhotoBooth.Customer.UI.Workflow;
class FourFixEntry{[STAThread]static void Main(string[] args)=>UnifiedUxCheck.FourFixMain(args);}
partial class UnifiedUxCheck {
 public static void FourFixMain(string[] args){output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};foreach(var theme in new[]{"DarkTheme","Icons"})app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/MiuPhotoBooth;component/Themes/"+theme+".xaml",UriKind.Relative)));SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());app.Dispatcher.BeginInvoke(new Action(async()=>{try{await FourFixRun();Console.WriteLine("ALL X88 CHECKS PASS: "+checks);}catch(Exception e){Console.WriteLine(e);Environment.ExitCode=1;}finally{app.Shutdown();}}));app.Run();}
 static async Task FourFixRun(){
 var p=await Provider(new Camera(),new Live(),new Auth());var vm=p.GetRequiredService<BoothLoginViewModel>();var view=new PhotoBooth.Customer.UI.Views.BoothLoginView{DataContext=vm};var box=(TextBox)view.FindName("CodeBox");vm.Code="AB8XYZ29";
 foreach(var size in new[]{new[]{1920,1080},new[]{1366,768},new[]{900,1600},new[]{600,1067}}){
 foreach(var keyboard in new[]{false,true}){
 typeof(PhotoBooth.Customer.UI.Views.BoothLoginView).GetField("keyboard",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(view,keyboard);
 ((FrameworkElement)view.FindName("KeyboardPanel")).Visibility=keyboard?Visibility.Visible:Visibility.Collapsed;
 typeof(PhotoBooth.Customer.UI.Views.BoothLoginView).GetMethod("Layout",BindingFlags.NonPublic|BindingFlags.Instance)?.Invoke(view,null);
 await Render(view,"code-"+size[0]+"-"+keyboard,size[0],size[1]);
 var rect=box.GetRectFromCharacterIndex(0);Check(rect.Height>25&&rect.Top>=0&&rect.Bottom<=box.ActualHeight-2,"complete code glyph height "+size[0]+" keyboard="+keyboard);Check(box.Text=="AB8XYZ29","all eight characters retained");
 var fg=((SolidColorBrush)box.Foreground).Color;var bg=((SolidColorBrush)box.Background).Color;Check((Luminance(fg)+.05)/(Luminance(bg)+.05)>=4.5,"input contrast");
 }}
 box.IsEnabled=false;await Render(view,"code-disabled",600,1067);box.IsEnabled=true;
 box.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,0,null,box){RoutedEvent=UIElement.GotKeyboardFocusEvent});await Render(view,"code-focused",1366,768);
 var frameRepo=p.GetRequiredService<IFrameRepository>();var ids=new System.Collections.Generic.List<Guid>();
 for(int i=0;i<9;i++){var id=Guid.NewGuid();ids.Add(id);await frameRepo.SaveAsync(new PhotoBooth.Core.Models.Frame{Id=id,Name="Khung "+(i+1),IsPinned=true,SourcePath=Path.GetFullPath("Scripts/Validation/Fixtures/ApprovedUx/frame-0.png"),PixelWidth=600,PixelHeight=900,CreatedAtUtc=DateTime.UtcNow,Slots=new[]{new FrameSlot{Id=Guid.NewGuid(),Index=0,X=30,Y=30,Width=250,Height=350}}},none);}
 await frameRepo.ReplacePinnedAsync(ids,none);var events=p.GetRequiredService<IEventService>();var ev=await events.GetDefaultAsync(none);var cfgs=p.GetRequiredService<IPhotoEventConfigurationRepository>();var legacy=await cfgs.GetAsync(ev.Id,none);legacy.FrameIds=ids.Take(3).ToList();await cfgs.SaveAsync(ev.Name,legacy,none);await cfgs.ActivateAsync(ev.Id,none);await frameRepo.ReplacePinnedAsync(ids,none);
 var context=p.GetRequiredService<CustomerWorkflowContext>();var shell=p.GetRequiredService<CustomerShellViewModel>();var freeze=typeof(CustomerShellViewModel).GetMethod("FreezeRunConfigurationAsync",BindingFlags.Instance|BindingFlags.NonPublic);
 foreach(var mode in new[]{BoothAccessCodeType.PhotoBooth,BoothAccessCodeType.SelfBooth,BoothAccessCodeType.Event}){
 context.Authorization=new BoothAccessAuthorization{CodeType=mode,Settings=new BoothAccessSettings{PhotoCount=4,CountdownSeconds=3,PrintCopies=2,ResultDisplaySeconds=45}};context.ManagerSession=null;await(Task)freeze.Invoke(shell,null);Check(context.AvailableFrames.Count==9,"all nine used frames in "+mode+" actual="+context.AvailableFrames.Count);if(mode!=BoothAccessCodeType.Event){Check(context.Settings.PhotoCount==4&&context.RequestedPrintCopies==2,"package capture and print settings retained "+mode);}
 var frozen=context.AvailableFrames;await frameRepo.ReplacePinnedAsync(ids.Take(8).ToList(),none);Check(frozen.Count==9,"active run snapshot immutable");await frameRepo.ReplacePinnedAsync(ids,none);
 }
 context.Authorization=null;context.ManagerSession=new PcSession{Role=PcAppRole.BoothOperator,ExpiresAtUtc=DateTime.UtcNow.AddHours(1)};await(Task)freeze.Invoke(shell,null);Check(context.AvailableFrames.Count==9,"employee uses same nine frames");
 await p.GetRequiredService<CaptureViewModel>().ShutdownAsync();
 }
}
