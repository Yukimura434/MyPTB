using System;
using System.IO;
using System.Linq;
using System.Threading;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CubeLutParserTests
    {
        [Fact]
        public void Parses_strict_3d_cube_and_preserves_domain()
        {
            var file=Write("TITLE \"Wedding\"\nLUT_3D_SIZE 2\nDOMAIN_MIN -1 0 0\nDOMAIN_MAX 1 2 3\n"+Samples(8));
            try{using(var data=new CubeLutParser().Parse(file,CancellationToken.None)){Assert.Equal(2,data.Metadata.CubeSize);Assert.Equal("Wedding",data.Metadata.Title);Assert.Equal(-1,data.Metadata.DomainMinR);Assert.Equal(24,data.Values.Length);}}
            finally{File.Delete(file);}
        }
        [Theory]
        [InlineData("LUT_1D_SIZE 2\n0 0 0\n1 1 1")]
        [InlineData("LUT_3D_SIZE 129\n")]
        [InlineData("LUT_3D_SIZE 2\n0 0 0\n")]
        public void Rejects_unsupported_or_incomplete_cube(string content)
        {
            var file=Write(content);try{Assert.Throws<InvalidDataException>(()=>new CubeLutParser().Parse(file,CancellationToken.None));}finally{File.Delete(file);}
        }
        [Fact]
        public void Resolver_rejects_paths_outside_data_root()
        {
            var root=Path.Combine(Path.GetTempPath(),"photobooth-path-"+Guid.NewGuid().ToString("N"));
            try{var resolver=new ColorLutPathResolver(new ApplicationOptions{DataDirectory=root});Assert.Throws<InvalidDataException>(()=>resolver.GetFullPath("../outside.cube"));Assert.StartsWith(Path.GetFullPath(root),resolver.GetFullPath(resolver.CreateRelativeAssetPath(Guid.NewGuid(),new string('a',64))),StringComparison.OrdinalIgnoreCase);}
            finally{try{Directory.Delete(root,true);}catch{}}
        }
        static string Write(string text){var file=Path.Combine(Path.GetTempPath(),"lut-"+Guid.NewGuid().ToString("N")+".cube");File.WriteAllText(file,text);return file;}
        static string Samples(int count)=>string.Join("\n",Enumerable.Range(0,count).Select(i=>(i%2)+" "+((i/2)%2)+" "+((i/4)%2)))+"\n";
    }
}
