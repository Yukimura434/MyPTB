using System.IO;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class DesktopApplicationDefaultsTests
    {
        [Fact]
        public void Manager_and_customer_share_the_same_versioned_data_store()
        {
            var local = Path.Combine("C:\\", "Users", "test", "AppData", "Local");

            var result = DesktopApplicationDefaults.ResolveDataDirectory(null, local);

            Assert.Equal(Path.Combine(local, "PhotoBooth", "2.0.0-alpha", "Data"), result);
            Assert.Equal("photobooth-v3.db", DesktopApplicationDefaults.DatabaseFileName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Missing_worker_override_uses_the_production_endpoint(string configured)
        {
            Assert.Equal("https://api.miuphotobooth.com", DesktopApplicationDefaults.ResolvePhotoApiBaseUrl(configured));
        }

        [Fact]
        public void Explicit_worker_override_is_trimmed_and_preserved()
        {
            Assert.Equal("https://worker.example.test", DesktopApplicationDefaults.ResolvePhotoApiBaseUrl("  https://worker.example.test/  "));
        }
    }
}
