using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoBooth.Color.D3D11
{
    /// <summary>
    /// Decodes only the newest JPEG on a worker thread and updates one reusable
    /// WriteableBitmap. Slow rendering therefore cannot queue old Live View frames.
    /// </summary>
    public sealed class LatestJpegImage : Image
    {
        public event EventHandler FramePresented;
        public static readonly DependencyProperty FrameDataProperty = DependencyProperty.Register(
            nameof(FrameData), typeof(object), typeof(LatestJpegImage), new PropertyMetadata(null, OnFrame));
        public static readonly DependencyProperty ColorPresetProperty = DependencyProperty.Register(
            nameof(ColorPreset), typeof(LivePreviewLut), typeof(LatestJpegImage), new PropertyMetadata(null, OnPreset));
        public LivePreviewLut ColorPreset { get => (LivePreviewLut)GetValue(ColorPresetProperty); set => SetValue(ColorPresetProperty, value); }
        static void OnPreset(DependencyObject value, DependencyPropertyChangedEventArgs args)
        {
            var image = (LatestJpegImage)value;
            image.Publish(image.FrameData);
        }

        readonly ConcurrentBag<byte[]> buffers = new ConcurrentBag<byte[]>();
        PendingInput pending;
        long inputVersion;
        DecodedFrame ready;
        int decoding;
        int presenting;
        int active;
        WriteableBitmap bitmap;

        public LatestJpegImage()
        {
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        public object FrameData
        {
            get => GetValue(FrameDataProperty);
            set => SetValue(FrameDataProperty, value);
        }

        static void OnFrame(DependencyObject value, DependencyPropertyChangedEventArgs args)
        {
            ((LatestJpegImage)value).Publish(args.NewValue);
        }

        void OnLoaded(object sender, RoutedEventArgs args)
        {
            Volatile.Write(ref active, IsVisible ? 1 : 0);
            Publish(FrameData);
        }

        void OnUnloaded(object sender, RoutedEventArgs args)
        {
            Volatile.Write(ref active, 0);
            Interlocked.Increment(ref inputVersion);
            Interlocked.Exchange(ref pending, null);
            var frame = Interlocked.Exchange(ref ready, null);
            if (frame != null) Return(frame.Pixels);
        }

        void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            Volatile.Write(ref active, IsLoaded && IsVisible ? 1 : 0);
            if (Volatile.Read(ref active) != 0) Publish(FrameData);
            else
            {
                Interlocked.Exchange(ref pending, null);
                var frame = Interlocked.Exchange(ref ready, null);
                if (frame != null) Return(frame.Pixels);
            }
        }

        void Publish(object value)
        {
            long version = Interlocked.Increment(ref inputVersion);
            if (value == null) { Interlocked.Exchange(ref pending, null); var old = Interlocked.Exchange(ref ready, null); if (old != null) Return(old.Pixels); Source = null; bitmap = null; return; }
            if (Volatile.Read(ref active) == 0) return;
            Interlocked.Exchange(ref pending, new PendingInput { Value = value, Version = version, Preset = ColorPreset });
            StartDecoder();
        }

        void StartDecoder()
        {
            if (Interlocked.CompareExchange(ref decoding, 1, 0) != 0) return;
            _ = Task.Run((Action)DecodeLoop);
        }

        void DecodeLoop()
        {
            try
            {
                while (Volatile.Read(ref active) != 0)
                {
                    var jpeg = Interlocked.Exchange(ref pending, null);
                    if (jpeg == null) break;
                    DecodedFrame decoded = null;
                    try { decoded = DecodeInput(jpeg.Value, jpeg.Preset); }
                    catch { }
                    if (decoded == null) continue;
                    decoded.Version = jpeg.Version;
                    if (jpeg.Version != Interlocked.Read(ref inputVersion)) { Return(decoded.Pixels); continue; }
                    if (Volatile.Read(ref active) == 0) { Return(decoded.Pixels); continue; }
                    var old = Interlocked.Exchange(ref ready, decoded);
                    if (old != null) Return(old.Pixels);
                    QueuePresenter();
                }
            }
            finally
            {
                Interlocked.Exchange(ref decoding, 0);
                if (Volatile.Read(ref active) != 0 && Volatile.Read(ref pending) != null) StartDecoder();
            }
        }

        void QueuePresenter()
        {
            if (Interlocked.CompareExchange(ref presenting, 1, 0) != 0) return;
            Dispatcher.BeginInvoke(new Action(PresentLatest), DispatcherPriority.Render);
        }

        void PresentLatest()
        {
            try
            {
                var frame = Interlocked.Exchange(ref ready, null);
                if (frame == null) return;
                try
                {
                    if (Volatile.Read(ref active) == 0 || frame.Version != Interlocked.Read(ref inputVersion)) return;
                    if (frame.Source != null) { bitmap = null; Source = frame.Source; FramePresented?.Invoke(this, EventArgs.Empty); return; }
                    if (bitmap == null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
                    {
                        bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                        Source = bitmap;
                    }
                    bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
                    FramePresented?.Invoke(this, EventArgs.Empty);
                }
                finally { Return(frame.Pixels); }
            }
            finally
            {
                Interlocked.Exchange(ref presenting, 0);
                if (Volatile.Read(ref active) != 0 && Volatile.Read(ref ready) != null) QueuePresenter();
            }
        }

        DecodedFrame DecodeInput(object value, LivePreviewLut preset)
        {
            var source = value as BitmapSource;
            if (source != null && preset == null) return new DecodedFrame(source);
            if (source == null)
            {
                using (var stream = new MemoryStream((byte[])value, false))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    source = decoder.Frames[0];
                }
            }
            // Bound preview cost only. Camera JPEG, Beauty input and captured files stay intact.
            var scale = preset == null ? 1d : Math.Min(1d, 640d / Math.Max(source.PixelWidth, source.PixelHeight));
            if (scale < 1) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var stride = checked(source.PixelWidth * 4);
            var length = checked(stride * source.PixelHeight);
            var pixels = Rent(length);
            try
            {
                source.CopyPixels(pixels, stride, 0);
                try { preset?.Apply(pixels, source.PixelWidth, source.PixelHeight, stride, CancellationToken.None); }
                catch { source.CopyPixels(pixels, stride, 0); } // Present original preview if coloring fails.
                if (preset != null)
                {
                    // Publish an immutable image like the Beauty path. WritePixels can
                    // block the UI behind the compositor's write lock for a live surface.
                    var colored = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                    colored.Freeze();
                    Return(pixels);
                    return new DecodedFrame(colored);
                }
                return new DecodedFrame(source.PixelWidth, source.PixelHeight, stride, pixels);
            }
            catch { Return(pixels); throw; }
        }

        byte[] Rent(int length)
        {
            while (buffers.TryTake(out var value)) if (value.Length == length) return value;
            return new byte[length];
        }

        void Return(byte[] value)
        {
            if (value != null && buffers.Count < 3) buffers.Add(value);
        }

        sealed class PendingInput { internal object Value; internal long Version; internal LivePreviewLut Preset; }

        sealed class DecodedFrame
        {
            internal DecodedFrame(BitmapSource source) { Source = source; }
            internal BitmapSource Source { get; }
            internal long Version { get; set; }
            internal DecodedFrame(int width, int height, int stride, byte[] pixels)
            {
                Width = width; Height = height; Stride = stride; Pixels = pixels;
            }
            internal int Width { get; }
            internal int Height { get; }
            internal int Stride { get; }
            internal byte[] Pixels { get; }
        }
    }
}
