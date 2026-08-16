using System;using System.Threading;using System.Threading.Tasks;using System.Windows;using Microsoft.Extensions.DependencyInjection;using PhotoBooth.Admin.UI.ViewModels;using PhotoBooth.Core.Services;using PhotoBooth.Customer.UI.ViewModels;
namespace PhotoBooth.Admin.UI.Services
{
 public interface ICustomerModeController{Task StartAsync();}
 public sealed class CustomerModeController:ICustomerModeController
 {
  readonly IServiceProvider provider;readonly ModeHandoffCoordinator handoff=new ModeHandoffCoordinator();bool running;string temporaryPin;public CustomerModeController(IServiceProvider provider){this.provider=provider;}
  public async Task StartAsync(){if(running)return;var admin=Application.Current.MainWindow;if(string.IsNullOrEmpty(temporaryPin)){var created=TemporaryPinDialog.Create(admin);if(string.IsNullOrEmpty(created))return;temporaryPin=created;}running=true;var home=provider.GetRequiredService<HomeViewModel>();
   try{
     var customer=provider.GetRequiredService<PhotoBooth.Customer.UI.MainWindow>();var shell=provider.GetRequiredService<CustomerShellViewModel>();shell.ResetForNewSession();customer.DataContext=shell;customer.Owner=admin;customer.RequestAdminAccess=()=>TemporaryPinDialog.Verify(customer,temporaryPin);
     using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30))){await handoff.TransferAsync(home.SuspendForCustomerAsync,async _=>await shell.ActivateAsync(),timeout.Token);}
     admin.Hide();
     customer.ShowDialog();
   }
   catch(Exception e){System.Diagnostics.Debug.WriteLine("Customer mode handoff failed: "+e);}
   finally{
     admin.Show();admin.Activate();
     try{await WithTimeout(TimeSpan.FromSeconds(15),async _=>{var capture=provider.GetService<CaptureViewModel>();if(capture!=null)await capture.ShutdownAsync();});}catch(Exception e){System.Diagnostics.Debug.WriteLine("Customer shutdown failed: "+e);}
     try{await WithTimeout(TimeSpan.FromSeconds(20),async ct=>await home.ResumeFromCustomerAsync(ct));}catch(Exception e){System.Diagnostics.Debug.WriteLine("Admin resume failed: "+e);}
     running=false;
   }
  }
  static async Task WithTimeout(TimeSpan timeout,Func<CancellationToken,Task> action){using(var cts=new CancellationTokenSource(timeout)){var task=action(cts.Token);var done=await Task.WhenAny(task,Task.Delay(timeout));if(done!=task)System.Diagnostics.Debug.WriteLine("Operation exceeded "+timeout);}}
 }
}
