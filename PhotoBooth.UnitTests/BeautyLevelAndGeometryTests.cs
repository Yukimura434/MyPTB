using System;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using PhotoBooth.Core.Models;
using PhotoBooth.OpenCvRetouch;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class BeautyLevelAndGeometryTests
    {
        [Fact]
        public void Blemish_repair_preserves_broad_skin_tone_and_bounds_local_color_change()
        {
            using(var image=new Mat(100,100,MatType.CV_8UC3,new Scalar(90,120,170)))
            using(var mask=new Mat(100,100,MatType.CV_8UC1,Scalar.White))
            {
                Cv2.Circle(image,new Point(65,65),20,new Scalar(85,105,205),-1);
                Cv2.Circle(image,new Point(20,20),2,new Scalar(30,45,80),-1);
                using(var before=image.Clone())
                {
                    OpenCvBeautyRetouchService.ApplyBlemishRemoval(image,mask,100);
                    Assert.Equal(before.At<Vec3b>(65,65),image.At<Vec3b>(65,65));
                    Assert.True(Cv2.Norm(before,image,NormTypes.L1)>0);
                    Assert.True(Cv2.Norm(before,image,NormTypes.INF)<=24);
                }
            }
        }
        [Theory]
        [InlineData(0,0,0,0,0,0)][InlineData(1,6,2,7,10,2)][InlineData(5,30,10,35,50,10)][InlineData(10,60,20,70,100,20)]
        public void Master_level_uses_separate_skin_and_geometry_scales(int level,int smooth,int light,int blemish,int sharpen,int geometry)
        {
            var s=new BeautySettings{Enabled=true};s.SetLevel(level);
            Assert.Equal(new[]{smooth,light,light,sharpen,blemish,0},new[]{s.SmoothSkin,s.BrightenSkin,s.SkinTone,s.Sharpen,s.BlemishRemoval,s.DarkCircleReduction});
            Assert.Equal(geometry,s.EyeSize);Assert.Equal(geometry,s.SlimFace);Assert.Equal(level,s.Level);
            s.SlimFace=49;Assert.Null(s.Level);Assert.Equal(49,s.Clone().SlimFace);
        }

        [Fact]
        public void Geometry_scale_mapping_and_image_edges_remain_stable_at_maximum()
        {
            var p=Enumerable.Range(0,68).Select(_=>new Point2f(160,160)).ToArray();
            p[0]=new Point2f(80,140);p[16]=new Point2f(240,140);p[8]=new Point2f(160,260);p[19]=new Point2f(130,110);p[24]=new Point2f(190,110);
            for(int i=0;i<12;i++)p[36+i]=new Point2f((i<6?130:190)+(float)Math.Cos(i*Math.PI/3)*12,145+(float)Math.Sin(i*Math.PI/3)*7);
            using(var original=new Mat(320,320,MatType.CV_8UC3))
            {
                for(int y=0;y<320;y++)for(int x=0;x<320;x++)original.Set(y,x,new Vec3b((byte)(x%251),(byte)(y%251),(byte)((x+y)%251)));
                using(var full=original.Clone())using(var scaled=original.Clone())using(var capped=original.Clone())
                {
                    var settings=new BeautySettings{EyeSize=20,SlimFace=20};
                    FaceGeometry.Apply(full,new[]{p},1,settings,CancellationToken.None);
                    FaceGeometry.Apply(scaled,new[]{p.Select(v=>new Point2f(v.X*.5f,v.Y*.5f)).ToArray()},.5,settings,CancellationToken.None);
                    FaceGeometry.Apply(capped,new[]{p},1,new BeautySettings{EyeSize=500,SlimFace=500},CancellationToken.None);
                    Assert.Equal(0,Cv2.Norm(full,scaled,NormTypes.INF));Assert.Equal(0,Cv2.Norm(full,capped,NormTypes.INF));
                    Assert.True(Cv2.Norm(full,original,NormTypes.L1)>0);Assert.Equal(original.Size(),full.Size());
                    for(int x=0;x<320;x++){Assert.Equal(original.At<Vec3b>(0,x),full.At<Vec3b>(0,x));Assert.Equal(original.At<Vec3b>(319,x),full.At<Vec3b>(319,x));}
                    var edge=p.Select(v=>new Point2f(v.X-120,v.Y-100)).ToArray();
                    FaceGeometry.Apply(full,new[]{edge},1,settings,CancellationToken.None);Assert.Equal(original.Size(),full.Size());
                }
            }
        }
    }
}
