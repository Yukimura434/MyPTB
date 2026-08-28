using System;using System.Threading;using System.Threading.Tasks;using System.Windows;using Microsoft.Extensions.DependencyInjection;using Microsoft.Extensions.Logging;using PhotoBooth.Admin.UI.ViewModels;using PhotoBooth.Core.Services;using PhotoBooth.Customer.UI.ViewModels;
namespace PhotoBooth.Admin.UI.Services
{
 public interface ICustomerModeController{Task StartAsync();}
 public sealed class CustomerModeController:ICustomerModeController
 {
  readonly IServiceProvider provider;readonly ILogger<CustomerModeController> log;readonly ModeHandoffCoordinator handoff=new ModeHandoffCoordinator();bool running;string temporaryPin;public CustomerModeController(IServiceProvider provider,ILogger<CustomerModeController> logger){this.provider=provider;log=logger;}
  public async Task StartAsync(){if(running)return;var admin=Application.Current.MainWindow;if(string.IsNullOrEmpty(temporaryPin)){var created=TemporaryPinDialog.Create(admin);if(string.IsNullOrEmpty(created))return;temporaryPin=created;}running=true;var home=provider.GetRequiredService<HomeViewModel>();
   try{
     await home.PersistSettingsAsync();
     var customer=provider.GetRequiredService<PhotoBooth.Customer.UI.MainWindow>();var shell=provider.GetRequiredService<CustomerShellViewModel>();shell.ResetForNewSession();customer.DataContext=shell;customer.Owner=admin;customer.RequestAdminAccess=()=>TemporaryPinDialog.Verify(customer,temporaryPin);
     using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30))){await handoff.TransferAsync(home.SuspendForCustomerAsync,async _=>await shell.ActivateAsync(),timeout.Token);}
     admin.Hide();
     customer.ShowDialog();
   }
   catch(Exception e){log.LogError(e,"Customer mode activation failed");}
   finally{
     try{
      try{await WithTimeout(TimeSpan.FromSeconds(20),async ct=>{var frame=provider.GetService<FrameSelectionViewModel>();if(frame!=null)await frame.ShutdownAsync();var video=provider.GetService<VideoSelectionViewModel>();if(video!=null)await video.ResetAsync();var capture=provider.GetService<CaptureViewModel>();if(capture!=null)await capture.ShutdownAsync(ct);});}catch(Exception e){log.LogError(e,"Customer workflow shutdown failed during Admin handoff");}
      try{await WithTimeout(TimeSpan.FromSeconds(20),async ct=>await home.ResumeFromCustomerAsync(ct));}catch(Exception e){log.LogError(e,"Admin camera resume failed after Customer handoff");}
     }
     finally{admin.Show();admin.Activate();running=false;}
   }
  }
  static async Task WithTimeout(TimeSpan timeout,Func<CancellationToken,Task> action){using(var cts=new CancellationTokenSource(timeout)){await action(cts.Token);}}
 }
}
