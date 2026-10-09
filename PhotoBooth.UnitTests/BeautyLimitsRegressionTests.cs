using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using OpenCvSharp;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using PhotoBooth.OpenCvRetouch;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class BeautyLimitsRegressionTests
    {
        [Fact]
        public async Task Legacy_values_are_capped_without_writing_on_read_and_hidden_effect_is_disabled()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
            try
            {
                var db = new SqliteDatabase(path); db.Initialize();
                var repository = new SqliteBeautySettingsRepository(db);
                var old = new BeautySettings { Enabled = true, SmoothSkin = 100, BrightenSkin = 100,
                    SkinTone = 100, BlemishRemoval = 100, DarkCircleReduction = 100, Sharpen = 91 };
                await repository.SaveAsync(old, CancellationToken.None);
                var service = new BeautySettingsService(repository);
                var read = await service.GetAsync(CancellationToken.None);
                Assert.Equal(new[] { 60, 20, 20, 70, 0, 91 }, Values(read));
                Assert.Equal(new[] { 100, 100, 100, 100, 100, 91 }, Values(await repository.GetAsync(CancellationToken.None)));
                await service.SaveAsync(old, CancellationToken.None);
                Assert.Equal(new[] { 60, 20, 20, 70, 0, 91 }, Values(await repository.GetAsync(CancellationToken.None)));
                Assert.Equal(100, old.BrightenSkin);
            }
            finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public async Task Dark_circle_only_legacy_setting_returns_original_bytes_without_loading_models()
        {
            string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "TestImages", "beauty-model.png");
            string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            try
            {
                var settings = new BeautySettings { Enabled = true, DarkCircleReduction = 100 };
                Assert.False(settings.HasEffect);
                using (var service = new OpenCvBeautyRetouchService("missing-models"))
                    Assert.False((await service.ProcessAsync(source, output, settings, CancellationToken.None)).Applied);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(output));
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }

        [Fact]
        public void Skin_feather_starts_at_zero_and_has_no_half_strength_step_or_spill()
        {
            using (var mask = new Mat(100, 100, MatType.CV_8UC1, Scalar.Black))
            {
                Cv2.Rectangle(mask, new Rect(10, 10, 80, 80), Scalar.White, -1);
                Cv2.Rectangle(mask, new Rect(45, 30, 10, 40), Scalar.Black, -1);
                FaceSkinMaskBuilder.FeatherSkinInside(mask, 10);
                Assert.Equal(0, mask.At<byte>(20, 9));
                Assert.Equal(0, mask.At<byte>(20, 10));
                Assert.Equal(255, mask.At<byte>(25, 25));
                Assert.Equal(0, mask.At<byte>(50, 45));
                Assert.Equal(0, mask.At<byte>(50, 44));
                for (int x = 10; x < 24; x++)
                {
                    int a = mask.At<byte>(25, x), b = mask.At<byte>(25, x + 1);
                    Assert.InRange(b - a, 0, 40);
                }
            }
        }

        static int[] Values(BeautySettings s) => new[] { s.SmoothSkin, s.BrightenSkin, s.SkinTone, s.BlemishRemoval, s.DarkCircleReduction, s.Sharpen };
    }
}
