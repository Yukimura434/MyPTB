using System;
using System.Security.Cryptography;
using System.Text;
using PhotoBooth.Core.Models;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class OfflineEntitlementLeaseVerifierTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(168)]
        public void Signed_v2_lease_is_bound_to_device_revision_and_expiry(int hours)
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var issuedAt = new DateTimeOffset(now).ToUnixTimeSeconds();
            var expiresAt = issuedAt + hours * 3600;
            var payload = "{\"leaseVersion\":2,\"issuer\":\"miuphotobooth-worker\",\"audience\":\"photobooth-device\",\"organizationId\":\"org-1\",\"deviceId\":\"device-1\",\"deviceKeyVersion\":1,\"deviceSecurityRevision\":3,\"issuedAtUnix\":" + issuedAt + ",\"notBeforeUnix\":" + issuedAt + ",\"expiresAtUnix\":" + expiresAt + ",\"entitlement\":{\"appAccess\":true}}";
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                var parameters = rsa.ExportParameters(false);
                var lease = new SignedCommercialEntitlementLease
                {
                    Format = "photobooth-entitlement-lease-v2",
                    Algorithm = "RS256",
                    SigningKeyVersion = 1,
                    Payload = Base64Url(Encoding.UTF8.GetBytes(payload)),
                    Signature = Base64Url(rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
                    IssuedAtUnix = issuedAt,
                    ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime,
                    Audience = "photobooth-device",
                    DeviceId = "device-1"
                };
                var identity = new DeviceIdentity { OrganizationId = "org-1", DeviceId = "device-1" };

                Assert.True(OfflineEntitlementLeaseVerifier.TryVerify(lease, identity, now, 0, Base64Url(parameters.Modulus), Base64Url(parameters.Exponent)));
                Assert.True(lease.IsVerified);
                Assert.Equal(1, lease.DeviceKeyVersion);
                Assert.Equal(3, lease.DeviceSecurityRevision);

                var wrongDevice = new DeviceIdentity { OrganizationId = "org-1", DeviceId = "device-2" };
                Assert.False(OfflineEntitlementLeaseVerifier.TryVerify(lease, wrongDevice, now, 0, Base64Url(parameters.Modulus), Base64Url(parameters.Exponent)));
                Assert.False(OfflineEntitlementLeaseVerifier.TryVerify(lease, identity, now.AddHours(hours + 1), 0, Base64Url(parameters.Modulus), Base64Url(parameters.Exponent)));

                lease.Signature = lease.Signature.Substring(0, lease.Signature.Length - 1) + (lease.Signature.EndsWith("A", StringComparison.Ordinal) ? "B" : "A");
                Assert.False(OfflineEntitlementLeaseVerifier.TryVerify(lease, identity, now, 0, Base64Url(parameters.Modulus), Base64Url(parameters.Exponent)));
            }
        }

        [Fact]
        public void Lease_issue_time_cannot_roll_back_beyond_tolerance()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var issuedAt = new DateTimeOffset(now).ToUnixTimeSeconds();
            var payload = "{\"leaseVersion\":2,\"issuer\":\"miuphotobooth-worker\",\"audience\":\"photobooth-device\",\"organizationId\":\"org-1\",\"deviceId\":\"device-1\",\"deviceKeyVersion\":1,\"deviceSecurityRevision\":1,\"issuedAtUnix\":" + issuedAt + ",\"notBeforeUnix\":" + issuedAt + ",\"expiresAtUnix\":" + (issuedAt + 3600) + ",\"entitlement\":{\"appAccess\":true}}";
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                var parameters = rsa.ExportParameters(false);
                var lease = new SignedCommercialEntitlementLease
                {
                    Format = "photobooth-entitlement-lease-v2", Algorithm = "RS256", SigningKeyVersion = 1,
                    Payload = Base64Url(Encoding.UTF8.GetBytes(payload)),
                    Signature = Base64Url(rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                };

                Assert.False(OfflineEntitlementLeaseVerifier.TryVerify(lease,
                    new DeviceIdentity { OrganizationId = "org-1", DeviceId = "device-1" }, now,
                    issuedAt + 301, Base64Url(parameters.Modulus), Base64Url(parameters.Exponent)));
            }
        }

        static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
