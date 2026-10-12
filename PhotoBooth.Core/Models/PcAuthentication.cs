using System;

namespace PhotoBooth.Core.Models
{
    public enum PcAppRole
    {
        LocalAdmin,
        BoothOperator
    }

    public enum PcAuthenticationMode
    {
        OnlineDeviceBound,
        OfflineCache,
        DevelopmentAccount
    }

    public sealed class PcSession
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public string OrganizationId { get; set; }
        public PcAppRole Role { get; set; }
        public PcAuthenticationMode AuthenticationMode { get; set; }
        public DateTime AuthenticatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        // Ephemeral Worker employee-session token. It is never persisted as plain text.
        public string RemoteSessionToken { get; set; }
        public BoothAccessAuthorization EventAuthorization { get; set; }

        public string RoleDisplayName => EventAuthorization?.IsEvent == true ? "Tài khoản sự kiện" : "Quản lý booth";
        // BoothOperator remains the server's single employee role identifier.
        // It grants manager controls on PC; it is not an operator-only UI tier.
        bool HasManagerAccess => !IsExpired &&
            ((AuthenticationMode == PcAuthenticationMode.OnlineDeviceBound && Role == PcAppRole.BoothOperator) ||
             (AuthenticationMode == PcAuthenticationMode.DevelopmentAccount && (Role == PcAppRole.LocalAdmin || Role == PcAppRole.BoothOperator)));
        // Event selects gallery attribution; only PTB/SelfBooth are guest sessions.
        public bool CanManageConfiguration => HasManagerAccess && (EventAuthorization == null || EventAuthorization.IsEvent);
        public bool CanConfigureDevice => HasManagerAccess && (EventAuthorization == null || EventAuthorization.IsEvent);
        public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
    }

    public sealed class PcAccountRecord
    {
        public string AccountId { get; set; }
        public string Email { get; set; }
        public string DisplayName { get; set; }
        public string OrganizationId { get; set; }
        public PcAppRole Role { get; set; }
        public string PasswordHash { get; set; }
        public bool IsActive { get; set; }
        public bool IsDevelopmentAccount { get; set; }
        public DateTime? LastOnlineAuthenticationAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class PcRememberedLogin
    {
        public string AccountId { get; set; }
        public string OrganizationId { get; set; }
        public byte[] ProtectedToken { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class PcAuthenticationResult
    {
        public bool Succeeded { get; set; }
        public PcSession Session { get; set; }
        public string Error { get; set; }
    }
}
