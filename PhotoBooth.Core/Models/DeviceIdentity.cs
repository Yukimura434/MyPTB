using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public enum DeviceEnrollmentState
    {
        Unenrolled,
        Enrolled,
        Revoked,
        Suspended
    }

    public sealed class DeviceIdentity
    {
        public string ProfileId { get; set; }
        public string InstallationId { get; set; }
        public string OrganizationId { get; set; }
        public string OrganizationName { get; set; }
        public string DeviceId { get; set; }
        public string LocationId { get; set; }
        public string LocationName { get; set; }
        public string DeviceName { get; set; }
        public DeviceEnrollmentState EnrollmentState { get; set; } = DeviceEnrollmentState.Unenrolled;
        public int? CredentialVersion { get; set; }
        public DateTime? LastAuthenticatedAtUtc { get; set; }
        public DateTime? LastHeartbeatAtUtc { get; set; }
    }

    public sealed class DeviceActivationAttempt
    {
        public string InstallationId { get; set; }
        public string AttemptId { get; set; }
        public string Challenge { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string OwnerDisplayName { get; set; }
        public string OrganizationId { get; set; }
        public string OrganizationName { get; set; }
        public string PublicKeyThumbprint { get; set; }
        public bool DeviceProofRequired { get; set; }
        public string LocationId { get; set; }
        public string LocationName { get; set; }
        public IReadOnlyList<DeviceActivationLocation> Locations { get; set; } = new DeviceActivationLocation[0];
    }

    public sealed class DeviceActivationLocation
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int DeviceCount { get; set; }
    }

    public sealed class DeviceReenrollmentRequiredException : InvalidOperationException
    {
        public DeviceReenrollmentRequiredException() : base("Thiết bị đã đăng xuất trên web. Hãy xác minh lại chủ tài khoản để kết nối máy này.") { }
    }
}
