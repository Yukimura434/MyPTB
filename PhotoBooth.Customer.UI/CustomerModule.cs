using Microsoft.Extensions.DependencyInjection;
using PhotoBooth.Customer.UI.ViewModels;
using PhotoBooth.Customer.UI.Workflow;

namespace PhotoBooth.Customer.UI
{
    public static class CustomerModule
    {
        public static IServiceCollection AddCustomerMode(this IServiceCollection services)
        {
            services.AddSingleton<CustomerWorkflowStateMachine>();
            services.AddSingleton<CustomerWorkflowContext>();
            services.AddSingleton<PrinterConnectionViewModel>();
            services.AddSingleton<CaptureViewModel>();
            services.AddSingleton<LiveColorState>();
            services.AddSingleton<FrameSelectionViewModel>();
            services.AddSingleton<CompleteViewModel>();
            services.AddSingleton<WaitingViewModel>();
            services.AddSingleton<CustomerShellViewModel>();
            services.AddTransient<MainWindow>();
            return services;
        }
    }
}
