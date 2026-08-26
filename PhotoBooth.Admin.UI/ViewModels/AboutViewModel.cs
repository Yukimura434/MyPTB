using System.Diagnostics;
using System.Reflection;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class AboutViewModel : PageViewModel
    {
        public AboutViewModel()
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutViewModel).Assembly;
            var productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
            var version = string.IsNullOrWhiteSpace(productVersion)
                ? assembly.GetName().Version?.ToString() ?? "Unknown"
                : productVersion;
            var metadataIndex = version.IndexOf('+');
            Version = metadataIndex >= 0 ? version.Substring(0, metadataIndex) : version;
        }

        public override string Title => "About";

        public string ProductName => "MiuCamezaPTB";

        public string Version { get; }
    }
}
