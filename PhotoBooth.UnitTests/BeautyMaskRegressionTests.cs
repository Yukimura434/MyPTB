using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PhotoBooth.Core.Models;
using PhotoBooth.OpenCvRetouch;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class BeautyMaskRegressionTests
    {
        [Theory]
        [InlineData("SmoothSkin")]
        [InlineData("BrightenSkin")]
        [InlineData("SkinTone")]
        [InlineData("Sharpen")]
        [InlineData("BlemishRemoval")]
        public async Task Each_masked_effect_changes_pixels_beyond_jpeg_encoding(string property)
        {
            Assert.Equal(4, IntPtr.Size);
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "TestImages", "harry-potter.jpg");
            var assets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Beauty");
            var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
            var settings = new BeautySettings { Enabled = true };
            typeof(BeautySettings).GetProperty(property).SetValue(settings, 100);
            try
            {
                using (var original = Cv2.ImRead(source, ImreadModes.Color))
                using (var still = new OpenCvBeautyRetouchService(assets))
                using (var live = new OpenCvLiveBeautyPreviewService(assets))
                {
                    var result = await still.ProcessAsync(source, output, settings, CancellationToken.None);
                    Assert.True(result.Applied);
                    Assert.True(result.FacesDetected > 0);
                    // Compare equal JPEG encodings, not input bytes: re-encoding alone
                    // used to let empty masks pass every image-changed assertion.
                    using (var baseline = Cv2.ImDecode(original.ImEncode(".jpg"), ImreadModes.Color))
                    using (var processed = Cv2.ImRead(output, ImreadModes.Color))
                        AssertChanged(baseline, processed, property + " still");

                }
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }

        static void AssertChanged(Mat baseline, Mat processed, string effect)
        {
            Assert.Equal(baseline.Size(), processed.Size());
            var difference = Cv2.Norm(baseline, processed, NormTypes.L1);
            Assert.True(difference > 0, effect + " made no pixel change beyond JPEG encoding; the effect mask may be empty.");
        }
    }
}
