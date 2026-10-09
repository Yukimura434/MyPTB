using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace PhotoBooth.OpenCvRetouch
{
    // Image-aware, conservative face masking. No global light-skin thresholds:
    // each face supplies its own cheek/nose chroma and relative luminance model.
    internal static class FaceSkinMaskBuilder
    {
        internal static void Build(Mat image, Point2f[][] faces, double landmarkScale,
            Mat skin, Mat features, Mat underEyes)
        {
            skin.SetTo(Scalar.Black); features.SetTo(Scalar.Black); underEyes.SetTo(Scalar.Black);
            if (faces == null || faces.Length == 0 || landmarkScale <= 0) return;
            var scale = Math.Min(1d, 1280d / Math.Max(image.Width, image.Height));
            using (var working = new Mat())
            using (var colors = new Mat())
            {
                Cv2.Resize(image, working, new Size(0, 0), scale, scale, InterpolationFlags.Area);
                Cv2.CvtColor(working, colors, ColorConversionCodes.BGR2YCrCb);
                using (var skinUnion = Black(working.Size()))
                using (var featureUnion = Black(working.Size()))
                using (var eyeUnion = Black(working.Size()))
                using (var protectedUnion = Black(working.Size()))
                {
                    foreach (var face in faces)
                    {
                        if (face == null || face.Length != 68) continue;
                        var p = face.Select(v => new Point2f((float)(v.X * scale / landmarkScale), (float)(v.Y * scale / landmarkScale))).ToArray();
                        if (p.Any(v => float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsInfinity(v.X) || float.IsInfinity(v.Y))) continue;
                        var leftEye = Average(p, 36, 6); var rightEye = Average(p, 42, 6);
                        var eyeDistance = Distance(leftEye, rightEye);
                        var browCenter = Average(p, 17, 10);
                        var height = Distance(browCenter, p[8]);
                        if (eyeDistance < 8 || height < eyeDistance * .5 || height > eyeDistance * 4) continue;
                        var up = new Point2f((browCenter.X - p[8].X) / height, (browCenter.Y - p[8].Y) / height);
                        // Work only around this face, with a halo for local-tone blur,
                        // feature dilation and feathering. Reconcile exclusions globally
                        // afterwards so overlapping faces cannot expose protected features.
                        var bounds = FaceBounds(p, up, height, eyeDistance, working.Size());
                        if (bounds.Width == 0 || bounds.Height == 0) continue;
                        // Round in image coordinates before subtracting the integer
                        // origin: rounding translated half-pixels can change edge pixels.
                        Func<Point2f, Point> localPoint = v => ToPoint(v) - bounds.Location;
                        using (var faceColors = new Mat(colors, bounds))
                        using (var skinPart = new Mat(skinUnion, bounds))
                        using (var featurePart = new Mat(featureUnion, bounds))
                        using (var eyePart = new Mat(eyeUnion, bounds))
                        using (var protectedPart = new Mat(protectedUnion, bounds))
                        using (var region = Black(bounds.Size))
                        using (var excluded = Black(bounds.Size))
                        using (var seeds = Black(bounds.Size))
                        using (var selected = Black(bounds.Size))
                        using (var detail = Black(bounds.Size))
                        using (var eyeArea = Black(bounds.Size))
                        {
                            var outline = p.Take(17).Select(localPoint).ToList();
                            // Forehead follows the face's own vertical axis, including roll.
                            foreach (var i in new[] { 26, 24, 22, 21, 19, 17 })
                            {
                                var amount = i == 26 || i == 17 ? .28 : i == 24 || i == 19 ? .62 : .70;
                                outline.Add(localPoint(new Point2f((float)(p[i].X + up.X * height * amount), (float)(p[i].Y + up.Y * height * amount))));
                            }
                            Cv2.FillPoly(region, new[] { outline.ToArray() }, Scalar.White);
                            Protect(p, excluded, detail, eyeDistance, bounds.Location);
                            Cv2.BitwiseOr(protectedPart, excluded, protectedPart);
                            Cv2.BitwiseAnd(detail, region, detail);
                            Cv2.BitwiseOr(featurePart, detail, featurePart);

                            Circle(seeds, Mix(p[2], p[31], p[48]), eyeDistance * .12, bounds.Location);
                            Circle(seeds, Mix(p[14], p[35], p[54]), eyeDistance * .12, bounds.Location);
                            Circle(seeds, new Point2f((p[27].X + p[30].X) * .5f, (p[27].Y + p[30].Y) * .5f), eyeDistance * .07, bounds.Location);
                            Cv2.BitwiseAnd(seeds, region, seeds); seeds.SetTo(Scalar.Black, excluded);
                            if (!SelectSkin(faceColors, region, seeds, selected, eyeDistance)) continue;
                            selected.SetTo(Scalar.Black, excluded);
                            KeepSeededComponents(selected, seeds);
                            FeatherSkinInside(selected, Math.Max(3, eyeDistance * .10));
                            // Alpha masks combine by maximum; bitwise OR can create
                            // stronger, discontinuous values where soft masks overlap.
                            Cv2.Max(skinPart, selected, skinPart);

                            foreach (var start in new[] { 36, 42 })
                            {
                                var a = p[start]; var b = p[start + 3];
                                var drop = eyeDistance * .13;
                                Cv2.FillPoly(eyeArea, new[] { new[] { localPoint(a), localPoint(b),
                                    localPoint(new Point2f((float)(b.X - up.X * drop), (float)(b.Y - up.Y * drop))),
                                    localPoint(new Point2f((float)(a.X - up.X * drop), (float)(a.Y - up.Y * drop))) } }, Scalar.White);
                            }
                            FeatherInside(eyeArea, Math.Max(.8, eyeDistance * .015));
                            Cv2.Min(eyeArea, selected, eyeArea);
                            Cv2.BitwiseOr(eyePart, eyeArea, eyePart);
                        }
                    }
                    // A feature of one overlapping face must never be retouched as another's skin.
                    skinUnion.SetTo(Scalar.Black, protectedUnion);
                    eyeUnion.SetTo(Scalar.Black, protectedUnion);
                    Cv2.Resize(skinUnion, skin, image.Size(), 0, 0, InterpolationFlags.Linear);
                    Cv2.Resize(featureUnion, features, image.Size(), 0, 0, InterpolationFlags.Linear);
                    Cv2.Resize(eyeUnion, underEyes, image.Size(), 0, 0, InterpolationFlags.Linear);
                    // Enforce exclusions after upsampling too; interpolation cannot refill holes.
                    using (var exclusions = new Mat())
                    {
                        Cv2.Resize(protectedUnion, exclusions, image.Size(), 0, 0, InterpolationFlags.Nearest);
                        skin.SetTo(Scalar.Black, exclusions); underEyes.SetTo(Scalar.Black, exclusions);
                        Cv2.Resize(skinUnion, exclusions, image.Size(), 0, 0, InterpolationFlags.Nearest);
                        Cv2.Threshold(exclusions, exclusions, 0, 255, ThresholdTypes.BinaryInv);
                        skin.SetTo(Scalar.Black, exclusions); underEyes.SetTo(Scalar.Black, exclusions);
                    }
                }
            }
        }

        static Rect FaceBounds(Point2f[] points, Point2f up, float height, float eyeDistance, Size size)
        {
            var extent = points.Concat(new[] { 26, 24, 22, 21, 19, 17 }.Select(i =>
                new Point2f(points[i].X + up.X * height * .70f, points[i].Y + up.Y * height * .70f))).ToArray();
            var halo = Math.Max(16, Math.Ceiling(eyeDistance * .25));
            int left = (int)Math.Max(0, Math.Min(size.Width, Math.Floor(extent.Min(p => p.X) - halo)));
            int top = (int)Math.Max(0, Math.Min(size.Height, Math.Floor(extent.Min(p => p.Y) - halo)));
            int right = (int)Math.Max(0, Math.Min(size.Width, Math.Ceiling(extent.Max(p => p.X) + halo + 1)));
            int bottom = (int)Math.Max(0, Math.Min(size.Height, Math.Ceiling(extent.Max(p => p.Y) + halo + 1)));
            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        static Mat Black(Size size) => new Mat(size, MatType.CV_8UC1, Scalar.Black);
        static Point ToPoint(Point2f p) => new Point(Math.Round(p.X), Math.Round(p.Y));
        static float Distance(Point2f a, Point2f b) => (float)Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        static Point2f Average(Point2f[] p, int start, int count) => new Point2f(p.Skip(start).Take(count).Average(v=>v.X),p.Skip(start).Take(count).Average(v=>v.Y));
        static Point2f Mix(Point2f jaw, Point2f nose, Point2f mouth) => new Point2f(jaw.X*.35f+nose.X*.4f+mouth.X*.25f,jaw.Y*.35f+nose.Y*.4f+mouth.Y*.25f);
        static void Circle(Mat mask, Point2f center, double radius, Point origin) => Cv2.Circle(mask, ToPoint(center) - origin, Math.Max(2,(int)Math.Round(radius)), Scalar.White, -1);

        static void Protect(Point2f[] p, Mat excluded, Mat detail, double eyeDistance, Point origin)
        {
            Func<Point2f, Point> local = v => ToPoint(v) - origin;
            foreach(var start in new[]{36,42})
                Cv2.FillPoly(detail,new[]{p.Skip(start).Take(6).Select(local).ToArray()},Scalar.White);
            foreach(var start in new[]{17,22})
                Cv2.Polylines(detail,new[]{p.Skip(start).Take(5).Select(local).ToArray()},false,Scalar.White,Math.Max(3,(int)Math.Round(eyeDistance*.10)));
            Cv2.FillPoly(detail,new[]{p.Skip(48).Take(12).Select(local).ToArray()},Scalar.White);
            var radius=Math.Max(1,(int)Math.Round(eyeDistance*.04));
            using(var kernel=Cv2.GetStructuringElement(MorphShapes.Ellipse,new Size(radius*2+1,radius*2+1)))
                Cv2.Dilate(detail,excluded,kernel);
            // Preserve nostrils as detail, without excluding the skin on the nose bridge.
            Cv2.Polylines(excluded,new[]{p.Skip(31).Take(5).Select(local).ToArray()},false,Scalar.White,Math.Max(2,(int)Math.Round(eyeDistance*.035)));
        }

        static unsafe bool SelectSkin(Mat colors, Mat region, Mat seeds, Mat output, double eyeDistance)
        {
            var ys=new List<int>();var crs=new List<int>();var cbs=new List<int>();
            // Mat dimension properties cross the native boundary; cache them outside
            // pixel loops. This changes no mask thresholds or pixel decisions.
            int width=colors.Width,height=colors.Height;
            for(var y=0;y<height;y++)
            {
                var color=(byte*)colors.Ptr(y);var seed=(byte*)seeds.Ptr(y);
                for(var x=0;x<width;x++)if(seed[x]!=0){ys.Add(color[x*3]);crs.Add(color[x*3+1]);cbs.Add(color[x*3+2]);}
            }
            if(ys.Count<12)return false;
            var medianY=Median(ys);var medianCr=Median(crs);var medianCb=Median(cbs);
            var spreadCr=Math.Max(12,Math.Min(24,Median(crs.Select(v=>Math.Abs(v-medianCr)).ToList())*3));
            var spreadCb=Math.Max(12,Math.Min(24,Median(cbs.Select(v=>Math.Abs(v-medianCb)).ToList())*3));
            using(var luminance=new Mat())using(var localTone=new Mat())
            {
                Cv2.ExtractChannel(colors,luminance,0);
                Cv2.GaussianBlur(luminance,localTone,new Size(0,0),Math.Max(1.5,eyeDistance*.025));
                for(var y=0;y<height;y++)
                {
                    var color=(byte*)colors.Ptr(y);var area=(byte*)region.Ptr(y);var mask=(byte*)output.Ptr(y);var local=(byte*)localTone.Ptr(y);
                    for(var x=0;x<width;x++)
                    {
                        if(area[x]==0)continue;
                        var yy=color[x*3];var cr=(color[x*3+1]-medianCr)/(double)spreadCr;var cb=(color[x*3+2]-medianCb)/(double)spreadCb;
                        // Reject dark fine structures (glasses frames, hair strands) as well
                        // as broad occluders. Mild skin texture remains eligible for retouch.
                        if(yy>=medianY*.52 && yy>=local[x]-Math.Max(12,local[x]*.20)
                            && yy<=Math.Min(255,medianY*1.65+20) && cr*cr+cb*cb<=1.8)
                            mask[x]=255;
                    }
                }
            }
            return true;
        }

        static int Median(List<int> values){values.Sort();return values[values.Count/2];}
        static unsafe void KeepSeededComponents(Mat mask, Mat seeds)
        {
            using(var labels=new Mat())
            {
                var count=Cv2.ConnectedComponents(mask,labels,PixelConnectivity.Connectivity8,MatType.CV_32S);
                var keep=new bool[count];
                int width=mask.Width,height=mask.Height;
                for(var y=0;y<height;y++)
                {
                    var row=(int*)labels.Ptr(y);var seed=(byte*)seeds.Ptr(y);
                    for(var x=0;x<width;x++)if(seed[x]!=0 && row[x]>0)keep[row[x]]=true;
                }
                for(var y=0;y<height;y++)
                {
                    var row=(int*)labels.Ptr(y);var pixels=(byte*)mask.Ptr(y);
                    for(var x=0;x<width;x++)if(!keep[row[x]])pixels[x]=0;
                }
            }
        }

        static void FeatherInside(Mat mask, double sigma)
        {
            using(var blurred=new Mat())
            {
                Cv2.GaussianBlur(mask,blurred,new Size(0,0),sigma);
                Cv2.Min(mask,blurred,mask);
            }
        }

        internal static unsafe void FeatherSkinInside(Mat mask, double width)
        {
            // Gaussian blur clipped to a binary mask starts near 50% at its edge.
            // Fade from zero INSIDE the accepted skin instead: no spill over hair,
            // brows, eyes or lips, and no bright cutout outline on protected areas.
            using (var distance = new Mat())
            {
                Cv2.DistanceTransform(mask, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                int rows = mask.Rows, columns = mask.Cols;
                for (int y = 0; y < rows; y++)
                {
                    var pixels = (byte*)mask.Ptr(y); var d = (float*)distance.Ptr(y);
                    for (int x = 0; x < columns; x++)
                    {
                        double t = Math.Max(0, Math.Min(1, (d[x] - 1) / width));
                        pixels[x] = (byte)Math.Round(pixels[x] * t * t * (3 - 2 * t));
                    }
                }
            }
        }
    }
}
