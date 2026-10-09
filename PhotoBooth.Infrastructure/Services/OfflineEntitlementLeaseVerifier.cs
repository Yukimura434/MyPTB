using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using PhotoBooth.Core.Models;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    internal static class OfflineEntitlementLeaseVerifier
    {
        const long MaximumLifetimeSeconds = 7 * 24 * 60 * 60;
        const long ClockToleranceSeconds = 5 * 60;

        public static bool TryVerify(SignedCommercialEntitlementLease lease, DeviceIdentity identity, DateTime utcNow, long minimumIssuedAtUnix = 0)
            => TryVerify(lease, identity, utcNow, minimumIssuedAtUnix, LicensePublicKey.Modulus, LicensePublicKey.Exponent);

        internal static bool TryVerify(SignedCommercialEntitlementLease lease, DeviceIdentity identity, DateTime utcNow, long minimumIssuedAtUnix, string modulus, string exponent)
        {
            if (lease == null || identity == null || string.IsNullOrWhiteSpace(identity.DeviceId) || string.IsNullOrWhiteSpace(identity.OrganizationId)) return false;
            if (!string.Equals(lease.Format, "photobooth-entitlement-lease-v2", StringComparison.Ordinal) ||
                !string.Equals(lease.Algorithm, "RS256", StringComparison.Ordinal) || lease.SigningKeyVersion != 1) return false;
            byte[] payloadBytes, signatureBytes;
            try { payloadBytes = Decode(lease.Payload); signatureBytes = Decode(lease.Signature); }
            catch (FormatException) { return false; }
            if (payloadBytes.Length == 0 || payloadBytes.Length > 64 * 1024 || signatureBytes.Length == 0) return false;
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = Decode(modulus), Exponent = Decode(exponent) });
                    if (!rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return false;
                }
            }
            catch (CryptographicException) { return false; }
            catch (PlatformNotSupportedException) { return false; }

            LeasePayload payload;
            try
            {
                using (var stream = new MemoryStream(payloadBytes))
                    payload = (LeasePayload)new DataContractJsonSerializer(typeof(LeasePayload)).ReadObject(stream);
            }
            catch (SerializationException) { return false; }
            if (payload == null || payload.LeaseVersion != 2 ||
                !string.Equals(payload.Issuer, "miuphotobooth-worker", StringComparison.Ordinal) ||
                !string.Equals(payload.Audience, "photobooth-device", StringComparison.Ordinal) ||
                !string.Equals(payload.OrganizationId, identity.OrganizationId, StringComparison.Ordinal) ||
                !string.Equals(payload.DeviceId, identity.DeviceId, StringComparison.Ordinal) ||
                payload.DeviceKeyVersion.GetValueOrDefault() < 1 || payload.DeviceSecurityRevision.GetValueOrDefault() < 1 ||
                payload.IssuedAtUnix < 1 || payload.NotBeforeUnix != payload.IssuedAtUnix || payload.ExpiresAtUnix <= payload.IssuedAtUnix ||
                payload.ExpiresAtUnix - payload.IssuedAtUnix > MaximumLifetimeSeconds || payload.Entitlement == null || !payload.Entitlement.AppAccess)
                return false;
            var now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
            if (payload.NotBeforeUnix > now + ClockToleranceSeconds || payload.ExpiresAtUnix <= now ||
                (minimumIssuedAtUnix > 0 && payload.IssuedAtUnix + ClockToleranceSeconds < minimumIssuedAtUnix)) return false;
            if (lease.IssuedAtUnix > 0 && lease.IssuedAtUnix != payload.IssuedAtUnix) return false;
            if (lease.ExpiresAtUtc.HasValue && new DateTimeOffset(lease.ExpiresAtUtc.Value.ToUniversalTime()).ToUnixTimeSeconds() != payload.ExpiresAtUnix) return false;
            if (!string.IsNullOrWhiteSpace(lease.Audience) && !string.Equals(lease.Audience, payload.Audience, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrWhiteSpace(lease.DeviceId) && !string.Equals(lease.DeviceId, payload.DeviceId, StringComparison.Ordinal)) return false;

            lease.IssuedAtUnix = payload.IssuedAtUnix;
            lease.NotBeforeUtc = DateTimeOffset.FromUnixTimeSeconds(payload.NotBeforeUnix).UtcDateTime;
            lease.ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnix).UtcDateTime;
            lease.Audience = payload.Audience;
            lease.DeviceId = payload.DeviceId;
            lease.DeviceKeyVersion = payload.DeviceKeyVersion;
            lease.DeviceSecurityRevision = payload.DeviceSecurityRevision;
            lease.IsVerified = true;
            return true;
        }

        static byte[] Decode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException();
            var normalized = value.Replace('-', '+').Replace('_', '/');
            if (normalized.Length % 4 == 2) normalized += "==";
            else if (normalized.Length % 4 == 3) normalized += "=";
            else if (normalized.Length % 4 == 1) throw new FormatException();
            return Convert.FromBase64String(normalized);
        }

        [DataContract]
        sealed class LeasePayload
        {
            [DataMember(Name = "leaseVersion")] public int LeaseVersion { get; set; }
            [DataMember(Name = "issuer")] public string Issuer { get; set; }
            [DataMember(Name = "audience")] public string Audience { get; set; }
            [DataMember(Name = "organizationId")] public string OrganizationId { get; set; }
            [DataMember(Name = "deviceId")] public string DeviceId { get; set; }
            [DataMember(Name = "deviceKeyVersion")] public int? DeviceKeyVersion { get; set; }
            [DataMember(Name = "deviceSecurityRevision")] public long? DeviceSecurityRevision { get; set; }
            [DataMember(Name = "issuedAtUnix")] public long IssuedAtUnix { get; set; }
            [DataMember(Name = "notBeforeUnix")] public long NotBeforeUnix { get; set; }
            [DataMember(Name = "expiresAtUnix")] public long ExpiresAtUnix { get; set; }
            [DataMember(Name = "entitlement")] public LeaseEntitlement Entitlement { get; set; }
        }

        [DataContract]
        sealed class LeaseEntitlement
        {
            [DataMember(Name = "appAccess")] public bool AppAccess { get; set; }
        }
    }
}
