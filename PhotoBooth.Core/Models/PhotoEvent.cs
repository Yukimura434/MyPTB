using System;

namespace PhotoBooth.Core.Models
{
    public static class PhotoEventLifecycleStates
    {
        public const string Draft = "Draft";
        public const string Scheduled = "Scheduled";
        public const string Open = "Open";
        public const string Closed = "Closed";
        public const string Delivered = "Delivered";
        public const string Archived = "Archived";

        public static bool IsKnown(string value) => value == Draft || value == Scheduled || value == Open || value == Closed || value == Delivered || value == Archived;

        public static string DisplayName(string value)
        {
            switch (value)
            {
                case Draft: return "Bản nháp";
                case Scheduled: return "Đã lên lịch";
                case Open: return "Đang mở nhận lượt chụp";
                case Closed: return "Đã đóng nhận lượt chụp";
                case Delivered: return "Đã bàn giao";
                case Archived: return "Đã lưu trữ";
                default: return "Chưa xác định";
            }
        }
    }

    /// <summary>Operator-managed business scope that groups many customer booth sessions.</summary>
    public sealed class PhotoEvent
    {
        public Guid Id { get; set; }
        public Guid? PresetId { get; set; }
        public string Name { get; set; }
        /// <summary>Operator-selected folder that receives exported media for this event.</summary>
        public string OutputDirectory { get; set; }
        /// <summary>Compatibility alias for the per-device runtime selection. It is not Event lifecycle.</summary>
        public bool IsDefault { get; set; }
        public bool IsSelectedForThisDevice { get => IsDefault; set => IsDefault = value; }
        /// <summary>The built-in event that keeps a newly installed booth immediately operable.</summary>
        public bool IsSystemDefault { get; set; }
        public string LifecycleState { get; set; } = PhotoEventLifecycleStates.Draft;
        public DateTime? ScheduledStartAtUtc { get; set; }
        public DateTime? ScheduledEndAtUtc { get; set; }
        public DateTime? OpenedAtUtc { get; set; }
        public DateTime? ClosedAtUtc { get; set; }
        public DateTime? DeliveredAtUtc { get; set; }
        public DateTime? ArchivedAtUtc { get; set; }
        public string RemoteEventId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public string LifecycleDisplayName => PhotoEventLifecycleStates.DisplayName(LifecycleState);
        public bool AcceptsNewBoothSessions => LifecycleState == PhotoEventLifecycleStates.Open;
        public bool IsArchived => LifecycleState == PhotoEventLifecycleStates.Archived;
        public override string ToString() => Name ?? string.Empty;
    }
}
