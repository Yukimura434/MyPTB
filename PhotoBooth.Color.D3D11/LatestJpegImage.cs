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

        readonly ConcurrentBag<byte[]> buffers = new ConcurrentBag<byte[]>();
        byte[] pending;
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
            ((LatestJpegImage)value).Publish(args.NewValue as byte[]);
        }

        void OnLoaded(object sender, RoutedEventArgs args)
        {
            Volatile.Write(ref active, IsVisible ? 1 : 0);
            Publish(FrameData as byte[]);
        }

        void OnUnloaded(object sender, RoutedEventArgs args)
        {
            Volatile.Write(ref active, 0);
            Interlocked.Exchange(ref pending, null);
            var frame = Interlocked.Exchange(ref ready, null);
            if (frame != null) Return(frame.Pixels);
        }

        void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            Volatile.Write(ref active, IsLoaded && IsVisible ? 1 : 0);
            if (Volatile.Read(ref active) != 0) Publish(FrameData as byte[]);
            else
            {
                Interlocked.Exchange(ref pending, null);
                var frame = Interlocked.Exchange(ref ready, null);
                if (frame != null) Return(frame.Pixels);
            }
        }

        void Publish(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0 || Volatile.Read(ref active) == 0) return;
            Interlocked.Exchange(ref pending, jpeg);
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
                    try { decoded = Decode(jpeg); }
                    catch { }
                    if (decoded == null) continue;
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
                    if (Volatile.Read(ref active) == 0) return;
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

        DecodedFrame Decode(byte[] jpeg)
        {
            BitmapSource source;
            using (var stream = new MemoryStream(jpeg, false))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
            }
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var stride = checked(source.PixelWidth * 4);
            var length = checked(stride * source.PixelHeight);
            var pixels = Rent(length);
            source.CopyPixels(pixels, stride, 0);
            return new DecodedFrame(source.PixelWidth, source.PixelHeight, stride, pixels);
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

        sealed class DecodedFrame
        {
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
