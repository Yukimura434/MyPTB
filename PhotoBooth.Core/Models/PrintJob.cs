using System;

namespace PhotoBooth.Core.Models
{
    public sealed class PrintJob
    {
        public Guid Id { get; set; }
        public string FilePath { get; set; }
        public string PrinterName { get; set; }
        public int Copies { get; set; }
        public string PaperSize { get; set; }
        public string PaperType { get; set; }
        public string Quality { get; set; }
        public bool Landscape { get; set; }
        public bool PrintInColor { get; set; } = true;
        public bool UseDefaultBorder { get; set; }
    }
}
