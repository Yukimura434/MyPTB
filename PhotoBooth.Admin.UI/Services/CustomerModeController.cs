using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.ViewModels;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.ViewModels;
using PhotoBooth.Customer.UI.Workflow;
using CustomerWindow = PhotoBooth.Customer.UI.MainWindow;

namespace PhotoBooth.Admin.UI.Services
{
    public interface ICustomerModeController
    {
        bool IsCustomerRunning { get; }
        string ActionText { get; }
        event EventHandler StateChanged;
        Task StartAsync();
    }

    // One service provider, one camera session, one active preview reader.
    public sealed class CustomerModeController : ICustomerModeController
    {
        readonly IServiceProvider provider;
        readonly ILogger<CustomerModeController> log;
        readonly SemaphoreSlim transition = new SemaphoreSlim(1, 1);
        CustomerWindow customer;
        MainWindow manager;
        HomeViewModel home;
        public bool IsCustomerRunning { get; private set; }
        public string ActionText => "Màn hình chụp";
        public event EventHandler StateChanged;

        public CustomerModeController(IServiceProvider provider, ILogger<CustomerModeController> logger)
        {
            this.provider = provider;
            log = logger;
        }

        public async Task StartAsync()
        {
            if (!await transition.WaitAsync(0)) return;
            try { await ShowCustomerAsync(); }
            catch (Exception error)
            {
                log.LogError(error, "Customer transition failed");
                MessageBox.Show("Chưa thể mở màn hình chụp. Vui lòng thử lại.", "MiuPhotoBooth");
                if (manager != null)
                {
                    // A failed activation may already own a preview; drain it before rollback.
                    await provider.GetRequiredService<CustomerShellViewModel>().PrepareReturnToAdminAsync();
                    customer?.Hide();
                    IsCustomerRunning = false;
                    Application.Current.MainWindow = manager;
                    manager.Show();
                    await home.ResumeFromCustomerAsync();
                }
                else if (customer == null) Application.Current.Shutdown(1);
            }
            finally { StateChanged?.Invoke(this, EventArgs.Empty); transition.Release(); }
        }

        async Task ShowCustomerAsync()
        {
            if (IsCustomerRunning) { customer.Activate(); return; }
            if (home != null)
            {
                await home.PersistSettingsAsync();
                await home.SuspendForCustomerAsync(CancellationToken.None);
            }
            var session = provider.GetRequiredService<IPcSessionContext>().Current;
            var shell = provider.GetRequiredService<CustomerShellViewModel>();
            shell.ResetForNewBoothSession();
            if (session != null) provider.GetRequiredService<IPcSessionContext>().SignIn(session);
            else provider.GetRequiredService<IPcSessionContext>().SignOut();
            provider.GetRequiredService<CustomerWorkflowContext>().ManagerSession = session;
            if (customer == null)
            {
                customer = provider.GetRequiredService<CustomerWindow>();
                customer.DataContext = shell;
                customer.RequestManagerAccessAsync = ShowManagerAsync;
                customer.Closed += (_, __) => Application.Current.Shutdown();
            }
            Application.Current.MainWindow = customer;
            manager?.Hide();
            IsCustomerRunning = true;
            customer.Show();
            customer.Activate();
            await shell.ActivateAsync();
        }

        async Task ShowManagerAsync()
        {
            if (!await transition.WaitAsync(0)) return;
            var suspended = false;
            try
            {
                var shell = provider.GetRequiredService<CustomerShellViewModel>();
                if (!shell.CanEnterManager)
                {
                    MessageBox.Show(customer, "Vui lòng hoàn tất lượt chụp hiện tại trước khi mở trang quản lý.", "MiuPhotoBooth");
                    return;
                }
                var login = provider.GetRequiredService<LoginWindow>();
                login.ConfigureForAdminAccess(customer);
                if (login.ShowDialog() != true || login.Session?.CanConfigureDevice != true) return;
                suspended = true;
                await shell.PrepareReturnToAdminAsync();
                provider.GetRequiredService<IPcSessionContext>().SignIn(login.Session);
                home = home ?? provider.GetRequiredService<HomeViewModel>();
                await home.Initialization;
                if (manager == null)
                {
                    manager = provider.GetRequiredService<MainWindow>();
                    var model = provider.GetRequiredService<MainViewModel>();
                    model.SignOutRequested += async (_, __) => await SignOutAsync();
                    manager.DataContext = model;
                    manager.Closed += (_, __) => Application.Current.Shutdown();
                }
                await home.ResumeFromCustomerAsync();
                await home.RefreshLivePresetAsync();
                IsCustomerRunning = false;
                Application.Current.MainWindow = manager;
                customer.Hide();
                manager.Show();
                manager.Activate();
            }
            catch (Exception error)
            {
                log.LogError(error, "Manager transition failed");
                if (suspended)
                {
                    if (home != null) await home.SuspendForCustomerAsync(CancellationToken.None);
                    await provider.GetRequiredService<CustomerShellViewModel>().ActivateAsync();
                }
                MessageBox.Show(customer, "Chưa thể mở trang quản lý. Vui lòng thử lại.", "MiuPhotoBooth");
            }
            finally { StateChanged?.Invoke(this, EventArgs.Empty); transition.Release(); }
        }

        async Task SignOutAsync()
        {
            if (!await transition.WaitAsync(0)) return;
            try
            {
                await provider.GetRequiredService<IPcAuthenticationService>().ForgetAsync(CancellationToken.None);
                provider.GetRequiredService<IPcSessionContext>().SignOut();
                await ShowCustomerAsync();
            }
            catch (Exception error)
            {
                log.LogError(error, "Sign out failed");
                MessageBox.Show("Chưa thể hoàn tất đăng xuất. Vui lòng thử lại.", "MiuPhotoBooth");
            }
            finally { StateChanged?.Invoke(this, EventArgs.Empty); transition.Release(); }
        }
    }
}