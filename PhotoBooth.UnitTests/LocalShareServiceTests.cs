using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class LocalShareServiceTests
    {
        [Fact]
        public void CreateArchive_PutsCaptureFilesUnderSessionCaptureFolder()
        {
            var testDirectory = Path.Combine(Path.GetTempPath(), "PhotoBooth-LocalShare-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);

            try
            {
                var first = Path.Combine(testDirectory, "photo-1.jpg");
                var second = Path.Combine(testDirectory, "photo-2.jpg");
                var zip = Path.Combine(testDirectory, "session.capture.zip");
                File.WriteAllText(first, "first-photo");
                File.WriteAllText(second, "second-photo");

                LocalShareService.CreateArchive(
                    zip,
                    "session.capture",
                    new[] { first, second });

                using (var archive = ZipFile.OpenRead(zip))
                {
                    Assert.Equal(
                        new[]
                        {
                            "session.capture/photo-1.jpg",
                            "session.capture/photo-2.jpg"
                        },
                        archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());

                    using (var reader = new StreamReader(archive.GetEntry("session.capture/photo-1.jpg").Open()))
                    {
                        Assert.Equal("first-photo", reader.ReadToEnd());
                    }
                }
            }
            finally
            {
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, true);
                }
            }
        }
    }
}
