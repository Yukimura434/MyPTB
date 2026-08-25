using System;

namespace PhotoBooth.Core.Models
{
    public sealed class PrintJobRecord
    {
        public Guid Id { get; set; }
        public Guid? SessionId { get; set; }
        public string CaptureId { get; set; }
        public Guid? PrinterProfileId { get; set; }
        public string PrinterName { get; set; }
        public int Copies { get; set; } = 1;
        public string PaperSize { get; set; }
        public string PaperType { get; set; }
        public string Quality { get; set; }
        public bool Landscape { get; set; }
        public bool PrintInColor { get; set; } = true;
        public bool UseDefaultBorder { get; set; }
        public string Status { get; set; } = "Success";
        public DateTime PrintedAtUtc { get; set; }
    }
}
