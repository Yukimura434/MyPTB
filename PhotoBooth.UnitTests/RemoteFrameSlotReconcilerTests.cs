using System;
using System.Collections.Generic;
using PhotoBooth.Core.Models;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class RemoteFrameSlotReconcilerTests
    {
        [Fact]
        public void Reconcile_removes_full_canvas_false_slot_and_uses_detected_geometry()
        {
            var remote = new List<FrameSlot>
            {
                Slot(0, 0, 0, 1024, 1536),
                Slot(1, 75, 65, 405, 665),
                Slot(2, 546, 65, 405, 665),
                Slot(3, 75, 804, 405, 665),
                Slot(4, 546, 804, 405, 665),
            };
            var detected = new List<FrameSlot>
            {
                Slot(0, 73, 63, 409, 668),
                Slot(1, 544, 63, 408, 668),
                Slot(2, 73, 802, 409, 670),
                Slot(3, 544, 802, 408, 670),
            };

            var result = RemoteFrameSlotReconciler.Reconcile(remote, detected);

            Assert.Equal(4, result.Count);
            Assert.Collection(result,
                slot => AssertSlot(slot, 0, 73, 63),
                slot => AssertSlot(slot, 1, 544, 63),
                slot => AssertSlot(slot, 2, 73, 802),
                slot => AssertSlot(slot, 3, 544, 802));
        }

        [Fact]
        public void Reconcile_preserves_a_valid_remote_capture_order()
        {
            var topLeft = Slot(1, 10, 10, 40, 40);
            var topRight = Slot(0, 60, 10, 40, 40);
            var detected = new[] { Slot(0, 10, 10, 40, 40), Slot(1, 60, 10, 40, 40) };

            var result = RemoteFrameSlotReconciler.Reconcile(new[] { topRight, topLeft }, detected);

            Assert.Equal(60, result[0].X);
            Assert.Equal(10, result[1].X);
        }

        static FrameSlot Slot(int index, int x, int y, int width, int height) => new FrameSlot
        {
            Id = Guid.NewGuid(), Index = index, X = x, Y = y, Width = width, Height = height
        };

        static void AssertSlot(FrameSlot slot, int index, int x, int y)
        {
            Assert.Equal(index, slot.Index);
            Assert.Equal(x, slot.X);
            Assert.Equal(y, slot.Y);
        }
    }
}
