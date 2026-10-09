using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Admin.UI.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly INavigationService navigation;
        private readonly IPcSessionContext sessionContext;
        private readonly Services.ICustomerModeController customerMode;
        private readonly Dictionary<string, PageViewModel> pages;

        private PageViewModel current;
        private bool expanded = true;
        private string message;

        public MainViewModel(
            INavigationService navigation,
            HomeViewModel home,
            FrameManagerViewModel frames,
            FrameSlotOrderViewModel frameSlotOrder,
            PresetManagerViewModel presets,
            BeautyViewModel beauty,
            PrinterManagerViewModel printers,
            LocalShareViewModel localShare,
            ShareSettingsViewModel shareSettings,
            AnalyticsViewModel analytics,
            InterfaceViewModel interfacePage,
            AboutViewModel about,
            DeviceEnrollmentViewModel deviceEnrollment,
            Services.ICustomerModeController customerMode,
            IPcSessionContext sessionContext)
        {
            this.navigation = navigation;
            this.sessionContext = sessionContext;
            this.customerMode = customerMode;

            pages = new Dictionary<string, PageViewModel>(
                StringComparer.OrdinalIgnoreCase)
            {
                { "home", home },
                { "frames", frames },
                { "frame-slot-order", frameSlotOrder },
                { "presets", presets },
                { "beauty", beauty },
                { "printers", printers },
                { "data", analytics },
                { "share", localShare },
                { "share-settings", shareSettings }
                ,{ "interface", interfacePage }
                ,{ "about", about }
                ,{ "device", deviceEnrollment }
            };

            NavigateCommand = new RelayCommand(Navigate);
            presets.LiveViewRequested += (_, __) => Navigate("home");
            navigation.CurrentRouteChanged += (_, __) => ApplyRoute(navigation.CurrentRoute);

            ToggleMenuCommand = new RelayCommand(
                _ => IsMenuExpanded = !IsMenuExpanded);

            StartCustomerCommand = new AsyncCommand(
                _ => customerMode.StartAsync());
            SignOutCommand = new RelayCommand(_ => SignOutRequested?.Invoke(this, EventArgs.Empty));
            sessionContext.Changed += (_, __) => RefreshSession();
            customerMode.StateChanged += (_, __) => { Raise(nameof(CustomerModeActionText)); Raise(nameof(IsCustomerRunning)); };

            navigation.Navigate("home");
        }

        public PageViewModel CurrentPage
        {
            get => current;
            private set { if(Set(ref current, value)) { Raise(nameof(StandardShellVisibility)); Raise(nameof(MenuWidth)); Raise(nameof(MinimumWindowWidth)); Raise(nameof(MinimumWindowHeight)); } }
        }

        public double MinimumWindowWidth => current is InterfaceViewModel ? 480 : 1000;
        public double MinimumWindowHeight => current is InterfaceViewModel ? 520 : 700;
        public Visibility StandardShellVisibility => current is InterfaceViewModel ? Visibility.Collapsed : Visibility.Visible;

        public string CurrentRoute => navigation.CurrentRoute;

        public string SessionDisplayName => sessionContext.Current?.DisplayName ?? "Đã khóa";

        public string SessionRoleDisplayName => sessionContext.Current?.RoleDisplayName ?? string.Empty;

        public string SessionModeLabel
        {
            get
            {
                switch (sessionContext.Current?.AuthenticationMode)
                {
                    case PcAuthenticationMode.OnlineDeviceBound: return "Đã xác thực online cùng thiết bị";
                    case PcAuthenticationMode.OfflineCache: return "Xác thực ngoại tuyến từ cache";
                    case PcAuthenticationMode.DevelopmentAccount: return "Tài khoản test cục bộ";
                    default: return "Phiên đã khóa";
                }
            }
        }

        public Visibility LibraryVisibility => ConfigurationVisibility;

        public Visibility DeviceConfigurationVisibility => sessionContext.Current?.CanConfigureDevice == true ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ConfigurationVisibility => sessionContext.Current?.CanManageConfiguration == true ? Visibility.Visible : Visibility.Collapsed;

        public bool IsMenuExpanded
        {
            get => expanded;
            set
            {
                if (Set(ref expanded, value))
                {
                    Raise(nameof(MenuWidth));
                    Raise(nameof(MenuTextVisibility));
                }
            }
        }

        public double MenuWidth => current is InterfaceViewModel ? 0 : expanded ? 224 : 72;

        public Visibility MenuTextVisibility =>
            expanded ? Visibility.Visible : Visibility.Collapsed;

        public string Message
        {
            get => message;
            set
            {
                Set(ref message, value);
                Raise(nameof(HasMessage));
            }
        }

        public bool HasMessage =>
            !string.IsNullOrWhiteSpace(Message);

        public ICommand NavigateCommand { get; }

        public ICommand ToggleMenuCommand { get; }

        public ICommand StartCustomerCommand { get; }
        public string CustomerModeActionText => customerMode.ActionText;
        public bool IsCustomerRunning => customerMode.IsCustomerRunning;

        public ICommand SignOutCommand { get; }

        public event EventHandler SignOutRequested;

        public void RequestLock() => SignOutRequested?.Invoke(this, EventArgs.Empty);

        private void Navigate(object parameter)
        {
            var route = parameter?.ToString();
            if (route != null && pages.ContainsKey(route) && CanNavigate(route))
            {
                navigation.Navigate(route);
            }
        }

        bool CanNavigate(string route) => route == "home" || route == "about" ||
            sessionContext.Current?.CanManageConfiguration == true ||
            (sessionContext.Current?.CanConfigureDevice == true && (route == "printers" || route == "interface" || route == "beauty"));

        void RefreshSession()
        {
            Raise(nameof(SessionDisplayName));
            Raise(nameof(SessionRoleDisplayName));
            Raise(nameof(SessionModeLabel));
            Raise(nameof(ConfigurationVisibility));
            Raise(nameof(DeviceConfigurationVisibility));
            Raise(nameof(LibraryVisibility));
            if (!CanNavigate(CurrentRoute)) navigation.Navigate("home");
        }

        private void ApplyRoute(string route)
        {
            if (route == null || !pages.TryGetValue(route, out var page)) { navigation.Navigate("home"); return; }
            if (!CanNavigate(route)) { navigation.Navigate("home"); return; }
            CurrentPage = page;
            Raise(nameof(CurrentRoute));
            if (page is InterfaceViewModel interfacePage) _ = interfacePage.RefreshAsync();
            if (page is HomeViewModel homePage) _ = homePage.RefreshEventCatalogAsync();
            if (page is PresetManagerViewModel presetPage) _ = presetPage.RefreshAsync();
            if (page is BeautyViewModel beautyPage) _ = beautyPage.RefreshAsync();
            if (page is FrameManagerViewModel framePage) _ = framePage.RefreshAsync();
            if (page is DeviceEnrollmentViewModel devicePage) _ = devicePage.RefreshAsync();
            if (page is AnalyticsViewModel analyticsPage) _ = analyticsPage.RefreshAsync();
        }
    }
}
