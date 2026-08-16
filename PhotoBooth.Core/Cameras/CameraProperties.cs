using System.Collections.Generic;
using System.Linq;

namespace PhotoBooth.Core.Cameras
{
    public sealed class CameraProperties
    {
        public CameraProperties(IEnumerable<CameraProperty> items) { Items = items.ToList(); }
        public IReadOnlyList<CameraProperty> Items { get; }
        public CameraProperty Get(CameraPropertyKind kind) => Items.FirstOrDefault(x => x.Kind == kind);
    }
}
