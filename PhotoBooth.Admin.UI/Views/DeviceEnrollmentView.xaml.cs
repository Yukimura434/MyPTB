using System;
using System.Windows;
using System.Windows.Controls;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI.Views
{
    public partial class DeviceEnrollmentView : UserControl
    {
        DeviceEnrollmentViewModel attachedViewModel;

        public DeviceEnrollmentView()
        {
            InitializeComponent();
            Loaded += DeviceEnrollmentView_Loaded;
            Unloaded += DeviceEnrollmentView_Unloaded;
            DataContextChanged += DeviceEnrollmentView_DataContextChanged;
        }

        async void DeviceEnrollmentView_Loaded(object sender, RoutedEventArgs e)
        {
            Attach(DataContext as DeviceEnrollmentViewModel);
            if (attachedViewModel != null) await attachedViewModel.RefreshAsync();
        }

        void DeviceEnrollmentView_Unloaded(object sender, RoutedEventArgs e) => Attach(null);

        void DeviceEnrollmentView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsLoaded) Attach(e.NewValue as DeviceEnrollmentViewModel);
        }

        void Attach(DeviceEnrollmentViewModel viewModel)
        {
            if (ReferenceEquals(attachedViewModel, viewModel)) return;
            if (attachedViewModel != null) attachedViewModel.PasswordClearRequested -= PasswordClearRequested;
            attachedViewModel = viewModel;
            if (attachedViewModel != null) attachedViewModel.PasswordClearRequested += PasswordClearRequested;
        }

        void PasswordClearRequested(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ActivationPassword.Clear()));
                return;
            }
            ActivationPassword.Clear();
        }

        void ActivationPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is DeviceEnrollmentViewModel viewModel)
                viewModel.Password = ((PasswordBox)sender).Password;
        }
    }
}
