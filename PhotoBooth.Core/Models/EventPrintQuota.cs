using System;

namespace PhotoBooth.Core.Models
{
    public sealed class EventPrintQuota
    {
        public string Mode { get; set; }
        public int? EventRemaining { get; set; }
        public int? CaptureRemaining { get; set; }

        public int AllowedCopies(int supportedMaximum)
        {
            if (Mode == "unlimited") return Math.Max(0, supportedMaximum);
            if (Mode != "limited" || !EventRemaining.HasValue || !CaptureRemaining.HasValue || EventRemaining < 0 || CaptureRemaining < 0)
                throw new InvalidOperationException("Chưa xác minh được số bản in còn lại. Vui lòng thử lại.");
            return Math.Max(0, Math.Min(supportedMaximum, Math.Min(EventRemaining.Value, CaptureRemaining.Value)));
        }
    }
}
