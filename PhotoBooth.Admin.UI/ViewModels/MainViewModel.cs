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
            EventManagerViewModel eventManager,
            EventFramePickerViewModel eventFramePicker,
            EventPresetPickerViewModel eventPresetPicker,
            FrameManagerViewModel frames,
            FrameSlotOrderViewModel frameSlotOrder,
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
                { "events", eventManager },
                { "event-frame-picker", eventFramePicker },
                { "event-preset-picker", eventPresetPicker },
                { "frames", frames },
                { "frame-slot-order", frameSlotOrder },
                { "presets", presets },
                { "beauty", beauty },
                { "printers", printers },
                { "data", diagnostics },
                { "share", localShare }
                ,{ "interface", interfacePage }
                ,{ "about", about }
            };

            NavigateCommand = new RelayCommand(Navigate);
            navigation.CurrentRouteChanged += (_, __) => ApplyRoute(navigation.CurrentRoute);

            ToggleMenuCommand = new RelayCommand(
                _ => IsMenuExpanded = !IsMenuExpanded);

            StartCustomerCommand = new AsyncCommand(
                _ => customerMode.StartAsync());

            navigation.Navigate("home");
        }

        public PageViewModel CurrentPage
        {
            get => current;
            private set => Set(ref current, value);
        }

        public string CurrentRoute => navigation.CurrentRoute;

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
            if (route != null && pages.ContainsKey(route)) navigation.Navigate(route);
        }

        private void ApplyRoute(string route)
        {
            if (route == null || !pages.TryGetValue(route, out var page)) return;
            CurrentPage = page;
            Raise(nameof(CurrentRoute));
            if (page is InterfaceViewModel interfacePage) _ = interfacePage.RefreshAsync();
            if (page is EventManagerViewModel eventPage && !eventPage.Dirty) _ = eventPage.RefreshAsync();
            if (page is BeautyViewModel beautyPage) _ = beautyPage.RefreshAsync();
            if (page is FrameManagerViewModel framePage) _ = framePage.RefreshAsync();
        }
    }
}
