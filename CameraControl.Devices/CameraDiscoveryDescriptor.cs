namespace CameraControl.Devices
{
    public sealed class CameraDiscoveryDescriptor
    {
        public CameraDiscoveryDescriptor(string id, string displayName, string manufacturer)
        {
            Id = id;
            DisplayName = displayName;
            Manufacturer = manufacturer;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Manufacturer { get; }
    }
}
