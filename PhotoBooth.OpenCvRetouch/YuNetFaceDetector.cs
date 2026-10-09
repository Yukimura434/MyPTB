using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace PhotoBooth.OpenCvRetouch
{
    // Callers serialize access with their existing processing gate. DirectML sessions
    // must not Run concurrently. Both backends use the same model and postprocessing.
    internal sealed class YuNetFaceDetector : IDisposable
    {
        internal const string ModelFileName = "face_detection_yunet_2023mar.onnx";
        const int InputSize = 640;
        const float ScoreThreshold = .8f;
        readonly string modelPath;
        readonly int deviceId;
        readonly float[] input = new float[3 * InputSize * InputSize];
        readonly NamedOnnxValue[] inputValues;
        InferenceSession session;
        bool disposed;
        internal string Backend { get; private set; }

        internal YuNetFaceDetector(string path, bool preferGpu = true, int deviceId = 0)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Beauty face model is missing.", path);
            modelPath = path;
            this.deviceId = deviceId;
            inputValues = new[] { NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, new[] { 1, 3, InputSize, InputSize })) };
            if (preferGpu)
            {
                try { session = CreateSession(true); Backend = "DirectML"; }
                catch (Exception error) when (CanFallback(error))
                { Trace.TraceWarning("Beauty DirectML initialization unavailable ({0}); using CPU.", error.GetType().Name); }
            }
            if (session == null) UseCpu();
        }

        InferenceSession CreateSession(bool gpu)
        {
            using (var options = new SessionOptions())
            {
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.EnableMemoryPattern = false;
                // Four CPU workers improved fallback inference in the matched
                // benchmark; doubling to eight added little while consuming more CPU.
                options.IntraOpNumThreads = gpu ? 2 : Math.Max(1, Math.Min(4, Environment.ProcessorCount));
                options.InterOpNumThreads = 1;
                // Let inference workers sleep between frames instead of competing
                // with landmark fitting, camera work and presentation while idle.
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                if (gpu) options.AppendExecutionProvider_DML(deviceId);
                return new InferenceSession(modelPath, options);
            }
        }

        void UseCpu()
        {
            session?.Dispose(); session = null;
            session = CreateSession(false); Backend = "CPU";
        }

        static bool CanFallback(Exception error) => error is OnnxRuntimeException || error is DllNotFoundException ||
            error is EntryPointNotFoundException || error is BadImageFormatException ||
            (error is TypeInitializationException && error.InnerException != null && CanFallback(error.InnerException));

        internal Rect[] Detect(Mat bgr)
        {
            if (disposed) throw new ObjectDisposedException(nameof(YuNetFaceDetector));
            if (bgr == null || bgr.Empty()) return Array.Empty<Rect>();
            if (bgr.Type() != MatType.CV_8UC3) throw new ArgumentException("Face detection requires BGR pixels.", nameof(bgr));
            var scale = Math.Min(1d, InputSize / (double)Math.Max(bgr.Width, bgr.Height));
            var size = new Size(Math.Max(1, (int)Math.Round(bgr.Width * scale)), Math.Max(1, (int)Math.Round(bgr.Height * scale)));
            using (var resized = new Mat())
            {
                Cv2.Resize(bgr, resized, size, 0, 0, InterpolationFlags.Area);
                FillInput(resized);
            }
            try { return Infer(bgr.Size(), scale); }
            catch (Exception error) when (Backend == "DirectML" && CanFallback(error))
            {
                // A lost/incompatible GPU must not disable beauty until application restart.
                // Switch once; never retry the failing GPU on each camera frame.
                Trace.TraceWarning("Beauty DirectML inference failed ({0}); switching to CPU.", error.GetType().Name);
                UseCpu();
                return Infer(bgr.Size(), scale);
            }
        }

        unsafe void FillInput(Mat image)
        {
            Array.Clear(input, 0, input.Length); // Top-left image with black right/bottom padding.
            int plane = InputSize * InputSize, width = image.Width, height = image.Height;
            for (int y = 0; y < height; y++)
            {
                byte* row = (byte*)image.Ptr(y);
                for (int x = 0; x < width; x++)
                {
                    int i = y * InputSize + x;
                    input[i] = row[x * 3]; input[plane + i] = row[x * 3 + 1]; input[2 * plane + i] = row[x * 3 + 2];
                }
            }
        }

        Rect[] Infer(Size originalSize, double scale)
        {
            using (var results = session.Run(inputValues))
            {
                var tensors = results.ToDictionary(v => v.Name, v => v.AsTensor<float>());
                var candidates = new List<Candidate>();
                foreach (int stride in new[] { 8, 16, 32 })
                {
                    int columns = InputSize / stride;
                    var cls = tensors["cls_" + stride]; var obj = tensors["obj_" + stride]; var box = tensors["bbox_" + stride];
                    for (int i = 0; i < columns * columns; i++)
                    {
                        double score = Math.Sqrt(Clamp01(cls.GetValue(i)) * Clamp01(obj.GetValue(i)));
                        if (!(score >= ScoreThreshold)) continue;
                        double cx = (i % columns + box.GetValue(i * 4)) * stride;
                        double cy = (i / columns + box.GetValue(i * 4 + 1)) * stride;
                        double w = Math.Exp(box.GetValue(i * 4 + 2)) * stride;
                        double h = Math.Exp(box.GetValue(i * 4 + 3)) * stride;
                        var bounds = ClipBox(cx - w / 2, cy - h / 2, w, h, scale, originalSize);
                        if (bounds.Width >= 20 && bounds.Height >= 20) candidates.Add(new Candidate(bounds, score));
                    }
                }
                return Suppress(candidates, .3);
            }
        }

        static double Clamp01(float value) => Math.Max(0, Math.Min(1, value));

        internal static Rect ClipBox(double x, double y, double width, double height, double scale, Size image)
        {
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ||
                new[] { x, y, width, height }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) || width <= 0 || height <= 0)
                return new Rect();
            int left = (int)Math.Max(0, Math.Min(image.Width, Math.Floor(x / scale)));
            int top = (int)Math.Max(0, Math.Min(image.Height, Math.Floor(y / scale)));
            int right = (int)Math.Max(0, Math.Min(image.Width, Math.Ceiling((x + width) / scale)));
            int bottom = (int)Math.Max(0, Math.Min(image.Height, Math.Ceiling((y + height) / scale)));
            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        internal static Rect[] Suppress(IEnumerable<Candidate> candidates, double threshold)
        {
            var kept = new List<Rect>();
            foreach (var candidate in candidates.OrderByDescending(c => c.Score).Take(5000))
            {
                bool overlap = kept.Any(other =>
                {
                    var intersection = candidate.Bounds.Intersect(other);
                    double area = (double)intersection.Width * intersection.Height;
                    double union = (double)candidate.Bounds.Width * candidate.Bounds.Height + (double)other.Width * other.Height - area;
                    return union > 0 && area / union > threshold;
                });
                if (!overlap) kept.Add(candidate.Bounds);
            }
            return kept.ToArray();
        }

        internal sealed class Candidate
        {
            internal Candidate(Rect bounds, double score) { Bounds = bounds; Score = score; }
            internal Rect Bounds { get; }
            internal double Score { get; }
        }

        public void Dispose() { if (disposed) return; disposed = true; session?.Dispose(); session = null; }
    }
}
