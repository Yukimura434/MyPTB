namespace PhotoBooth.Shared
{
    public sealed class ApplicationOptions
    {
        public string ApplicationName { get; set; }
        public string DataDirectory { get; set; }
        public string DatabasePath { get; set; }
        public bool UseFakeCamera { get; set; }
        public bool RestartLiveViewDuringRecovery { get; set; } = true;
        public string PhotoApiBaseUrl { get; set; }
        public string PhotoPageBaseUrl { get; set; }
        public int UploadMaxRetries { get; set; } = 2;
        public int UploadTimeoutSeconds { get; set; } = 120;
        public string LicensePublicKeyModulus { get; set; } = LicensePublicKey.Modulus;
        public string LicensePublicKeyExponent { get; set; } = LicensePublicKey.Exponent;
        public System.Collections.Generic.IDictionary<string, bool> Features { get; set; } = new System.Collections.Generic.Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["QR"] = true, ["Plugins"] = true, ["Diagnostics"] = true, ["Telemetry"] = false,
            ["ColorGpuLiveView"] = true, ["ColorGpuDiagnosticMonochrome"] = false,
            ["MotionPhoto"] = true,
            ["MotionPhotoNativeEncoder"] = true
        };
    }
}
