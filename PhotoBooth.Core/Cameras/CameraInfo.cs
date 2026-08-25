namespace PhotoBooth.Core.Cameras
{
    public sealed class CameraInfo
    {
        public CameraInfo(string id, string displayName, bool isConnected, string manufacturer = null, int batteryPercent = 0, bool supportsLiveView = false)
        {
            Id = id;
            DisplayName = displayName;
            IsConnected = isConnected;
            Manufacturer = manufacturer;
            BatteryPercent = batteryPercent;
            SupportsLiveView = supportsLiveView;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string FriendlyName
        {
            get
            {
                var value = string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
                if (string.IsNullOrWhiteSpace(value)) return "Camera";
                var deviceSuffix = value.IndexOf(" (@device", System.StringComparison.OrdinalIgnoreCase);
                if (deviceSuffix < 0) deviceSuffix = value.IndexOf(" (@usb", System.StringComparison.OrdinalIgnoreCase);
                return (deviceSuffix > 0 ? value.Substring(0, deviceSuffix) : value).Trim();
            }
        }
        public bool IsConnected { get; }
        public string Manufacturer { get; }
        public int BatteryPercent { get; }
        public bool SupportsLiveView { get; }
        public override string ToString() => FriendlyName;
    }
}
