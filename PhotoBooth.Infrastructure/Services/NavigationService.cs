using System;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class NavigationService : INavigationService
    {
        public string CurrentRoute { get; private set; }
        public event EventHandler CurrentRouteChanged;
        public void Navigate(string route) { CurrentRoute = route; CurrentRouteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
