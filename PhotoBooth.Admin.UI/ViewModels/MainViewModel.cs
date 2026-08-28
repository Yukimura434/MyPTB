using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly INavigationService navigation;
        private readonly Dictionary<string, PageViewModel> pages;

        private PageViewModel current;
        private bool expanded = true;
        private string message;

        public MainViewModel(
            INavigationService navigation,
            HomeViewModel home,
            FrameManagerViewModel frames,
            PresetManagerViewModel presets,
            BeautyViewModel beauty,
            PrinterManagerViewModel printers,
            DiagnosticsViewModel diagnostics,
            LocalShareViewModel localShare,
            InterfaceViewModel interfacePage,
            AboutViewModel about,
            Services.ICustomerModeController customerMode)
        {
            this.navigation = navigation;

            pages = new Dictionary<string, PageViewModel>(
                StringComparer.OrdinalIgnoreCase)
            {
                { "home", home },
                { "frames", frames },
                { "presets", presets },
                { "beauty", beauty },
                { "printers", printers },
                { "data", diagnostics },
                { "share", localShare }
                ,{ "interface", interfacePage }
                ,{ "about", about }
            };

            NavigateCommand = new RelayCommand(Navigate);

            ToggleMenuCommand = new RelayCommand(
                _ => IsMenuExpanded = !IsMenuExpanded);

            StartCustomerCommand = new AsyncCommand(
                _ => customerMode.StartAsync());

            Navigate("home");
        }

        public PageViewModel CurrentPage
        {
            get => current;
            private set => Set(ref current, value);
        }

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

        public double MenuWidth => expanded ? 210 : 68;

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

        private void Navigate(object parameter)
        {
            var route = parameter?.ToString();

            if (route != null &&
                pages.TryGetValue(route, out var page))
            {
                navigation.Navigate(route);
                CurrentPage = page;
                if (page is InterfaceViewModel interfacePage)
                    _ = interfacePage.RefreshAsync();
            }
        }
    }
}
