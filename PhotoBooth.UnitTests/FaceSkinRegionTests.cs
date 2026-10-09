using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PhotoBooth.Core.Models;
using PhotoBooth.OpenCvRetouch;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class FaceSkinRegionTests
    {
        static readonly MethodInfo Build = typeof(OpenCvBeautyRetouchService).Assembly
            .GetType("PhotoBooth.OpenCvRetouch.FaceSkinMaskBuilder").GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static);

        [Theory]
        [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)]
        [InlineData(0, -18)] [InlineData(1, 18)] [InlineData(2, -18)]
        public void Mask_selects_forehead_and_cheeks_but_protects_features_hair_and_background(int tone, int angle)
        {
            Assert.Equal(4, IntPtr.Size);
            var points = Landmarks();
            using (var original = Fixture(tone))
            using (var image = new Mat())
            using (var rotation = Cv2.GetRotationMatrix2D(new Point2f(200, 210), angle, 1))
            using (var skin = Black(original.Size()))
            using (var feature = Black(original.Size()))
            using (var eyes = Black(original.Size()))
            {
                Cv2.WarpAffine(original, image, rotation, original.Size(), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(20, 50, 20));
                Func<Point2f, Point2f> transform = p => new Point2f(
                    (float)(rotation.At<double>(0,0)*p.X+rotation.At<double>(0,1)*p.Y+rotation.At<double>(0,2)),
                    (float)(rotation.At<double>(1,0)*p.X+rotation.At<double>(1,1)*p.Y+rotation.At<double>(1,2)));
                Build.Invoke(null, new object[] { image, new[] { points.Select(transform).ToArray() }, 1d, skin, feature, eyes });
                Assert.True(Value(skin, transform(new Point2f(200,110))) > 180, "Visible forehead must be included.");
                Assert.True(Value(skin, transform(new Point2f(170,230))) > 180, "Cheek skin must be included for every skin tone.");
                foreach (var point in new[] { new Point2f(145,146), new Point2f(148,180), new Point2f(200,290), new Point2f(200,70), new Point2f(121,235), new Point2f(250,215), new Point2f(20,300), new Point2f(200,395) })
                    Assert.Equal(0, Value(skin, transform(point)));
                using (var excess = new Mat())
                {
                    Cv2.Subtract(eyes, skin, excess);
                    Assert.Equal(0, Cv2.CountNonZero(excess));
                }
                Assert.True(Cv2.CountNonZero(eyes) > 0);
            }
        }

        [Fact]
        public void Two_different_skin_tones_get_independent_masks_and_invalid_landmarks_are_ignored()
        {
            using (var left = Fixture(0)) using (var right = Fixture(2))
            using (var image = new Mat(new Size(800,480),MatType.CV_8UC3,new Scalar(20,50,20)))
            using (var skin = Black(image.Size())) using (var features = Black(image.Size())) using (var eyes = Black(image.Size()))
            {
                using(var roi=new Mat(image,new Rect(0,0,400,480)))left.CopyTo(roi);
                using(var roi=new Mat(image,new Rect(400,0,400,480)))right.CopyTo(roi);
                var points=Landmarks();var second=points.Select(p=>new Point2f(p.X+400,p.Y)).ToArray();
                Build.Invoke(null,new object[]{image,new[]{points,second,new Point2f[68]},1d,skin,features,eyes});
                Assert.True(Value(skin,new Point2f(200,110))>180);
                Assert.True(Value(skin,new Point2f(600,110))>180);
                Assert.Equal(0,Value(skin,new Point2f(400,220)));
                Assert.Equal(0,Value(skin,new Point2f(545,146)));
            }
        }

        [Fact]
        public void Full_resolution_masks_keep_feature_exclusions_after_resizing()
        {
            using(var small=Fixture(1))using(var large=new Mat())
            using(var skin=Black(new Size(1600,1920)))using(var features=Black(skin.Size()))using(var eyes=Black(skin.Size()))
            {
                Cv2.Resize(small,large,skin.Size());
                Build.Invoke(null,new object[]{large,new[]{Landmarks()},.25d,skin,features,eyes});
                Assert.True(Value(skin,new Point2f(800,440))>180);
                foreach(var p in new[]{new Point2f(145,146),new Point2f(148,180),new Point2f(200,290),new Point2f(121,235)})
                    Assert.Equal(0,Value(skin,new Point2f(p.X*4,p.Y*4)));
            }
        }

        [Fact]
        public void Overlapping_face_regions_keep_both_sets_of_eyes_and_lips_protected()
        {
            using (var image = new Mat(new Size(500, 500), MatType.CV_8UC3, new Scalar(90, 130, 175)))
            using (var skin = Black(image.Size())) using (var features = Black(image.Size())) using (var eyes = Black(image.Size()))
            {
                var first = Landmarks();
                var second = first.Select(p => new Point2f(p.X + 65, p.Y + 35)).ToArray();
                FaceSkinMaskBuilder.Build(image, new[] { first, second }, 1, skin, features, eyes);
                Assert.True(Cv2.CountNonZero(skin) > 0);
                foreach (var offset in new[] { new Point2f(0, 0), new Point2f(65, 35) })
                    foreach (var point in new[] { new Point2f(148, 180), new Point2f(252, 180), new Point2f(200, 290) })
                    {
                        var p = new Point2f(point.X + offset.X, point.Y + offset.Y);
                        Assert.Equal(0, Value(skin, p)); Assert.Equal(0, Value(eyes, p));
                    }
            }
        }

        [Fact]
        public void Face_completely_outside_image_cannot_create_skin_or_throw()
        {
            using (var image = Fixture(1))
            using (var skin = Black(image.Size())) using (var features = Black(image.Size())) using (var eyes = Black(image.Size()))
            {
                var outside = Landmarks().Select(p => new Point2f(p.X - 2000, p.Y - 2000)).ToArray();
                FaceSkinMaskBuilder.Build(image, new[] { outside }, 1, skin, features, eyes);
                Assert.Equal(0, Cv2.CountNonZero(skin));
                Assert.Equal(0, Cv2.CountNonZero(features));
                Assert.Equal(0, Cv2.CountNonZero(eyes));
            }
        }

        [Fact]
        public void Blemish_dilation_and_under_eye_brightening_cannot_escape_their_masks()
        {
            using(var image=new Mat(new Size(64,64),MatType.CV_8UC3,new Scalar(65,100,180)))
            using(var mask=Black(image.Size()))using(var before=image.Clone())
            {
                Cv2.Rectangle(mask,new Rect(12,12,40,40),Scalar.White,-1);
                Cv2.Circle(mask,new Point(32,32),8,Scalar.Black,-1);
                typeof(OpenCvBeautyRetouchService).GetMethod("ApplyBlemishRemoval",BindingFlags.NonPublic|BindingFlags.Static)
                    .Invoke(null,new object[]{image,mask,100});
                typeof(OpenCvBeautyRetouchService).GetMethod("ApplyDarkCircleReduction",BindingFlags.NonPublic|BindingFlags.Static)
                    .Invoke(null,new object[]{image,mask,100});
                for(var y=0;y<64;y++)for(var x=0;x<64;x++)if(mask.At<byte>(y,x)==0)
                    Assert.Equal(before.At<Vec3b>(y,x),image.At<Vec3b>(y,x));
            }
        }

        [Fact]
        public async Task Live_preview_drops_previous_face_mask_immediately_when_person_leaves()
        {
            Assert.Equal(4,IntPtr.Size);
            var input=File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","TestImages","harry-potter.jpg"));
            var assets=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Assets","Beauty");
            using(var decoded=Cv2.ImDecode(input,ImreadModes.Color))
            using(var blank=new Mat(decoded.Size(),MatType.CV_8UC3,new Scalar(90,110,140)))
            using(var live=new OpenCvLiveBeautyPreviewService(assets))
            {
                var settings=new BeautySettings{Enabled=true,BrightenSkin=100};
                var processed=await OpenCvBeautyRetouchTests.WaitForBeauty(live,input,settings);
                Assert.True(processed.BeautyApplied);
                var noFace=blank.ImEncode(".jpg");
                Assert.False((await live.ProcessAsync(noFace,settings,CancellationToken.None)).BeautyApplied);
            }
        }

        static Mat Black(Size size)=>new Mat(size,MatType.CV_8UC1,Scalar.Black);
        static byte Value(Mat mask,Point2f p)=>mask.At<byte>((int)Math.Round(p.Y),(int)Math.Round(p.X));
        static Mat Fixture(int tone)
        {
            var colors=new[]{new Scalar(45,70,95),new Scalar(90,130,175),new Scalar(165,190,220)};
            var image=new Mat(new Size(400,480),MatType.CV_8UC3,new Scalar(20,50,20));
            Cv2.Ellipse(image,new Point(200,210),new Size(110,170),0,0,360,colors[tone],-1);
            // Hair fringe and a dark occluder on one cheek.
            Cv2.Rectangle(image,new Rect(90,30,220,55),new Scalar(12,12,12),-1);
            Cv2.Rectangle(image,new Rect(115,205,13,65),new Scalar(12,12,12),-1);
            // A thin frame with skin-like chroma: chroma/face geometry alone cannot reject it.
            var color=colors[tone];
            Cv2.Line(image,new Point(235,215),new Point(278,215),new Scalar(color.Val0*.65,color.Val1*.65,color.Val2*.65),2);
            return image;
        }
        static Point2f[] Landmarks()
        {
            var p=new Point2f[68];
            for(var i=0;i<=16;i++)p[i]=new Point2f((float)(200-Math.Cos(i*Math.PI/16)*110),(float)(175+Math.Sin(i*Math.PI/16)*170));
            var brows=new[]{new Point2f(112,155),new Point2f(130,145),new Point2f(145,145),new Point2f(162,150),new Point2f(174,157),new Point2f(226,157),new Point2f(238,150),new Point2f(255,145),new Point2f(270,145),new Point2f(288,155)};
            Array.Copy(brows,0,p,17,10);
            var nose=new[]{new Point2f(200,170),new Point2f(200,190),new Point2f(200,215),new Point2f(200,240),new Point2f(182,250),new Point2f(192,254),new Point2f(200,257),new Point2f(208,254),new Point2f(218,250)};
            Array.Copy(nose,0,p,27,9);
            var eyes=new[]{new Point2f(120,180),new Point2f(132,168),new Point2f(156,168),new Point2f(176,180),new Point2f(156,190),new Point2f(132,190),new Point2f(224,180),new Point2f(244,168),new Point2f(268,168),new Point2f(280,180),new Point2f(268,190),new Point2f(244,190)};
            Array.Copy(eyes,0,p,36,12);
            for(var i=0;i<12;i++)p[48+i]=new Point2f((float)(200-Math.Cos(i*Math.PI/6)*42),(float)(290-Math.Sin(i*Math.PI/6)*18));
            for(var i=0;i<8;i++)p[60+i]=new Point2f((float)(200-Math.Cos(i*Math.PI/4)*25),(float)(290-Math.Sin(i*Math.PI/4)*8));
            return p;
        }
    }
}
