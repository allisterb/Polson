namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// Synfig's noise layer: a gradient looked up by value noise over the whole plane — film grain, a mottled ground, smoke,
/// a texture that ties a composite of drawn shapes and generated pictures into one world.
/// </summary>
/// <remarks>
/// <b>Ported from Synfig</b> (<c>modules/mod_noise/noise.cpp</c>'s <c>TaskNoiseSW</c> and <c>random_noise.cpp</c>,
/// <c>synfig/quick_rng.h</c>, GPL-2-or-later). A value in −1…1 for each lattice cell, from the hash
/// <c>quick_rng((x+y)·21870 ^ (y+t)·11213 ^ (t+x)·36979 ^ (seed+salt)·31337)</c>, interpolated by <c>smooth</c>: nearest,
/// linear, cosine, spline (a cubic B-spline; without speed, Synfig's fast spline) or cubic (Catmull–Rom). <c>detail</c>
/// octaves, the first at <c>size / 2^detail</c> and each after at twice the scale, are summed with each running total halved
/// and clamped, then mapped to 0…1 (or taken as an absolute value when <c>turbulent</c>) and looked up in the gradient,
/// premultiplied as Synfig's <c>CompiledGradient</c> is. <c>speed</c> is in cycles per second: the lattice's third axis is
/// <c>speed × time</c>, so nearest grain at <c>speed = fps</c> is a new pattern every frame. Each pixel is evaluated at its
/// raster corner, as Synfig's paint task does. Synfig's super-sampling is not ported.
/// </remarks>
internal sealed class MotionNoiseLayer : MotionLayer
{
    #region Constructors
    public MotionNoiseLayer(MotionOptions o) : base(o)
    {
        stops = MotionGradientLayer.ReadStops(o.Raw("stops"), o.Who);
        lut = Lookup(stops);
        packed = [.. lut.Select(c => Pack(c.R, c.G, c.B, c.A))];
        seed = (int)o.Number("seed", 0);
        size = MotionBlurLayer.ReadSize(o, 60);
        var name = o.Has("smooth") ? o.Raw("smooth") as string ?? throw new ArgumentException("noise's smooth is a name: " + string.Join(", ", Smooths) + ".") : "cosine";
        smooth = Array.IndexOf(Smooths, name) is var i and >= 0 ? i
            : throw new ArgumentException($"noise has no smooth '{name}'. It takes: {string.Join(", ", Smooths)}.");
        detail = (int)o.Number("detail", 4);
        if (detail is < 1 or > 10) throw new ArgumentException($"noise's detail is the number of octaves, 1 to 10, not {detail}.");
        speed = o.Node("speed", MotionType.Real, 0d);
        turbulent = o.Bool("turbulent", false);
        alpha = o.Bool("alpha", false);
    }
    #endregion

    #region Fields
    /// <summary>Synfig's <c>SmoothType</c> names, by number; fast spline (5) is chosen for spline when nothing moves.</summary>
    private static readonly string[] Smooths = ["nearest", "linear", "cosine", "spline", "cubic"];
    private const int Spline = 3, Cubic = 4, FastSpline = 5;

    private readonly (double Pos, double[] Color)[] stops;
    private readonly (float R, float G, float B, float A)[] lut;

    /// <summary>The same table packed as pixels, so a pixel without alpha noise is one lookup.</summary>
    private readonly uint[] packed;
    private readonly int seed, smooth, detail;
    private readonly MotionNode size, speed;
    private readonly bool turbulent, alpha;
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() => [Amount, size, speed];

    protected override void Draw(SkiaCanvas surface, double time, double amount)
    {
        var canvas = surface.SkCanvas;
        if (!canvas.TotalMatrix.TryInvert(out var back)) return;
        var sz = size.Evaluate(time);
        var (sx, sy) = ((float)sz[0], (float)sz[1]);
        if (sx == 0 || sy == 0) return;
        var v = speed.Evaluate(time)[0];
        var mode = smooth == Spline && v == 0 ? FastSpline : smooth;
        var ftime = (float)(v * time);
        var (w, h) = (surface.Width, surface.Height);
        var scale = (float)(1 << detail);

        // The raster corner of each pixel in the layer's units, as Synfig's paint task steps them, by the inverse matrix in
        // managed arithmetic. Grain — nearest, one octave, no alpha noise — is one lattice cell a pixel, so it skips the
        // octave loop; the value is the general path's exactly. becool1 measured grain at ~65 ms a frame at 1920 × 1080 and
        // left it out of its film; most of that was unoptimised first-pass code (see Polson.CLI's TieredCompilation
        // settings), and the rest was this loop: 10 ms with the cell inline, against 17 through the general path.
        var grain = mode == 0 && detail == 1 && !alpha;
        var t0 = (int)MathF.Floor(ftime);
        var pixels = new uint[w * h];
        System.Threading.Tasks.Parallel.For(0, h, row =>
        {
            var line = pixels.AsSpan(row * w, w);
            for (var col = 0; col < w; col++)
            {
                float px = back.ScaleX * col + back.SkewX * row + back.TransX, py = back.SkewY * col + back.ScaleY * row + back.TransY;
                var wgt = back.Persp0 * col + back.Persp1 * row + back.Persp2;
                if (wgt != 1f) (px, py) = (px / wgt, py / wgt);
                if (!grain)
                {
                    line[col] = Pixel(px / sx * scale, py / sy * scale, ftime, mode);
                    continue;
                }
                // One octave of Pixel: the cell, clamped, folded or mapped to 0…1, then the packed colour.
                var value = Math.Clamp(Cell(5, (int)MathF.Floor(px / sx * scale), (int)MathF.Floor(py / sy * scale), t0), -1f, 1f);
                value = turbulent ? MathF.Abs(value) : value / 2 + 0.5f;
                line[col] = packed[(int)MathF.Round(Math.Clamp(value, 0, 1) * (packed.Length - 1))];
            }
        });

        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(bitmap.GetPixelSpan());
        bitmap.NotifyPixelsChanged();
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(amount * 255)) };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawBitmap(bitmap, 0, 0, paint);
        canvas.Restore();
    }

    /// <summary>
    /// TaskNoiseSW's octave loop for one point, as a premultiplied RGBA pixel: the gradient's colour from the packed table,
    /// or, with the alpha noise, that colour scaled by it.
    /// </summary>
    private uint Pixel(float x, float y, float t, int mode)
    {
        float value = 0, opacity = 0;
        for (var i = 0; i < detail; i++)
        {
            value = Math.Clamp(Noise(mode, (detail - i) * 5, x, y, t) + value * 0.5f, -1f, 1f);
            if (alpha) opacity = Math.Clamp(Noise(mode, 3 + (detail - i) * 5, x, y, t) + opacity * 0.5f, -1f, 1f);
            if (turbulent)
            {
                value = MathF.Abs(value);
                opacity = MathF.Abs(opacity);
            }
            x *= 0.5f;
            y *= 0.5f;
        }
        if (!turbulent)
        {
            value = value / 2 + 0.5f;
            opacity = opacity / 2 + 0.5f;
        }
        var index = (int)MathF.Round(Math.Clamp(value, 0, 1) * (lut.Length - 1));
        if (!alpha) return packed[index];
        var c = lut[index];
        return Pack(c.R * opacity, c.G * opacity, c.B * opacity, c.A * opacity);
    }

    /// <summary>A premultiplied colour as one Rgba8888 pixel: red in the lowest byte, alpha in the highest.</summary>
    private static uint Pack(float r, float g, float b, float a)
    {
        static uint B(float v) => (uint)MathF.Round(Math.Clamp(v, 0, 1) * 255);
        return B(r) | B(g) << 8 | B(b) << 16 | B(a) << 24;
    }

    /// <summary>Synfig's <c>RandomNoise(salt, x, y, t)</c>: one lattice cell's value, −1 to 1.</summary>
    private float Cell(int salt, int x, int y, int t)
    {
        unchecked
        {
            var next = ((uint)(x + y) * 21870u) ^ ((uint)(y + t) * 11213u) ^ ((uint)(t + x) * 36979u) ^ ((uint)(seed + salt) * 31337u);
            next = next * 1664525u + 1013904223u;
            return (next >> 16) / 65535f * 2f - 1f;
        }
    }

    /// <summary>Synfig's <c>RandomNoise(smooth, subseed, x, y, t)</c>, unlooped: the lattice interpolated by <paramref name="mode"/>.</summary>
    private float Noise(int mode, int salt, float xf, float yf, float tf)
    {
        var (x, y, t) = ((int)MathF.Floor(xf), (int)MathF.Floor(yf), (int)MathF.Floor(tf));
        var (tm1, t0, t1, t2) = (t - 1, t, t + 1, t + 2);
        switch (mode)
        {
            case Cubic:
            {
                // Catmull–Rom on each axis, which does not blur. On the stack: this runs per pixel per octave.
                Span<float> wx = stackalloc float[4], wy = stackalloc float[4], wt = stackalloc float[4];
                Weights(xf - x, wx);
                Weights(yf - y, wy);
                Weights(tf - t, wt);
                Span<int> ts = [tm1, t0, t1, t2];
                var sum = 0f;
                for (var i = 0; i < 4; i++)
                {
                    var rowSum = 0f;
                    for (var j = 0; j < 4; j++)
                    {
                        var cell = 0f;
                        for (var k = 0; k < 4; k++) cell += Cell(salt, x - 1 + j, y - 1 + i, ts[k]) * wt[k];
                        rowSum += cell * wx[j];
                    }
                    sum += rowSum * wy[i];
                }
                return sum;
            }
            case FastSpline:
            {
                var (a, b) = (xf - x, yf - y);
                var sum = 0f;
                for (var i = -1; i <= 2; i++)
                    for (var j = -1; j <= 2; j++)
                        sum += Cell(salt, i + x, j + y, 0) * (R(i - a) * R(b - j));
                return sum;
            }
            case Spline:
            {
                var (a, b, c) = (xf - x, yf - y, tf - t);
                Span<int> ts = [tm1, t0, t1, t2];
                var sum = 0f;
                for (var k = -1; k <= 2; k++)
                    for (var i = -1; i <= 2; i++)
                        for (var j = -1; j <= 2; j++)
                            sum += Cell(salt, i + x, j + y, ts[k + 1]) * (R(i - a) * R(b - j) * R(k - c));
                return sum;
            }
            case 2 or 1:
            {
                var (a, b) = (xf - x, yf - y);
                if (mode == 2)
                {
                    // Synfig's cosine eases the two space axes, never time, so motion stays smooth.
                    a = (float)((1.0f - Math.Cos(a * 3.1415927)) * 0.5f);
                    b = (float)((1.0f - Math.Cos(b * 3.1415927)) * 0.5f);
                }
                var (d, e) = (1f - a, 1f - b);
                if (t == tf)
                    return Cell(salt, x, y, t0) * (d * e) + Cell(salt, x + 1, y, t0) * (a * e)
                        + Cell(salt, x, y + 1, t0) * (d * b) + Cell(salt, x + 1, y + 1, t0) * (a * b);
                var (c, f) = (tf - t, 1f - (tf - t));
                return Cell(salt, x, y, t0) * (d * e * f) + Cell(salt, x + 1, y, t0) * (a * e * f)
                    + Cell(salt, x, y + 1, t0) * (d * b * f) + Cell(salt, x + 1, y + 1, t0) * (a * b * f)
                    + Cell(salt, x, y, t1) * (d * e * c) + Cell(salt, x + 1, y, t1) * (a * e * c)
                    + Cell(salt, x, y + 1, t1) * (d * b * c) + Cell(salt, x + 1, y + 1, t1) * (a * b * c);
            }
            default:
                return Cell(salt, x, y, t0);
        }

        static void Weights(float d, Span<float> into)
        {
            into[0] = 0.5f * d * (d * (d * -1f + 2f) - 1f);
            into[1] = 0.5f * (d * (d * (3f * d - 5f)) + 2f);
            into[2] = 0.5f * d * (d * (-3f * d + 4f) + 1f);
            into[3] = 0.5f * d * d * (d - 1f);
        }

        // The cubic B-spline kernel of Synfig's spline modes.
        static float P(float v) => v > 0 ? v * v * v : 0f;
        static float R(float v) => (P(v + 2) - 4f * P(v + 1) + 6f * P(v) - 4f * P(v - 1)) * (1f / 6f);
    }

    /// <summary>The gradient at 4096 places from 0 to 1, premultiplied and interpolated premultiplied, clamped at its ends.</summary>
    private static (float R, float G, float B, float A)[] Lookup((double Pos, double[] Color)[] stops)
    {
        var table = new (float, float, float, float)[4096];
        for (var i = 0; i < table.Length; i++)
        {
            var x = i / (double)(table.Length - 1);
            var after = Array.FindIndex(stops, s => s.Pos >= x);
            double[] c;
            if (after <= 0) c = Premultiplied(stops[after < 0 ? ^1 : 0].Color);
            else
            {
                var (p0, c0) = stops[after - 1];
                var (p1, c1) = stops[after];
                var f = p1 > p0 ? (x - p0) / (p1 - p0) : 1;
                var (a, b) = (Premultiplied(c0), Premultiplied(c1));
                c = [.. a.Zip(b, (u, w) => u + (w - u) * f)];
            }
            table[i] = ((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
        }
        return table;

        static double[] Premultiplied(double[] c) => [c[0] * c[3], c[1] * c[3], c[2] * c[3], c[3]];
    }

    public override XElement ToSif(SifWriter sif) => Element("noise", "0.1", sif,
        MotionGradientLayer.SifGradient(stops),
        SifWriter.Param("seed", new XElement("integer", new XAttribute("value", seed))),
        sif.Param("size", size),
        SifWriter.Param("smooth", new XElement("integer", new XAttribute("value", smooth))),
        SifWriter.Param("detail", new XElement("integer", new XAttribute("value", detail))),
        sif.Param("speed", speed),
        SifWriter.Param("turbulent", SifWriter.Bool(turbulent)),
        SifWriter.Param("do_alpha", SifWriter.Bool(alpha)),
        SifWriter.Param("super_sample", SifWriter.Bool(false)));

    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map) =>
        throw new InvalidOperationException($"The noise{(Desc is null ? "" : $" '{Desc}'")} cannot be written as animated SVG. "
            + "Write a .sif, or capture the composition as frames.");
    #endregion
}
