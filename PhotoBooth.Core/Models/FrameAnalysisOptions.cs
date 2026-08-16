namespace PhotoBooth.Core.Models
{
    public sealed class FrameAnalysisOptions
    {
        public byte AlphaThreshold { get; set; } = 8;
        public int MinimumArea { get; set; } = 10000;
        public int MinimumWidth { get; set; } = 40;
        public int MinimumHeight { get; set; } = 40;
        public bool IgnoreBorderConnectedRegions { get; set; } = true;
        public int MaximumSlots { get; set; } = 8;
    }
}
