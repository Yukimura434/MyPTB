using System;

namespace PhotoBooth.Core.Models
{
    public sealed class RevenueReport
    {
        public Guid BoothSessionId { get; set; }
        public Guid EventId { get; set; }
        public string RemoteEventId { get; set; }
        public string DisplayCode { get; set; }
        public long BasePriceMinor { get; set; }
        public int IncludedPrints { get; set; } = 1;
        public int PrintedCopies { get; set; }
        public int AdditionalPrintCopies { get; set; }
        public long AdditionalPrintUnitPriceMinor { get; set; }
        public long GrossRevenueMinor { get; set; }
        public string Currency { get; set; } = "VND";
        public DateTime OpenedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public int AttemptCount { get; set; }
    }
}
