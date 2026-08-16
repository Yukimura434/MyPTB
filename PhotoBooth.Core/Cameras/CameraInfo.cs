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
        public bool IsConnected { get; }
        public string Manufacturer { get; }
        public int BatteryPercent { get; }
        public bool SupportsLiveView { get; }
        public override string ToString() => DisplayName ?? Id ?? string.Empty;
    }
}
