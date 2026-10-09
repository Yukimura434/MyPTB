using System;using System.IO;using System.Linq;using System.Threading;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;using PhotoBooth.Core.Services;using PhotoBooth.Admin.UI.ViewModels;
class ManualUpdateEntry{[STAThread]static void Main(string[] args)=>UnifiedUxCheck.ManualUpdateMain(args);}
partial class UnifiedUxCheck {
 public static void ManualUpdateMain(string[] args){output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};foreach(var theme in new[]{"DarkTheme","Icons"})app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/MiuPhotoBooth;component/Themes/"+theme+".xaml",UriKind.Relative)));SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());app.Dispatcher.BeginInvoke(new Action(async()=>{try{await RunManualUpdate();Console.WriteLine("ALL MANUAL UPDATE UX CHECKS PASS: "+checks);}catch(Exception e){Console.WriteLine(e);Environment.ExitCode=1;}finally{app.Shutdown();}}));app.Run();}
 sealed class UpdateFake:IUpdateService { public int Checks,Downloads;public bool Available=true,Fail;public TaskCompletionSource<bool> Pending;
  public Task<bool>IsUpdateAvailableAsync(CancellationToken t){Checks++;if(Fail)throw new IOException("offline");return Pending?.Task??Task.FromResult(Available);}
  public Task<string>GetAvailableVersionAsync(CancellationToken t)=>Task.FromResult("3.0.1");
  public Task<string>DownloadUpdateAsync(CancellationToken t){Downloads++;return Task.FromResult("synthetic-installer-never-launched.exe");}
 }
 static async Task RunManualUpdate(){
  var fake=new UpdateFake();var vm=new AboutViewModel(fake);var view=new PhotoBooth.Admin.UI.Views.AboutView{DataContext=vm};Check(fake.Checks==0&&fake.Downloads==0,"opening About never checks or downloads automatically");Check(vm.Version=="3.0.0","real assembly version is 3.0.0");
  foreach(var size in new[]{new[]{1366,768},new[]{600,1067},new[]{480,900}})await Render(view,"about-initial-"+size[0],size[0],size[1]);
  vm.CheckCommand.Execute(null);await Wait(()=>vm.HasUpdate,"check exposes new version");Check(fake.Downloads==0,"check never downloads");
  await Render(view,"about-available-1366",1366,768);await Render(view,"about-available-600",600,1067);
  var button=Descendants(view).OfType<Button>().First(x=>Equals(x.Content,"Kiểm tra bản cập nhật"));await ButtonStates(view,button,"update-check");
  vm.DownloadCommand.Execute(null);await Wait(()=>vm.IsReadyToInstall,"explicit download prepares installer");Check(fake.Downloads==1&&vm.InstallCommand.CanExecute(null),"installer requires separate action");await Render(view,"about-ready-1366",1366,768);await Render(view,"about-ready-600",600,1067);
  fake.Fail=true;vm.CheckCommand.Execute(null);await Wait(()=>vm.Status.Contains("Chưa kiểm tra"),"network error displayed");Check(!vm.HasUpdate&&!vm.IsReadyToInstall,"failed recheck discards stale offer");await Render(view,"about-error-600",600,1067);
  fake.Fail=false;fake.Pending=new TaskCompletionSource<bool>();vm.CheckCommand.Execute(null);Check(!vm.CheckCommand.CanExecute(null)&&!vm.DownloadCommand.CanExecute(null),"busy update cannot overlap another request");await Render(view,"about-checking-600",600,1067);fake.Pending.SetResult(false);await Wait(()=>vm.CheckCommand.CanExecute(null),"check ends and can retry");Check(vm.Status.Contains("mới nhất"),"up-to-date state displayed");
  var provider=await Provider(new Camera(),new Live(),new Auth());var login=provider.GetRequiredService<PhotoBooth.Admin.UI.LoginWindow>();login.ConfigureForAdminAccess(null);Check(login.FindName("RememberLoginBox")==null,"remember login checkbox removed");
  var body=(FrameworkElement)login.Content;login.Left=-15000;login.Top=-15000;login.Show();foreach(var size in new[]{new[]{1264,780},new[]{720,1100}}){login.Width=size[0];login.Height=size[1];login.UpdateLayout();await Task.Delay(100);await Render(body,"staff-login-"+size[0],size[0],size[1]);}login.Close();provider.Dispose();
 }
}
