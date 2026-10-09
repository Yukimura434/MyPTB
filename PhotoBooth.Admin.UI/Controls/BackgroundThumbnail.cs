using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoBooth.Admin.UI.Controls
{
    // Thumbnail crop matches the editor's portrait shelf without modifying the stored artwork.
    public sealed class BackgroundThumbnail : FrameworkElement
    {
        BitmapSource bitmap;
        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(nameof(SourcePath), typeof(string), typeof(BackgroundThumbnail), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, SourceChanged));
        public static readonly DependencyProperty PositionYProperty = DependencyProperty.Register(nameof(PositionY), typeof(double), typeof(BackgroundThumbnail), new FrameworkPropertyMetadata(.5d, FrameworkPropertyMetadataOptions.AffectsRender));
        public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
        public double PositionY { get => (double)GetValue(PositionYProperty); set => SetValue(PositionYProperty, value); }
        static void SourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        {
            var view = (BackgroundThumbnail)target;
            view.bitmap = null;
            if (!(args.NewValue is string path) || string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (path.StartsWith("/PhotoBooth.")) path = "pack://application:,,," + path;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 600; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); view.bitmap = image;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is NotSupportedException || ex is UriFormatException || ex is System.Security.SecurityException) { }
        }
        protected override void OnRender(DrawingContext drawing)
        {
            if (bitmap == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            var scale = Math.Max(ActualWidth / bitmap.PixelWidth, ActualHeight / bitmap.PixelHeight);
            var width = bitmap.PixelWidth * scale; var height = bitmap.PixelHeight * scale;
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 8, 8));
            drawing.DrawImage(bitmap, new Rect((ActualWidth - width) / 2, (ActualHeight - height) * PositionY, width, height));
            drawing.Pop();
        }
    }
}
