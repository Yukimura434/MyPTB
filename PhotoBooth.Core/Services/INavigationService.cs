using System;

namespace PhotoBooth.Core.Services
{
    public interface INavigationService
    {
        string CurrentRoute { get; }
        event EventHandler CurrentRouteChanged;
        void Navigate(string route);
    }
}
