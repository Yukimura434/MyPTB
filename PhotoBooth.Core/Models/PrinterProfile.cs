using System;

namespace PhotoBooth.Core.Models
{
    public sealed class PrinterProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string PrinterName { get; set; }
        public string PrinterId { get; set; }
        public string PaperSize { get; set; }
        public bool Landscape { get; set; }
        public bool PrintInColor { get; set; } = true;
        public int DefaultCopies { get; set; }
        public string PaperType { get; set; }
        public string Quality { get; set; }
        public bool UseDefaultBorder { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class DiscoveredPrinter
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string PortName { get; set; }
        public string DriverName { get; set; }
        public string ConnectionType { get; set; }
        public bool IsOnline { get; set; }
        public bool SupportsColor { get; set; }
        public bool SupportsDuplex { get; set; }
        public string[] PaperSizes { get; set; } = new string[0];
        public string[] PaperSources { get; set; } = new string[0];
        public string[] Resolutions { get; set; } = new string[0];
    }
}
