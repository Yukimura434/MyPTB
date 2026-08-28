using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace PhotoBooth.Customer.UI.Converters
{
    public sealed class PathImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var image = new BitmapImage();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    if (parameter != null && int.TryParse(parameter.ToString(), out var decodeWidth) && decodeWidth > 0)
                        image.DecodePixelWidth = decodeWidth;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                }
                return image;
            }
            catch (NotSupportedException) { return null; }
            catch (IOException) { return null; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
