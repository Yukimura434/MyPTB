namespace PhotoBooth.Core.Models
{
    public sealed class BeautySettings
    {
        public bool Enabled { get; set; }
        public int SmoothSkin { get; set; }
        public int BrightenSkin { get; set; }
        public int SkinTone { get; set; }
        public int Sharpen { get; set; }
        public int EyeSize { get; set; }
        public int SlimFace { get; set; }

        public bool HasEffect => Enabled && (SmoothSkin > 0 || BrightenSkin > 0 || SkinTone > 0 || Sharpen > 0 || EyeSize > 0 || SlimFace > 0);

        public BeautySettings Clone() => new BeautySettings
        {
            Enabled = Enabled,
            SmoothSkin = SmoothSkin,
            BrightenSkin = BrightenSkin,
            SkinTone = SkinTone,
            Sharpen = Sharpen,
            EyeSize = EyeSize,
            SlimFace = SlimFace
        };
    }
}
