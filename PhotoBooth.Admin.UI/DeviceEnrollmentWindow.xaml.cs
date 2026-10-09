using System;
using System.Windows;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI
{
    public partial class DeviceEnrollmentWindow : Window
    {
        public DeviceEnrollmentWindow(DeviceEnrollmentViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }
    }
}
