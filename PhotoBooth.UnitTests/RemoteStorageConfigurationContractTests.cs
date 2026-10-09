using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using PhotoBooth.Core.Services;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class RemoteStorageConfigurationContractTests
    {
        [Theory]
        [InlineData("drive")]
        [InlineData("google_drive")]
        public void Drive_wire_labels_normalize_to_public_provider(string value)
        {
            Assert.Equal(RemoteStorageProviders.GoogleDrive, RemoteStorageProviders.Normalize(value));
            Assert.Equal("drive", RemoteStorageProviders.GoogleDrive);
        }

        [Fact]
        public void Pc_storage_contract_does_not_expose_device_oauth()
        {
            var methods = typeof(IRemoteStorageConfigurationService)
                .GetMethods()
                .Select(method => method.Name)
                .OrderBy(name => name)
                .ToArray();

            Assert.Equal(new[] { "GetAsync", "SetProviderAsync" }, methods);
        }

        [Fact]
        public void Device_status_reads_the_current_drive_response_key()
        {
            var responseType = typeof(RemoteStorageConfigurationService)
                .GetNestedType("SettingsResponse", BindingFlags.NonPublic);
            var driveProperty = responseType?.GetProperty("Drive", BindingFlags.Instance | BindingFlags.Public);
            var dataMember = driveProperty?.GetCustomAttribute<DataMemberAttribute>();

            Assert.NotNull(responseType);
            Assert.NotNull(driveProperty);
            Assert.Equal("drive", dataMember?.Name);
            Assert.Null(responseType.GetProperty("GoogleDrive", BindingFlags.Instance | BindingFlags.Public));
        }
    }
}
