using System;

namespace PhotoBooth.Core.Models
{
    // Local presentation only. A null profile in Settings means pre-profile data.
    public sealed class WaitingScreenProfile
    {
        public Guid? BackgroundAssetId { get; set; }
        public bool ShowLiveView { get; set; }
        public double LiveViewX { get; set; } = 50;
        public double LiveViewY { get; set; } = 50;
        public double LiveViewAreaPercent { get; set; } = 5;
        public double BackgroundZoom { get; set; } = 100;
        public double BackgroundPanX { get; set; }
        public double BackgroundPanY { get; set; }
        public WaitingScreenProfile Copy() => (WaitingScreenProfile)MemberwiseClone();
    }
}
