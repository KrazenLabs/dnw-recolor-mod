using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Recolor
{
    public struct ColorAdjustParams : IEquatable<ColorAdjustParams>
    {
        public float Hue;
        public float Saturation;
        public float Brightness;

        public static readonly ColorAdjustParams Identity = new ColorAdjustParams { Hue = 0f, Saturation = 1f, Brightness = 1f };

        public ColorAdjustParams(float hue, float saturation, float brightness)
        {
            Hue = hue;
            Saturation = saturation;
            Brightness = brightness;
        }

        public bool IsIdentity
        {
            get { return Mathf.Abs(Mathf.DeltaAngle(0f, Hue)) < 0.05f && Mathf.Abs(Saturation - 1f) < 0.002f && Mathf.Abs(Brightness - 1f) < 0.002f; }
        }

        public ColorAdjustParams Clamped()
        {
            return new ColorAdjustParams(Mathf.Repeat(Hue + 180f, 360f) - 180f, Mathf.Clamp(Saturation, 0f, 3f), Mathf.Clamp(Brightness, 0f, 3f));
        }

        public bool Equals(ColorAdjustParams other)
        {
            return Mathf.Abs(Hue - other.Hue) < 0.001f && Mathf.Abs(Saturation - other.Saturation) < 0.0001f && Mathf.Abs(Brightness - other.Brightness) < 0.0001f;
        }

        public override bool Equals(object obj) { return obj is ColorAdjustParams p && Equals(p); }
        public override int GetHashCode() { return Hue.GetHashCode() ^ (Saturation.GetHashCode() << 8) ^ (Brightness.GetHashCode() << 16); }
        public override string ToString() { return "hue " + Hue.ToString("0") + "°, sat " + Saturation.ToString("0.00") + ", bright " + Brightness.ToString("0.00"); }
    }

    // A 3x3 RGB matrix that applies hue rotation
    public sealed class ColorMatrix
    {
        private readonly float[] _m = new float[9];
        private readonly int[] _fixed = new int[9];

        public ColorMatrix(ColorAdjustParams p)
        {
            // Hue rotation around (1,1,1)
            float a = p.Hue * Mathf.Deg2Rad;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            float k = (1f - c) / 3f;
            float t = Mathf.Sqrt(1f / 3f) * s;
            float[] h =
            {
                c + k, k - t, k + t,
                k + t, c + k, k - t,
                k - t, k + t, c + k,
            };

            // Saturation: luma lerp
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float sat = p.Saturation, inv = 1f - sat;
            float[] sm =
            {
                inv * lr + sat, inv * lg, inv * lb,
                inv * lr, inv * lg + sat, inv * lb,
                inv * lr, inv * lg, inv * lb + sat,
            };

            // Brightness
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    float sum = 0f;
                    for (int q = 0; q < 3; q++) sum += sm[i * 3 + q] * h[q * 3 + j];
                    _m[i * 3 + j] = sum * p.Brightness;
                }

            for (int i = 0; i < 9; i++) _fixed[i] = Mathf.RoundToInt(_m[i] * 4096f);
        }

        // Pixels per chunk when a large texture is split
        private const int ParallelChunk = 1 << 17;

        public void Apply(Color32[] source, Color32[] destination)
        {
            if (source == null || destination == null || destination.Length < source.Length) throw new ArgumentException("destination too small");
            int length = source.Length;
            if (length <= ParallelChunk || Environment.ProcessorCount <= 1)
            {
                ApplyRange(source, destination, 0, length);
                return;
            }
            int chunks = (length + ParallelChunk - 1) / ParallelChunk;
            Parallel.For(0, chunks, chunk => ApplyRange(source, destination, chunk * ParallelChunk, Math.Min(length, (chunk + 1) * ParallelChunk)));
        }

        private void ApplyRange(Color32[] source, Color32[] destination, int start, int end)
        {
            int m0 = _fixed[0], m1 = _fixed[1], m2 = _fixed[2];
            int m3 = _fixed[3], m4 = _fixed[4], m5 = _fixed[5];
            int m6 = _fixed[6], m7 = _fixed[7], m8 = _fixed[8];
            for (int i = start; i < end; i++)
            {
                Color32 c = source[i];
                int r = c.r, g = c.g, b = c.b;
                int nr = (m0 * r + m1 * g + m2 * b + 2048) >> 12;
                int ng = (m3 * r + m4 * g + m5 * b + 2048) >> 12;
                int nb = (m6 * r + m7 * g + m8 * b + 2048) >> 12;
                if (nr < 0) nr = 0; else if (nr > 255) nr = 255;
                if (ng < 0) ng = 0; else if (ng > 255) ng = 255;
                if (nb < 0) nb = 0; else if (nb > 255) nb = 255;
                destination[i] = new Color32((byte)nr, (byte)ng, (byte)nb, c.a);
            }
        }

        public Color32 Apply(Color32 c)
        {
            int r = c.r, g = c.g, b = c.b;
            int nr = Mathf.Clamp((_fixed[0] * r + _fixed[1] * g + _fixed[2] * b + 2048) >> 12, 0, 255);
            int ng = Mathf.Clamp((_fixed[3] * r + _fixed[4] * g + _fixed[5] * b + 2048) >> 12, 0, 255);
            int nb = Mathf.Clamp((_fixed[6] * r + _fixed[7] * g + _fixed[8] * b + 2048) >> 12, 0, 255);
            return new Color32((byte)nr, (byte)ng, (byte)nb, c.a);
        }

        public Color Apply(Color c)
        {
            return new Color(
                Mathf.Clamp01(_m[0] * c.r + _m[1] * c.g + _m[2] * c.b),
                Mathf.Clamp01(_m[3] * c.r + _m[4] * c.g + _m[5] * c.b),
                Mathf.Clamp01(_m[6] * c.r + _m[7] * c.g + _m[8] * c.b),
                c.a);
        }

        // Applies the matrix to float RGB triplets in place (used for Float32 vertex color streams)
        public void Apply(ref float r, ref float g, ref float b)
        {
            float nr = _m[0] * r + _m[1] * g + _m[2] * b;
            float ng = _m[3] * r + _m[4] * g + _m[5] * b;
            float nb = _m[6] * r + _m[7] * g + _m[8] * b;
            r = Mathf.Clamp01(nr); g = Mathf.Clamp01(ng); b = Mathf.Clamp01(nb);
        }
    }
}
