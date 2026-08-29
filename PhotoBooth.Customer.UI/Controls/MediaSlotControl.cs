using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Customer.UI.Controls
{
    /// <summary>A clipped image/video viewport with shared cursor-anchored Cover pan and zoom.</summary>
    public sealed class MediaSlotControl : Canvas
    {
        readonly Image image = new Image { Stretch = Stretch.Fill };
        FrameworkElement media;
        Point previous;
        bool dragging;
        double naturalWidth;
        double naturalHeight;

        public MediaSlotControl()
        {
            ClipToBounds = true; Background = Brushes.Transparent; Cursor = Cursors.Hand;
            SizeChanged += (s, e) => Render();
            MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up; LostMouseCapture += (s, e) => dragging = false;
            MouseWheel += Wheel;
            Loaded += (s, e) => LoadSource();
            Unloaded += (s, e) => ReleaseMedia();
        }

        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(nameof(SourcePath), typeof(string), typeof(MediaSlotControl), new PropertyMetadata(null, SourceChanged));
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(MediaSlotControl), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TransformChanged));
        public static readonly DependencyProperty CenterXProperty = DependencyProperty.Register(nameof(CenterX), typeof(double), typeof(MediaSlotControl), new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TransformChanged));
        public static readonly DependencyProperty CenterYProperty = DependencyProperty.Register(nameof(CenterY), typeof(double), typeof(MediaSlotControl), new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TransformChanged));
        public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(nameof(SelectCommand), typeof(ICommand), typeof(MediaSlotControl));
        public static readonly DependencyProperty SelectCommandParameterProperty = DependencyProperty.Register(nameof(SelectCommandParameter), typeof(object), typeof(MediaSlotControl));

        public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
        public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
        public double CenterX { get => (double)GetValue(CenterXProperty); set => SetValue(CenterXProperty, value); }
        public double CenterY { get => (double)GetValue(CenterYProperty); set => SetValue(CenterYProperty, value); }
        public ICommand SelectCommand { get => (ICommand)GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }
        public object SelectCommandParameter { get => GetValue(SelectCommandParameterProperty); set => SetValue(SelectCommandParameterProperty, value); }

        static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MediaSlotControl)d).LoadSource();
        static void TransformChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MediaSlotControl)d).Render();

        void LoadSource()
        {
            ReleaseMedia();
            var path = SourcePath;
            media = image;
            Children.Add(media);
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var uri = new Uri(path, UriKind.Absolute);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = uri; bitmap.EndInit(); bitmap.Freeze();
                image.Source = bitmap; naturalWidth = bitmap.PixelWidth; naturalHeight = bitmap.PixelHeight; Render();
            }
            catch { naturalWidth = naturalHeight = 0; }
        }

        void ReleaseMedia()
        {
            dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            image.Source = null;
            Children.Clear();
            media = null;
            naturalWidth = naturalHeight = 0;
        }

        void Down(object sender, MouseButtonEventArgs e)
        {
            SelectCommand?.Execute(SelectCommandParameter);
            if (naturalWidth <= 0 || naturalHeight <= 0) return;
            dragging = true; previous = e.GetPosition(this); CaptureMouse(); e.Handled = true;
        }

        void Move(object sender, MouseEventArgs e)
        {
            if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(this); var layout = Layout();
            var left = layout.Left + point.X - previous.X; var top = layout.Top + point.Y - previous.Y;
            CenterX = (ActualWidth / 2d - left) / Math.Max(1, layout.Width);
            CenterY = (ActualHeight / 2d - top) / Math.Max(1, layout.Height);
            previous = point; NormalizeAndRender(); e.Handled = true;
        }

        void Up(object sender, MouseButtonEventArgs e) { if (!dragging) return; dragging = false; ReleaseMouseCapture(); e.Handled = true; }

        void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (naturalWidth <= 0 || naturalHeight <= 0) return;
            var point = e.GetPosition(this); var before = Layout();
            var mediaX = (point.X - before.Left) / Math.Max(before.Scale, double.Epsilon);
            var mediaY = (point.Y - before.Top) / Math.Max(before.Scale, double.Epsilon);
            var nextZoom = MediaTransformGeometry.Clamp(Zoom * (e.Delta > 0 ? 1.1d : 1d / 1.1d), 1d, 2d);
            if (Math.Abs(nextZoom - Zoom) < 0.000001) { e.Handled = true; return; }
            Zoom = nextZoom;
            var next = Layout();
            CenterX = (ActualWidth / 2d - (point.X - mediaX * next.Scale)) / Math.Max(1, next.Width);
            CenterY = (ActualHeight / 2d - (point.Y - mediaY * next.Scale)) / Math.Max(1, next.Height);
            NormalizeAndRender(); e.Handled = true;
        }

        MediaLayout Layout() => MediaTransformGeometry.Calculate(naturalWidth, naturalHeight, ActualWidth, ActualHeight, Zoom, CenterX, CenterY);
        void NormalizeAndRender()
        {
            var value = Layout();
            SetCurrentValue(ZoomProperty, MediaTransformGeometry.Clamp(Zoom, 1, 2));
            SetCurrentValue(CenterXProperty, value.CenterX); SetCurrentValue(CenterYProperty, value.CenterY); Render(value);
        }
        void Render() => Render(Layout());
        void Render(MediaLayout value)
        {
            if (media == null || naturalWidth <= 0 || naturalHeight <= 0) return;
            media.Width = value.Width; media.Height = value.Height;
            Canvas.SetLeft(media, value.Left); Canvas.SetTop(media, value.Top);
        }
    }
}
