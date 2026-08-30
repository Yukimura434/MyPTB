using PhotoBooth.Core.Models;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class LiveViewLayoutGeometryTests
    {
        [Fact]
        public void LandscapeMinimumUsesFivePercentFourByThreeAndIsCentered()
        {
            var layout = LiveViewLayoutGeometry.Calculate(1600, 900, 0, 5, 0, 0);
            Assert.Equal(5d, layout.Width * layout.Height / (1600d * 900d) * 100d, 3);
            Assert.Equal(4d / 3d, layout.Width / layout.Height, 3);
            Assert.Equal((1600d-layout.Width)/2d, layout.Left, 3);
            Assert.Equal((900d-layout.Height)/2d, layout.Top, 3);
        }

        [Fact]
        public void QuarterTurnUsesPortraitAspectAndStaysInsideCanvas()
        {
            var layout = LiveViewLayoutGeometry.Calculate(1600, 900, -90, 40, 100, 100);
            Assert.Equal(3d / 4d, layout.Width / layout.Height, 3);
            Assert.True(layout.Left >= 0 && layout.Top >= 0);
            Assert.True(layout.Left + layout.Width <= 1600.001);
            Assert.True(layout.Top + layout.Height <= 900.001);
        }

        [Fact]
        public void AreaIsClampedAndLegacyPositionsCannotMoveLiveViewOffCenter()
        {
            var minimum = LiveViewLayoutGeometry.Calculate(900, 1600, 90, -20, -20, -20);
            var maximum = LiveViewLayoutGeometry.Calculate(900, 1600, 90, 500, 500, 500);
            Assert.Equal(5d, minimum.Width * minimum.Height / (900d * 1600d) * 100d, 3);
            Assert.Equal(40d, maximum.Width * maximum.Height / (900d * 1600d) * 100d, 3);
            Assert.Equal((900d-minimum.Width)/2d, minimum.Left, 3);
            Assert.Equal((1600d-minimum.Height)/2d, minimum.Top, 3);
            Assert.Equal((900d-maximum.Width)/2d, maximum.Left, 3);
            Assert.Equal((1600d-maximum.Height)/2d, maximum.Top, 3);
        }
    }
}
