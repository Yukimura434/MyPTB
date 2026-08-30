using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class FileStorageServiceTests
    {
        [Fact]
        public async Task Relative_file_is_saved_inside_the_configured_root()
        {
            var parent = NewDirectory();
            var root = Path.Combine(parent, "Data");
            try
            {
                var service = new FileStorageService(new ApplicationOptions { DataDirectory = root });
                using (var content = new MemoryStream(Encoding.UTF8.GetBytes("photo")))
                {
                    var saved = await service.SaveAsync(Path.Combine("Frames", "frame.txt"), content, CancellationToken.None);
                    Assert.Equal(Path.Combine(root, "Frames", "frame.txt"), saved, StringComparer.OrdinalIgnoreCase);
                    Assert.Equal("photo", File.ReadAllText(saved));
                }
            }
            finally { Directory.Delete(parent, true); }
        }

        [Fact]
        public async Task Sibling_with_same_prefix_cannot_escape_storage_root()
        {
            var parent = NewDirectory();
            var root = Path.Combine(parent, "Data");
            try
            {
                var service = new FileStorageService(new ApplicationOptions { DataDirectory = root });
                using (var content = new MemoryStream(new byte[] { 1 }))
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        service.SaveAsync(Path.Combine("..", "Data-escape", "file.bin"), content, CancellationToken.None));
                }
                Assert.False(File.Exists(Path.Combine(parent, "Data-escape", "file.bin")));
            }
            finally { Directory.Delete(parent, true); }
        }

        [Fact]
        public async Task Rooted_path_is_rejected_even_when_it_points_below_root()
        {
            var parent = NewDirectory();
            var root = Path.Combine(parent, "Data");
            try
            {
                var service = new FileStorageService(new ApplicationOptions { DataDirectory = root });
                using (var content = new MemoryStream(new byte[] { 1 }))
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        service.SaveAsync(Path.Combine(root, "file.bin"), content, CancellationToken.None));
                }
            }
            finally { Directory.Delete(parent, true); }
        }

        static string NewDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
