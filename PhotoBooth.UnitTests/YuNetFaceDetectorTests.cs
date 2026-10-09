using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PhotoBooth.Core.Models;
using PhotoBooth.OpenCvRetouch;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class YuNetFaceDetectorTests
    {
        static string Assets => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Beauty");
        static string Model => Path.Combine(Assets, YuNetFaceDetector.ModelFileName);
        static string Photo(string name) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "TestImages", name);

        [Theory]
        [InlineData("beauty-model.png", 1)]
        [InlineData("model.png", 1)]
        [InlineData("harry-potter.jpg", 3)]
        public void Cpu_backend_detects_real_samples_in_original_coordinates(string name, int expected)
        {
            Assert.Equal(4, IntPtr.Size);
            using (var detector = new YuNetFaceDetector(Model, false))
            using (var image = Cv2.ImRead(Photo(name)))
            {
                Assert.Equal("CPU", detector.Backend);
                var faces = detector.Detect(image);
                Assert.Equal(expected, faces.Length);
                Assert.All(faces, face =>
                {
                    Assert.InRange(face.Left, 0, image.Width - 1); Assert.InRange(face.Top, 0, image.Height - 1);
                    Assert.InRange(face.Right, face.Left + 20, image.Width); Assert.InRange(face.Bottom, face.Top + 20, image.Height);
                });
                using (var blank = new Mat(image.Size(), MatType.CV_8UC3, Scalar.All(127)))
                    Assert.Empty(detector.Detect(blank)); // No cached face after the person leaves.
            }
        }

        [Fact]
        public void Unavailable_gpu_falls_back_to_same_model_on_cpu()
        {
            using (var detector = new YuNetFaceDetector(Model, true, int.MaxValue))
            using (var image = Cv2.ImRead(Photo("model.png")))
            {
                Assert.Equal("CPU", detector.Backend);
                Assert.Single(detector.Detect(image));
            }
        }

        [Fact]
        public void Default_backend_and_cpu_agree_on_multiple_faces()
        {
            using (var accelerated = new YuNetFaceDetector(Model))
            using (var cpu = new YuNetFaceDetector(Model, false))
            using (var image = Cv2.ImRead(Photo("harry-potter.jpg")))
            {
                var actual = accelerated.Detect(image).OrderBy(r => r.X).ToArray();
                var expected = cpu.Detect(image).OrderBy(r => r.X).ToArray();
                Assert.Equal(expected.Length, actual.Length);
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.InRange(Math.Abs(actual[i].X - expected[i].X), 0, 2);
                    Assert.InRange(Math.Abs(actual[i].Y - expected[i].Y), 0, 2);
                    Assert.InRange(Math.Abs(actual[i].Width - expected[i].Width), 0, 2);
                    Assert.InRange(Math.Abs(actual[i].Height - expected[i].Height), 0, 2);
                }
            }
        }

        [Fact]
        public void Invalid_and_off_image_predictions_cannot_form_a_mask()
        {
            var size = new Size(1000, 800);
            Assert.Equal(new Rect(), YuNetFaceDetector.ClipBox(double.NaN, 0, 10, 10, .5, size));
            Assert.Equal(0, YuNetFaceDetector.ClipBox(900, 900, 10, 10, .5, size).Width);
            Assert.Equal(new Rect(0, 0, 40, 60), YuNetFaceDetector.ClipBox(-10, -10, 30, 40, .5, size));
            var candidates = new[] { new YuNetFaceDetector.Candidate(new Rect(10, 10, 100, 100), .9),
                new YuNetFaceDetector.Candidate(new Rect(15, 15, 100, 100), .85),
                new YuNetFaceDetector.Candidate(new Rect(200, 10, 100, 100), .95) };
            Assert.Equal(2, YuNetFaceDetector.Suppress(candidates, .3).Length);
        }

        [Fact]
        public async Task Previously_missed_tilted_portrait_is_processed_by_both_services()
        {
            var source = Photo("model.png"); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
            var settings = new BeautySettings { Enabled = true, SmoothSkin = 50, BrightenSkin = 30 };
            try
            {
                using (var still = new OpenCvBeautyRetouchService(Assets))
                using (var live = new OpenCvLiveBeautyPreviewService(Assets))
                using (var original = Cv2.ImRead(source))
                {
                    var result = await still.ProcessAsync(source, output, settings, CancellationToken.None);
                    Assert.True(result.Applied); Assert.Equal(1, result.FacesDetected);
                    var pixels = await OpenCvBeautyRetouchTests.WaitForBeauty(live, File.ReadAllBytes(source), settings);
                    Assert.True(pixels.BeautyApplied); Assert.True(pixels.Pixels.Length > 0);
                }
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }
}
