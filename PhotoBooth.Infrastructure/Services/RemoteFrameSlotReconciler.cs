using System;
using System.Collections.Generic;
using System.Linq;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Infrastructure.Services
{
    internal static class RemoteFrameSlotReconciler
    {
        const double MinimumIntersectionOverUnion = 0.5d;

        public static IReadOnlyList<FrameSlot> Reconcile(IReadOnlyList<FrameSlot> remote, IReadOnlyList<FrameSlot> detected)
        {
            if (detected == null || detected.Count == 0) throw new InvalidOperationException("Không tìm thấy ô ảnh hợp lệ trong frame PNG.");
            var remaining = detected.ToList();
            var ordered = new List<FrameSlot>();
            foreach (var candidate in (remote ?? new FrameSlot[0]).OrderBy(slot => slot.Index))
            {
                var match = remaining.Select(slot => new { Slot = slot, Score = IntersectionOverUnion(candidate, slot) })
                    .OrderByDescending(value => value.Score).FirstOrDefault();
                if (match == null || match.Score < MinimumIntersectionOverUnion) continue;
                ordered.Add(match.Slot);
                remaining.Remove(match.Slot);
            }
            ordered.AddRange(remaining.OrderBy(slot => slot.Y).ThenBy(slot => slot.X));
            return ordered.Select((slot, index) => new FrameSlot
            {
                Id = Guid.NewGuid(), Index = index, X = slot.X, Y = slot.Y, Width = slot.Width, Height = slot.Height
            }).ToList();
        }

        static double IntersectionOverUnion(FrameSlot left, FrameSlot right)
        {
            var x1 = Math.Max(left.X, right.X); var y1 = Math.Max(left.Y, right.Y);
            var x2 = Math.Min(left.X + left.Width, right.X + right.Width);
            var y2 = Math.Min(left.Y + left.Height, right.Y + right.Height);
            var intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            if (intersection == 0) return 0d;
            var union = left.Width * left.Height + right.Width * right.Height - intersection;
            return union <= 0 ? 0d : (double)intersection / union;
        }
    }
}
