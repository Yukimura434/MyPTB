using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoBooth.Core.Models
{
    public enum CommercialAccessBlockReason
    {
        None,
        DeviceNotEnrolled,
        DeviceLocked,
        AccountVerificationRequired,
        AccountSuspended,
        AccountClosed,
        SubscriptionRequired,
        DeviceLimitReached,
        EntitlementExpired,
        ServiceUnavailable,
        InvalidResponse
    }

    public sealed class CommercialEntitlement
    {
        public string AccountState { get; set; }
        public string CommercialState { get; set; }
        public IReadOnlyList<string> EffectivePlans { get; set; } = new string[0];
        public bool AppAccess { get; set; }
        public string WebAccess { get; set; }
        public bool UploadAccess { get; set; }
        public int DeviceLimit { get; set; }
        public CommercialDeviceAccess DeviceAccess { get; set; }
        public bool LifetimeAccess { get; set; }
        public CommercialStorageAllowance PlatformStorage { get; set; }
        public CommercialExternalStorageAllowance ExternalStorage { get; set; }
        public CommercialEventRetention EventRetention { get; set; }
        public CommercialPeriod CurrentPeriod { get; set; }
        public CommercialPeriod NextPeriod { get; set; }

        // Only a lease verified by the desktop's embedded Worker public key may
        // extend access while the Worker is temporarily unreachable.
        public SignedCommercialEntitlementLease Lease { get; set; }

        public CommercialAccessDecision EvaluateAppAccess(DateTime utcNow)
        {
            if (string.Equals(AccountState, "pending_verification", StringComparison.OrdinalIgnoreCase))
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.AccountVerificationRequired,
                    "Email chủ tài khoản chưa được xác minh. Vui lòng xác minh email trong Web Customer.");
            if (string.Equals(AccountState, "suspended", StringComparison.OrdinalIgnoreCase))
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.AccountSuspended,
                    "Tài khoản đang bị tạm ngưng. Vui lòng liên hệ nhà cung cấp để được hỗ trợ.");
            if (string.Equals(AccountState, "closed", StringComparison.OrdinalIgnoreCase))
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.AccountClosed,
                    "Tài khoản không còn hoạt động. Vui lòng liên hệ nhà cung cấp để được hỗ trợ.");
            if (!string.Equals(AccountState, "active", StringComparison.OrdinalIgnoreCase))
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.InvalidResponse,
                    "Không thể xác nhận trạng thái tài khoản. Vui lòng thử lại.", true);
            if (!AppAccess)
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.SubscriptionRequired,
                    "Gói sử dụng chưa được kích hoạt hoặc đã hết hạn. Vui lòng liên hệ chủ tài khoản hoặc nhà cung cấp để gia hạn.");
            if (DeviceLimit <= 0)
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.DeviceLimitReached,
                    "Gói hiện tại chưa cho phép sử dụng thiết bị. Vui lòng liên hệ chủ tài khoản hoặc nhà cung cấp.");
            if (DeviceAccess != null && !DeviceAccess.Allowed)
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.DeviceLimitReached,
                    "Thiết bị này nằm ngoài số lượng thiết bị được phép của gói hiện tại. Vui lòng kiểm tra trong MiuManager.");
            if (!HasPerpetualAppAccess() && CurrentPeriod?.EndsAtUtc != null && utcNow >= CurrentPeriod.EndsAtUtc.Value
                && !PeriodIsEffective(NextPeriod, utcNow))
                return CommercialAccessDecision.Blocked(this, CommercialAccessBlockReason.EntitlementExpired,
                    "Gói sử dụng đã hết hạn. Vui lòng liên hệ chủ tài khoản hoặc nhà cung cấp để gia hạn.");
            return CommercialAccessDecision.Allowed(this);
        }

        static bool PeriodIsEffective(CommercialPeriod period, DateTime utcNow)
        {
            if (period == null) return false;
            if (period.StartsAtUtc.HasValue && utcNow < period.StartsAtUtc.Value) return false;
            return !period.EndsAtUtc.HasValue || utcNow < period.EndsAtUtc.Value;
        }

        bool HasPerpetualAppAccess()
        {
            if (LifetimeAccess) return true;
            if ((CommercialState ?? string.Empty).StartsWith("perpetual", StringComparison.OrdinalIgnoreCase)) return true;
            return (EffectivePlans ?? new string[0]).Any(value =>
                !string.IsNullOrWhiteSpace(value) && value.IndexOf("PERPETUAL", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    public sealed class CommercialDeviceAccess
    {
        public bool Allowed { get; set; }
        public int? Rank { get; set; }
        public int Limit { get; set; }
        public string DenialCode { get; set; }
    }

    public sealed class CommercialStorageAllowance
    {
        public long LimitBytes { get; set; }
        public long UsedBytes { get; set; }
        public long ReservedBytes { get; set; }
        public DateTime? HoldUntilUtc { get; set; }
        public DateTime? PurgeAtUtc { get; set; }
    }

    public sealed class CommercialExternalStorageAllowance
    {
        public bool Allowed { get; set; }
        public bool Required { get; set; }
    }

    public sealed class CommercialEventRetention
    {
        public int MinimumDays { get; set; }
        public int MaximumDays { get; set; }
    }

    public sealed class CommercialPeriod
    {
        public string PlanCode { get; set; }
        public DateTime? StartsAtUtc { get; set; }
        public DateTime? EndsAtUtc { get; set; }
    }

    public sealed class SignedCommercialEntitlementLease
    {
        public string Format { get; set; }
        public string Algorithm { get; set; }
        public string KeyId { get; set; }
        public int SigningKeyVersion { get; set; }
        public string Payload { get; set; }
        public string Signature { get; set; }
        public long Revision { get; set; }
        public long IssuedAtUnix { get; set; }
        public DateTime? NotBeforeUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public string Audience { get; set; }
        public string DeviceId { get; set; }
        public int? DeviceKeyVersion { get; set; }
        public long? DeviceSecurityRevision { get; set; }
        public bool IsVerified { get; set; }
    }

    public sealed class CommercialAccessDecision
    {
        public bool IsAllowed { get; private set; }
        public CommercialAccessBlockReason BlockReason { get; private set; }
        public string UserMessage { get; private set; }
        public bool IsRetryable { get; private set; }
        public CommercialEntitlement Entitlement { get; private set; }

        public static CommercialAccessDecision Allowed(CommercialEntitlement entitlement) => new CommercialAccessDecision
        {
            IsAllowed = true,
            BlockReason = CommercialAccessBlockReason.None,
            Entitlement = entitlement
        };

        public static CommercialAccessDecision Blocked(CommercialEntitlement entitlement, CommercialAccessBlockReason reason, string message, bool retryable = false) => new CommercialAccessDecision
        {
            IsAllowed = false,
            BlockReason = reason,
            UserMessage = message,
            IsRetryable = retryable,
            Entitlement = entitlement
        };
    }
}
