using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class TetrahedralColorLutTests
    {
        [Theory]
        [InlineData(0.8f,0.5f,0.2f)]
        [InlineData(0.8f,0.2f,0.5f)]
        [InlineData(0.5f,0.2f,0.8f)]
        [InlineData(0.2f,0.5f,0.8f)]
        [InlineData(0.2f,0.8f,0.5f)]
        [InlineData(0.5f,0.8f,0.2f)]
        public void Identity_cube_preserves_rgb_in_all_tetrahedra(float r,float g,float b)
        {
            var values=new float[24];var index=0;
            for(var blue=0;blue<2;blue++)for(var green=0;green<2;green++)for(var red=0;red<2;red++)
            {values[index++]=red;values[index++]=green;values[index++]=blue;}
            var lut=new ColorLutData{Metadata=new ColorLutMetadata{CubeSize=2},Values=values};
            float actualR,actualG,actualB;ColorLutService.Sample(lut,r,g,b,out actualR,out actualG,out actualB);
            Assert.Equal(r,actualR,4);Assert.Equal(g,actualG,4);Assert.Equal(b,actualB,4);
        }
    }
}
