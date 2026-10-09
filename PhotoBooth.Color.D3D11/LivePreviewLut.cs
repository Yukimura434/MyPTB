using System;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace PhotoBooth.Color.D3D11
{
    /// <summary>Immutable, bounded preview cube. Precomputes byte-to-domain coordinates once,
    /// then applies tetrahedral interpolation to an owned BGRA buffer on the image worker.</summary>
    public sealed class LivePreviewLut
    {
        readonly float[] values;
        readonly int[] red = new int[256], green = new int[256], blue = new int[256];
        readonly float[] rf = new float[256], gf = new float[256], bf = new float[256];
        readonly int size;
        readonly float strength;

        public LivePreviewLut(float[] values, int size, float strength,
            float minR = 0, float minG = 0, float minB = 0,
            float maxR = 1, float maxG = 1, float maxB = 1)
        {
            if (size < 2 || size > 65 || values == null || values.Length != size * size * size * 3)
                throw new ArgumentException("Invalid preview cube.");
            if (!Finite(strength) || !Finite(minR) || !Finite(minG) || !Finite(minB) ||
                !Finite(maxR) || !Finite(maxG) || !Finite(maxB) || maxR <= minR || maxG <= minG || maxB <= minB)
                throw new ArgumentException("Invalid preview domain or strength.");
            this.values = (float[])values.Clone(); this.size = size;
            this.strength = Math.Max(0, Math.Min(1, strength));
            for (var i = 0; i < this.values.Length; i++)
            {
                if (!Finite(this.values[i])) throw new ArgumentException("Nonfinite preview value.");
                this.values[i] *= 255f * this.strength;
            }
            Coordinates(red, rf, minR, maxR, 3);
            Coordinates(green, gf, minG, maxG, size * 3);
            Coordinates(blue, bf, minB, maxB, size * size * 3);
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        void Coordinates(int[] offsets, float[] fractions, float min, float max, int step)
        {
            for (var i = 0; i < 256; i++)
            {
                var p = Math.Max(0, Math.Min(1, (i / 255f - min) / (max - min))) * (size - 1);
                var lower = Math.Min(size - 2, (int)p);
                offsets[i] = lower * step; fractions[i] = p - lower;
            }
        }

        public void Apply(byte[] bgra, int width, int height, int stride, CancellationToken token)
        {
            if (width <= 0 || height <= 0 || stride < checked(width * 4) || bgra == null || bgra.Length < checked(stride * height))
                throw new ArgumentException("Invalid preview pixels.");
            if (strength == 0) return;
            if (height < 240) ApplyRows(bgra, width, stride, 0, height, token);
            else Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                part => ApplyRows(bgra, width, stride, part * height / 2, (part + 1) * height / 2, token));
        }

        unsafe void ApplyRows(byte[] bgra, int width, int stride, int firstRow, int endRow, CancellationToken token)
        {
            var gStep = size * 3; var bStep = size * size * 3;
            var endStep = 3 + gStep + bStep;
            var originalWeight = 1 - strength;
            fixed (byte* pixels = bgra)
            fixed (float* lut = values)
            for (var y = firstRow; y < endRow; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int p = y * stride, end = p + width * 4; p < end; p += 4)
                {
                    int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
                    var baseIndex = red[r] + green[g] + blue[b];
                    float x = rf[r], yy = gf[g], z = bf[b], a, c, d;
                    int first, second;
                    if (x >= yy)
                    {
                        if (yy >= z) { first = 3; second = 3 + gStep; a = x; c = yy; d = z; }
                        else if (x >= z) { first = 3; second = 3 + bStep; a = x; c = z; d = yy; }
                        else { first = bStep; second = bStep + 3; a = z; c = x; d = yy; }
                    }
                    else
                    {
                        if (x >= z) { first = gStep; second = gStep + 3; a = yy; c = x; d = z; }
                        else if (yy >= z) { first = gStep; second = gStep + bStep; a = yy; c = z; d = x; }
                        else { first = bStep; second = bStep + gStep; a = z; c = yy; d = x; }
                    }
                    var w0 = 1 - a; var w1 = a - c; var w2 = c - d;
                    float* v0 = lut + baseIndex, v1 = v0 + first, v2 = v0 + second, v3 = v0 + endStep;
                    pixels[p + 2] = ToByte(r * originalWeight + v0[0] * w0 + v1[0] * w1 + v2[0] * w2 + v3[0] * d);
                    pixels[p + 1] = ToByte(g * originalWeight + v0[1] * w0 + v1[1] * w1 + v2[1] * w2 + v3[1] * d);
                    pixels[p] = ToByte(b * originalWeight + v0[2] * w0 + v1[2] * w1 + v2[2] * w2 + v3[2] * d);
                }
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte ToByte(float value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + .5f);
    }
}
