using PhotoBooth.Admin.UI.Mvvm;

namespace PhotoBooth.Admin.UI.ViewModels
{
    /// <summary>
    /// Capture history and QR recovery only. Delivery configuration lives on the
    /// separate ShareSettingsViewModel so operators are not asked to configure the
    /// system while they are looking for a customer's photos.
    /// </summary>
    public sealed class LocalShareViewModel : PageViewModel
    {
        public LocalShareViewModel(DiagnosticsViewModel library)
        {
            Library = library;
        }

        public override string Title => "Ảnh & mã QR";
        public DiagnosticsViewModel Library { get; }
    }
}
