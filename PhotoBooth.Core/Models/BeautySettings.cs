namespace PhotoBooth.Core.Models
{
    public sealed class BeautySettings
    {
        public const int MaxGeometry = 20;
        public const int MaxSmoothSkin = 60;
        public const int MaxBrightenSkin = 20;
        public const int MaxSkinTone = 20;
        public const int MaxBlemishRemoval = 70;
        public bool Enabled { get; set; }
        public int SmoothSkin { get; set; }
        public int BrightenSkin { get; set; }
        public int SkinTone { get; set; }
        public int Sharpen { get; set; }
        // Geometry uses the original strength scale, capped at 20.
        public int EyeSize { get; set; }
        public int SlimFace { get; set; }
        public int BlemishRemoval { get; set; }
        // Retained for database compatibility; no longer used by the beauty engine.
        public int DarkCircleReduction { get; set; }

        public bool HasEffect => Enabled && (HasLiveEffect || SkinTone > 0 || Sharpen > 0 || EyeSize > 0 || SlimFace > 0 || BlemishRemoval > 0);
        public bool HasLiveEffect => Enabled && (SmoothSkin > 0 || BrightenSkin > 0);

        public void SetLevel(int level)
        {
            level = System.Math.Max(0, System.Math.Min(10, level));
            SmoothSkin = level * 6;
            BrightenSkin = SkinTone = level * 2;
            BlemishRemoval = level * 7;
            Sharpen = level * 10;
            DarkCircleReduction = 0;
            EyeSize = SlimFace = level * 2;
        }

        public int? Level
        {
            get
            {
                int level = SmoothSkin / 6;
                return level >= 0 && level <= 10 && SmoothSkin == level * 6 && BrightenSkin == level * 2 &&
                    SkinTone == level * 2 && Sharpen == level * 10 && BlemishRemoval == level * 7 &&
                    EyeSize == level * 2 && SlimFace == EyeSize ? (int?)level : null;
            }
        }

        public void ClampStrengths()
        {
            SmoothSkin = Clamp(SmoothSkin, MaxSmoothSkin);
            BrightenSkin = Clamp(BrightenSkin, MaxBrightenSkin);
            SkinTone = Clamp(SkinTone, MaxSkinTone);
            BlemishRemoval = Clamp(BlemishRemoval, MaxBlemishRemoval);
            Sharpen = Clamp(Sharpen, 100);
            EyeSize = Clamp(EyeSize, MaxGeometry); SlimFace = Clamp(SlimFace, MaxGeometry);
            DarkCircleReduction = 0;
        }

        static int Clamp(int value, int maximum) => System.Math.Max(0, System.Math.Min(maximum, value));

        public BeautySettings Clone() => new BeautySettings
        {
            Enabled = Enabled,
            SmoothSkin = SmoothSkin,
            BrightenSkin = BrightenSkin,
            SkinTone = SkinTone,
            Sharpen = Sharpen,
            EyeSize = EyeSize,
            SlimFace = SlimFace,
            BlemishRemoval = BlemishRemoval,
            DarkCircleReduction = DarkCircleReduction
        };
    }
}
