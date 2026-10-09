using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoBooth.Core.Models
{
    // Collection membership and manual pins are independent sources of selection.
    public static class BoothLibrarySelection
    {
        public static IReadOnlyList<Frame> Frames(IEnumerable<Frame> assets, Settings settings)
        {
            var collections = new HashSet<Guid>(settings?.SelectedFrameCollectionIds ?? new Guid[0]);
            return assets.Where(x => x.IsPinned || (x.EventId.HasValue && collections.Contains(x.EventId.Value)))
                .GroupBy(x => x.Id).Select(x => x.First()).ToList();
        }

        public static IReadOnlyList<Preset> Presets(IEnumerable<Preset> assets, Settings settings)
        {
            var collections = new HashSet<Guid>(settings?.SelectedPresetCollectionIds ?? new Guid[0]);
            return assets.Where(x => x.IsPinned || (x.EventId.HasValue && collections.Contains(x.EventId.Value)))
                .GroupBy(x => x.Id).Select(x => x.First()).ToList();
        }
    }
}
