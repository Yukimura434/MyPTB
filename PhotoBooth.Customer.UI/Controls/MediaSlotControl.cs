using System;
using System.IO;
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
        readonly MediaElement video = new MediaElement { Stretch = Stretch.Fill, LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true };
        FrameworkElement media;
        Point previous;
        bool dragging;
        double naturalWidth;
        double naturalHeight;

        public MediaSlotControl()
        {
            ClipToBounds = true; Background = Brushes.Transparent; Cursor = Cursors.Hand;
            video.MediaOpened += (s, e) => { naturalWidth = video.NaturalVideoWidth; naturalHeight = video.NaturalVideoHeight; video.Play(); Render(); };
            video.MediaEnded += (s, e) => { video.Position = TimeSpan.Zero; video.Play(); };
            SizeChanged += (s, e) => Render();
            MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up; LostMouseCapture += (s, e) => dragging = false;
            MouseWheel += Wheel;
            Loaded += (s, e) => { if (IsVideo && video.Source != null) video.Play(); };
            Unloaded += (s, e) => video.Stop();
        }

        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(nameof(SourcePath), typeof(string), typeof(MediaSlotControl), new PropertyMetadata(null, SourceChanged));
        public static readonly DependencyProperty IsVideoProperty = DependencyProperty.Register(nameof(IsVideo), typeof(bool), typeof(MediaSlotControl), new PropertyMetadata(false, SourceChanged));
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(MediaSlotControl), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TransformChanged));
        public static readonly DependencyProperty CenterXProperty = DependencyProperty.Register(nameof(CenterX), typeof(double), typeof(MediaSlotControl), new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TransformChanged));
        public static readonly DependencyProperty CenterYProperty = DependencyProperty.Register(nameof(CenterY), typeof(double), typeof(MediaSlotControl), new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TransformChanged));
        public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(nameof(SelectCommand), typeof(ICommand), typeof(MediaSlotControl));
        public static readonly DependencyProperty SelectCommandParameterProperty = DependencyProperty.Register(nameof(SelectCommandParameter), typeof(object), typeof(MediaSlotControl));

        public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
        public bool IsVideo { get => (bool)GetValue(IsVideoProperty); set => SetValue(IsVideoProperty, value); }
        public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
        public double CenterX { get => (double)GetValue(CenterXProperty); set => SetValue(CenterXProperty, value); }
        public double CenterY { get => (double)GetValue(CenterYProperty); set => SetValue(CenterYProperty, value); }
        public ICommand SelectCommand { get => (ICommand)GetValue(SelectCommandProperty); set => SetValue(SelectCommandProperty, value); }
        public object SelectCommandParameter { get => GetValue(SelectCommandParameterProperty); set => SetValue(SelectCommandParameterProperty, value); }

        static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MediaSlotControl)d).LoadSource();
        static void TransformChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MediaSlotControl)d).Render();

        void LoadSource()
        {
            Children.Clear(); video.Stop(); image.Source = null; video.Source = null; naturalWidth = naturalHeight = 0;
            media = IsVideo ? (FrameworkElement)video : image; Children.Add(media);
            if (string.IsNullOrWhiteSpace(SourcePath)) return;
            try
            {
                var uri = new Uri(SourcePath, UriKind.Absolute);
                if (IsVideo) { video.Source = uri; if (IsLoaded) video.Play(); }
                else
                {
                    var bitmap = new BitmapImage();
                    using (var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        // This is an interactive screen preview. Keeping one full camera image
                        // per frame slot exhausts the x86 address space after repeated sessions.
                        // The original file remains untouched and final composition still uses it.
                        bitmap.DecodePixelWidth = 1280;
                        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    }
                    image.Source = bitmap; naturalWidth = bitmap.PixelWidth; naturalHeight = bitmap.PixelHeight; Render();
                }
            }
            catch { naturalWidth = naturalHeight = 0; }
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
