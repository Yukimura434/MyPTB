using PhotoBooth.Core.Models;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class MediaTransformGeometryTests
    {
        [Fact]
        public void CoverAlwaysFillsSlotAndCentersLockedAxis()
        {
            var value=MediaTransformGeometry.Calculate(700,500,500,500,1,0,1);
            Assert.True(value.Width>=500);Assert.True(value.Height>=500);
            Assert.Equal(0,value.Top,6);Assert.Equal(0.5,value.CenterY,6);
            Assert.InRange(value.Left,-200,0);
        }

        [Fact]
        public void ZoomAndCenterAreClampedWithoutExposingEmptySpace()
        {
            var value=MediaTransformGeometry.Calculate(500,800,500,500,99,-20,20);
            Assert.Equal(2,value.Scale,6);
            Assert.Equal(1000,value.Width,6);Assert.Equal(1600,value.Height,6);
            Assert.InRange(value.Left,-500,0);Assert.InRange(value.Top,-1100,0);
            Assert.True(value.Left+value.Width>=500);Assert.True(value.Top+value.Height>=500);
        }

        [Fact]
        public void ResizeRecalculatesCoverScaleAndReclampsCenter()
        {
            var first=MediaTransformGeometry.Calculate(1600,900,400,400,1,0,0.5);
            var resized=MediaTransformGeometry.Calculate(1600,900,800,300,1,first.CenterX,first.CenterY);
            Assert.True(resized.Width>=800);Assert.True(resized.Height>=300);
            Assert.InRange(resized.Left,800-resized.Width,0);
            Assert.InRange(resized.Top,300-resized.Height,0);
        }
    }
}
