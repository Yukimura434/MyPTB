using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class DeviceRequestProofTests
    {
        const string InstallationId = "installation-device-proof-tests";

        [Fact]
        public void Device_key_is_stable_and_activation_proof_is_bound_to_the_challenge()
        {
            var first = DeviceRequestProof.GetOrCreatePublicKey(InstallationId);
            var second = DeviceRequestProof.GetOrCreatePublicKey(InstallationId);

            Assert.Equal(first.Thumbprint, second.Thumbprint);
            Assert.Equal(first.PublicKey.X, second.PublicKey.X);
            Assert.Equal(first.PublicKey.Y, second.PublicKey.Y);

            const string canonical = "photobooth-device-activation-v1\naat_test\nchallenge-a\norg-1\n";
            var message = canonical + first.Thumbprint + "\nloc-1\nBooth 1\nwindows\n1.2.3";
            var signature = DeviceRequestProof.SignActivation(InstallationId, "aat_test", "challenge-a", "org-1", first.Thumbprint, "loc-1", "Booth 1", "windows", "1.2.3");

            Assert.True(Verify(first.PublicKey, Encoding.UTF8.GetBytes(message), signature));
            Assert.False(Verify(first.PublicKey, Encoding.UTF8.GetBytes(message.Replace("challenge-a", "challenge-b")), signature));
        }

        [Fact]
        public async Task Device_request_signature_binds_method_path_body_credential_and_uses_a_fresh_nonce()
        {
            var repository = new DeviceRepository();
            DeviceRequestProof.GetOrCreatePublicKey(InstallationId);
            var body = Encoding.UTF8.GetBytes("{\"value\":1}");
            var first = new HttpRequestMessage(HttpMethod.Post, "https://api.test/api/v3/device/configuration?revision=2")
            {
                Content = new ByteArrayContent(body)
            };
            var second = new HttpRequestMessage(HttpMethod.Post, first.RequestUri)
            {
                Content = new ByteArrayContent(body)
            };

            await DeviceRequestProof.SignAsync(first, repository, "dv3.dcr_test.secret", null, CancellationToken.None);
            await DeviceRequestProof.SignAsync(second, repository, "dv3.dcr_test.secret", null, CancellationToken.None);

            var nonce = Header(first, "X-Device-Nonce");
            var timestamp = Header(first, "X-Device-Timestamp");
            var contentHash = Header(first, "X-Device-Content-Sha256");
            Assert.Equal(Sha256Hex(body), contentHash);
            Assert.NotEqual(nonce, Header(second, "X-Device-Nonce"));

            var canonical = string.Join("\n", new[]
            {
                "photobooth-device-request-v1", "dcr_test", "device-1", "POST",
                "/api/v3/device/configuration?revision=2", timestamp, nonce, contentHash, "1"
            });
            var publicKey = DeviceRequestProof.GetOrCreatePublicKey(InstallationId).PublicKey;
            Assert.True(Verify(publicKey, Encoding.UTF8.GetBytes(canonical), Header(first, "X-Device-Signature")));

            first.Dispose();
            second.Dispose();
        }

        static string Header(HttpRequestMessage request, string name) => request.Headers.GetValues(name).Single();

        static bool Verify(DevicePublicKey publicKey, byte[] data, string signature)
        {
            var x = Base64UrlDecode(publicKey.X);
            var y = Base64UrlDecode(publicKey.Y);
            var blob = new byte[8 + x.Length + y.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(0x31534345), 0, blob, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(x.Length), 0, blob, 4, 4);
            Buffer.BlockCopy(x, 0, blob, 8, x.Length);
            Buffer.BlockCopy(y, 0, blob, 8 + x.Length, y.Length);
            using (var key = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
            using (var verifier = new ECDsaCng(key))
                return verifier.VerifyData(data, Base64UrlDecode(signature), HashAlgorithmName.SHA256);
        }

        static string Sha256Hex(byte[] value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", string.Empty).ToLowerInvariant();
        }

        static byte[] Base64UrlDecode(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            switch (normalized.Length % 4)
            {
                case 2: normalized += "=="; break;
                case 3: normalized += "="; break;
            }
            return Convert.FromBase64String(normalized);
        }

        sealed class DeviceRepository : IDeviceIdentityRepository
        {
            readonly DeviceIdentity identity = new DeviceIdentity
            {
                ProfileId = "profile-1",
                InstallationId = InstallationId,
                DeviceId = "device-1",
                OrganizationId = "org-1",
                EnrollmentState = DeviceEnrollmentState.Enrolled
            };

            public Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken) => Task.FromResult(identity);
            public Task<byte[]> GetProtectedCredentialAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]>(null);
            public Task SaveEnrolledAsync(DeviceIdentity value, byte[] credential, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ResetRevokedInstallationAsync(string installationPublicId, string organizationId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string> GetInstallationPublicIdAsync(CancellationToken cancellationToken) => Task.FromResult(InstallationId);
            public Task<IReadOnlyList<DeviceIdentity>> ListProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeviceIdentity>>(new[] { identity });
            public Task SelectProfileAsync(string profileId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task SaveProfileAsync(DeviceIdentity value, byte[] credential, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
