namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// Synfig's blur layer: everything below it in its stack, blurred.
/// </summary>
/// <remarks>
/// <b>Ported from Synfig</b> (<c>modules/mod_filter/blur.cpp</c>, <c>rendering/software/function/blur.cpp</c> and
/// <c>blurtemplates.h</c>, GPL-2-or-later; Ivan Mahonin 2015). <c>size</c> is in units and becomes pixels by the
/// render's scale; each type multiplies it by its own amplifier, and the stack is padded by Synfig's extra size so a
/// blur near the frame's edge draws in what lies beyond it, as Synfig's does.
/// <list type="bullet">
/// <item><b>gaussian</b>: a sampled Gaussian of σ = ½ + 3⁄8·size px, row then column — Synfig's pattern path. Past an
/// extra size of 32 px Synfig switches to an FFT of the same kernel; this takes Skia's Gaussian at that σ instead.</item>
/// <item><b>fastGaussian</b>: one box pass a row and a column, radius round(3⁄8 · 1.662 · size). Synfig means two —
/// <c>bool count = 1; … count = 2;</c> leaves the count <c>true</c>, which is one — and one is what it draws.</item>
/// <item><b>box</b>: one box pass a row and a column, radius round(¼√π · size).</item>
/// <item><b>cross</b>: the box's row pass and its column pass, each on half the stack, summed: a "+" kernel.</item>
/// </list>
/// <b>disc</b> is not ported: its 2-D kernel needs Synfig's FFT path to be usable at motion sizes.
/// <para>The result replaces the stack below at full <c>amount</c> (Synfig's straight blend) and fades to it at less.</para>
/// </remarks>
internal sealed class MotionBlurLayer : MotionLayer, IMotionStackFilter
{
    #region Constructors
    public MotionBlurLayer(MotionOptions o) : base(o)
    {
        if (!Blend.IsComposite)
            throw new ArgumentException($"{o.Who} replaces the stack below it with its blur, as Synfig's straight blend does; it takes amount, not blend.");

        size = ReadSize(o);
        type = o.Has("type") ? ParseType(o.Raw("type")?.ToString(), o.Who) : BlurType.FastGaussian;
    }

    /// <summary>
    /// A blur's <c>size</c>: a number is both axes, and so is a keyed number — a focus pull is keyed as one value, as an
    /// agent writes it first — linked into both of a composite so it stays one value in .sif too. Anything else is a vector.
    /// </summary>
    internal static MotionNode ReadSize(MotionOptions o) => o.Raw("size") switch
    {
        IConvertible n and not string => MotionNodeFactory.Node(new[] { Convert.ToDouble(n), Convert.ToDouble(n) }, MotionType.Vector, $"{o.Who}'s size"),
        MotionNode { Kind: MotionType.Real } r => new MotionComposite(r, r),
        // Synfig's default is 0.1 of its units, which are 60 px by default: 6 px. Ours are the view's, pixels unless the
        // composition says otherwise, where 0.1 is no blur at all (trailer1 found the docs' example invisible).
        _ => o.Node("size", MotionType.Vector, new[] { 6d, 6d }),
    };
    #endregion

    #region Types
    /// <summary>Synfig's numbering, which is what <c>.sif</c> stores.</summary>
    internal enum BlurType { Box = 0, FastGaussian = 1, Cross = 2, Gaussian = 3, Disc = 4 }
    #endregion

    #region Fields
    private readonly MotionNode size;
    private readonly BlurType type;
    #endregion

    #region Properties
    public BlurType Type => type;
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() => [Amount, size];

    /// <summary>Synfig's straight blend, which is what replacing the stack below is.</summary>
    protected override int SifBlendMethod => 1;

    protected override void Draw(SkiaCanvas surface, double time, double amount) =>
        throw new InvalidOperationException("A blur is applied by its stack, to what is below it.");

    public override XElement ToSif(SifWriter sif) => Element("blur", "0.3", sif,
        sif.Param("size", size),
        SifWriter.Param("type", new XElement("integer", new XAttribute("value", (int)type))));

    /// <summary>
    /// A marker the stack wraps everything before it in: <c>&lt;g filter&gt;</c> over an <c>feGaussianBlur</c>, its
    /// σ in units tracked from <c>size</c>. Gaussian types only, and only at full amount — SVG has no straight blend.
    /// </summary>
    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
    {
        if (type is not (BlurType.Gaussian or BlurType.FastGaussian))
            throw new InvalidOperationException(
                $"The blur{(Desc is null ? "" : $" '{Desc}'")} is a {Name(type)} blur, which SVG cannot draw: SVG blurs are "
                + "Gaussian. Use type 'gaussian' or 'fastGaussian', or capture the composition as frames.");

        var amount = svg.Track(Amount, 0, map);
        if (!amount.IsStatic || Math.Abs(amount.Values[0] - 1) > 1e-12)
            throw new InvalidOperationException(
                $"The blur{(Desc is null ? "" : $" '{Desc}'")} has an amount other than 1, a straight blend of the blurred "
                + "stack with the sharp one, which SVG cannot say. Animate its size instead, or capture the composition as frames.");

        var blur = new XElement(SvgAnimationWriter.Svg + "feGaussianBlur");
        svg.Attribute(blur, "stdDeviation", t =>
        {
            var s = size.Evaluate(map.Local(t));
            return $"{SvgAnimationWriter.N(Math.Abs(s[0]) * GaussAmplifier)} {SvgAnimationWriter.N(Math.Abs(s[1]) * GaussAmplifier)}";
        });
        return new XElement(SvgAnimationWriter.Svg + "g", new XAttribute(SvgAnimationWriter.BelowFilter, svg.Filter(blur)));
    }

    /// <summary>A blur at no amount, or of no size, changes nothing — the stack is then drawn in place.</summary>
    public bool IsIdle(SKMatrix matrix, double time)
    {
        if (AmountAt(time) <= 0) return true;
        var (sx, sy) = Pixels(size, matrix, time);
        return sx <= 1e-10 && sy <= 1e-10;
    }

    /// <summary>Synfig's extra size: how far beyond what is kept the blur reads, per axis, in device pixels.</summary>
    public (int X, int Y) Margin(SKMatrix matrix, double time)
    {
        var (sx, sy) = Pixels(size, matrix, time);
        return MarginFor(type, sx, sy);
    }

    /// <summary>The extra size of a blur of <paramref name="type"/> and these device-pixel sizes.</summary>
    internal static (int X, int Y) MarginFor(BlurType type, double sx, double sy)
    {
        var k = ExtraFactor(type);
        return (Extra(sx * k), Extra(sy * k));

        static int Extra(double s) => (int)Math.Ceiling(Math.Abs(s) + 1 - 1e-10);
    }

    public SkiaCanvas Apply(SkiaCanvas below, SKMatrix matrix, double time)
    {
        var amount = AmountAt(time);
        var result = new SkiaCanvas(below.Width, below.Height, below.SkBitmap.ColorType);
        result.SkCanvas.Clear(SKColors.Transparent);
        var (sx, sy) = Pixels(size, matrix, time);
        if (amount <= 0 || (sx <= 1e-10 && sy <= 1e-10))
        {
            result.SkCanvas.DrawBitmap(below.SkBitmap, 0, 0);
            return result;
        }

        using var blurred = Blurred(below.SkBitmap, type, sx, sy, Margin(matrix, time));

        // Straight blend on premultiplied colour: (blurred − below)·amount + below.
        if (amount >= 1)
        {
            result.SkCanvas.DrawBitmap(blurred, 0, 0);
            return result;
        }
        using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round((1 - amount) * 255)) };
        result.SkCanvas.DrawBitmap(below.SkBitmap, 0, 0, fade);
        using var add = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(amount * 255)), BlendMode = SKBlendMode.Plus };
        result.SkCanvas.DrawBitmap(blurred, 0, 0, add);
        return result;
    }

    /// <summary><c>size</c> in device pixels along each axis: Synfig's <c>size.multiply_coords(ppu)</c>.</summary>
    /// <summary>
    /// One channel blurred as <see cref="Blurred"/> blurs four: a shadow is one colour, so only its coverage needs it, at a
    /// quarter of the work. Wide Gaussians take Skia's Gaussian at the same σ, as the four-channel path does.
    /// </summary>
    internal static float[] BlurredAlpha(float[] alpha, int w, int h, BlurType type, double sizeX, double sizeY, (int X, int Y) extra)
    {
        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        if (type == BlurType.Gaussian && (extra.X >= 32 || extra.Y >= 32))
        {
            using var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.AlphaF16, SKAlphaType.Premul));
            using var source = new SKBitmap(new SKImageInfo(w, h, SKColorType.AlphaF16, SKAlphaType.Premul));
            var span = MemoryMarshal.Cast<byte, Half>(source.GetPixelSpan());
            for (var i = 0; i < alpha.Length; i++) span[i] = (Half)alpha[i];
            source.NotifyPixelsChanged();
            using (var canvas = new SKCanvas(mask))
            {
                canvas.Clear(SKColors.Transparent);
                using var filter = SKImageFilter.CreateBlur((float)(0.5 + GaussAmplifier * sizeX), (float)(0.5 + GaussAmplifier * sizeY), SKShaderTileMode.Decal);
                using var paint = new SKPaint { ImageFilter = filter };
                canvas.DrawBitmap(source, 0, 0, paint);
            }
            var result = MemoryMarshal.Cast<byte, Half>(mask.GetPixelSpan());
            var output = new float[alpha.Length];
            for (var i = 0; i < output.Length; i++) output[i] = (float)result[i];
            return output;
        }

        return type switch
        {
            BlurType.Gaussian => SynfigBlur.Gaussian(alpha, w, h, GaussAmplifier * sizeX, GaussAmplifier * sizeY, extra, 1),
            BlurType.FastGaussian => SynfigBlur.Box(alpha, w, h, Round(GaussAmplifier * FastGaussStretch * sizeX), Round(GaussAmplifier * FastGaussStretch * sizeY), false, 1),
            BlurType.Box => SynfigBlur.Box(alpha, w, h, Round(BoxAmplifier * sizeX), Round(BoxAmplifier * sizeY), false, 1),
            BlurType.Cross => SynfigBlur.Box(alpha, w, h, Round(sizeX), Round(sizeY), true, 1),
            _ => throw new InvalidOperationException($"A {Name(type)} blur is not ported."),
        };
    }

    internal static (double X, double Y) Pixels(MotionNode size, SKMatrix matrix, double time)
    {
        var s = size.Evaluate(time);
        var ppuX = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        var ppuY = Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY);
        return (Math.Max(0, s[0]) * ppuX, Math.Max(0, s[1]) * ppuY);
    }

    /// <summary>The stack blurred, as Synfig's software renderer does it, on premultiplied float colour.</summary>
    internal static SKBitmap Blurred(SKBitmap source, BlurType type, double sizeX, double sizeY, (int X, int Y) extra)
    {
        int w = source.Width, h = source.Height;
        var info = new SKImageInfo(w, h, SKColorType.RgbaF32, SKAlphaType.Premul);

        if (type == BlurType.Gaussian && (extra.X >= 32 || extra.Y >= 32))
        {
            // Synfig's FFT path for a wide Gaussian: the same kernel, so Skia's Gaussian at the same σ.
            var sigmaX = (float)(0.5 + GaussAmplifier * sizeX);
            var sigmaY = (float)(0.5 + GaussAmplifier * sizeY);
            var wide = new SKBitmap(info);
            using var canvas = new SKCanvas(wide);
            canvas.Clear(SKColors.Transparent);
            using var filter = SKImageFilter.CreateBlur(sigmaX, sigmaY, SKShaderTileMode.Decal);
            using var paint = new SKPaint { ImageFilter = filter };
            canvas.DrawBitmap(source, 0, 0, paint);
            return wide;
        }

        var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
        }
        var pixels = MemoryMarshal.Cast<byte, float>(bitmap.GetPixelSpan()).ToArray();

        var output = type switch
        {
            BlurType.Gaussian => SynfigBlur.Gaussian(pixels, w, h, GaussAmplifier * sizeX, GaussAmplifier * sizeY, extra),
            BlurType.FastGaussian => SynfigBlur.Box(pixels, w, h, Round(GaussAmplifier * FastGaussStretch * sizeX), Round(GaussAmplifier * FastGaussStretch * sizeY), cross: false),
            BlurType.Box => SynfigBlur.Box(pixels, w, h, Round(BoxAmplifier * sizeX), Round(BoxAmplifier * sizeY), cross: false),
            BlurType.Cross => SynfigBlur.Box(pixels, w, h, Round(sizeX), Round(sizeY), cross: true),
            _ => throw new InvalidOperationException($"A {Name(type)} blur is not ported."),
        };

        MemoryMarshal.Cast<float, byte>(output.AsSpan()).CopyTo(bitmap.GetPixelSpan());
        bitmap.NotifyPixelsChanged();
        return bitmap;

        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }

    internal static BlurType ParseType(string? name, string who) => name?.Trim().ToLowerInvariant() switch
    {
        "box" => BlurType.Box,
        "fastgaussian" => BlurType.FastGaussian,
        "cross" => BlurType.Cross,
        "gaussian" => BlurType.Gaussian,
        "disc" => throw new ArgumentException(
            $"{who}'s type 'disc' is not ported yet: its 2-D kernel needs Synfig's FFT path to be usable at motion sizes. "
            + "Use 'gaussian' for a soft focus, or 'box' for a harder one."),
        _ => throw new ArgumentException($"{who}'s type is 'gaussian', 'fastGaussian', 'box' or 'cross'; '{name}' is not."),
    };

    internal static string Name(BlurType t) => t switch
    {
        BlurType.FastGaussian => "fastGaussian",
        _ => t.ToString().ToLowerInvariant(),
    };

    /// <summary>Synfig's <c>get_extra_size(type)</c>: the blur's reach per unit of size.</summary>
    private static double ExtraFactor(BlurType t) => t switch
    {
        BlurType.Box => BoxAmplifier,
        BlurType.Cross => 1,
        BlurType.Disc => 0.5,
        BlurType.FastGaussian => Ungauss(1.0 / 128) * GaussAmplifier,
        _ => Ungauss(1.0 / 4096) * GaussAmplifier,
    };

    /// <summary>Synfig's <c>ungauss</c>: the distance at which a unit Gaussian falls to <paramref name="x"/>.</summary>
    private static double Ungauss(double x) => Math.Sqrt(-2 * Math.Log(x * Math.Sqrt(2 * Math.PI)));
    #endregion

    #region Fields (static)
    internal const double GaussAmplifier = 3.0 / 8.0;
    private const double FastGaussStretch = 1.662155813;
    private static readonly double BoxAmplifier = 0.25 * Math.Sqrt(Math.PI);
    #endregion
}

/// <summary>Synfig's software blur kernels on interleaved premultiplied RGBA floats, row by row then column by column.</summary>
internal static class SynfigBlur
{
    #region Methods
    /// <summary>
    /// <c>blur_pattern</c> with <c>fill_pattern_gauss</c>: a half kernel of σ = ½ + <paramref name="ax"/> taps out to the
    /// extra size, normalised over the whole, run along rows into a fresh surface and then along its columns.
    /// Outside the extra size from any edge the result is left empty, as Synfig's is.
    /// </summary>
    /// <remarks><paramref name="channels"/> per pixel, interleaved: 4 for premultiplied RGBA, 1 for a shadow's alpha alone.</remarks>
    public static float[] Gaussian(float[] src, int w, int h, double ax, double ay, (int X, int Y) extra, int channels = 4)
    {
        var rowPattern = Pattern(extra.X + 1, 0.5 + Math.Abs(ax));
        var colPattern = Pattern(extra.Y + 1, 0.5 + Math.Abs(ay));
        var n = channels;

        var rows = new float[src.Length];
        Parallel.For(0, h, y => Convolve(src, rows, y * w * n, n, w, rowPattern, n));
        var cols = new float[src.Length];
        Parallel.For(0, w, x => Convolve(rows, cols, x * n, w * n, h, colPattern, n));
        return cols;
    }

    /// <summary>
    /// <c>blur_box</c>: one <c>blur_box_discrete</c> pass of radius <paramref name="rx"/> along rows and one of
    /// <paramref name="ry"/> along columns, in place. <paramref name="cross"/> halves the stack, runs the row pass on one
    /// copy and the column pass on the other, and sums them.
    /// </summary>
    public static float[] Box(float[] src, int w, int h, int rx, int ry, bool cross, int channels = 4)
    {
        var n = channels;
        var surface = (float[])src.Clone();
        if (!cross)
        {
            Parallel.For(0, h, y => BoxLine(surface, y * w * n, n, w, rx, n));
            Parallel.For(0, w, x => BoxLine(surface, x * n, w * n, h, ry, n));
            return surface;
        }

        for (var i = 0; i < surface.Length; i++) surface[i] *= 0.5f;
        var copy = (float[])surface.Clone();
        Parallel.For(0, h, y => BoxLine(surface, y * w * n, n, w, rx, n));
        Parallel.For(0, w, x => BoxLine(copy, x * n, w * n, h, ry, n));
        for (var i = 0; i < surface.Length; i++) surface[i] += copy[i];
        return surface;
    }

    /// <summary><c>fill_pattern_gauss</c> then <c>normalize_half_pattern</c>.</summary>
    private static double[] Pattern(int count, double s)
    {
        var p = new double[count];
        var k = 1 / Math.Sqrt(2 * Math.PI);
        for (var i = 0; i < count; i++)
            p[i] = s < 1e-10 ? (i == 0 ? 1 : 0) : k * Math.Exp(-0.5 * i * i / (s * s)) / s;

        var sum = p[0] + 2 * p.Skip(1).Sum();
        if (sum > 1e-10) for (var i = 0; i < count; i++) p[i] /= sum;
        return p;
    }

    /// <summary><c>blur_pattern</c> along one line of <paramref name="count"/> pixels, every channel.</summary>
    private static void Convolve(float[] src, float[] dst, int start, int stride, int count, double[] pattern, int channels)
    {
        var size = pattern.Length - 1;
        for (var i = size; i < count - size; i++)
        {
            var at = start + i * stride;
            for (var c = 0; c < channels; c++)
            {
                var v = src[at + c] * pattern[0];
                for (var k = 1; k <= size; k++) v += (src[at - k * stride + c] + src[at + k * stride + c]) * pattern[k];
                dst[at + c] = (float)v;
            }
        }
    }

    /// <summary>
    /// <c>blur_box_discrete</c> in place: each pixel from <paramref name="radius"/> to <c>count − radius − 1</c> becomes
    /// the mean of the window around it, read before it was overwritten; the ends are left as they were.
    /// </summary>
    private static void BoxLine(float[] x, int start, int stride, int count, int radius, int channels)
    {
        var s = Math.Abs(radius);
        var full = 1 + 2 * s;
        if (s == 0 || count < full) return;
        var weight = 1.0 / full;
        var n = channels;

        Span<double> sum = stackalloc double[n];
        var window = new float[full * n];
        for (var i = 0; i < full; i++)
        {
            for (var c = 0; c < n; c++)
            {
                var v = x[start + i * stride + c];
                window[i * n + c] = v;
                sum[c] += v;
            }
        }

        var head = 0;
        for (int i = full, j = s; i < count; i++, j++)
        {
            for (var c = 0; c < n; c++)
            {
                x[start + j * stride + c] = (float)(weight * sum[c]);
                var incoming = x[start + i * stride + c];
                sum[c] += incoming - window[head * n + c];
                window[head * n + c] = incoming;
            }
            head = (head + 1) % full;
        }
    }
    #endregion
}
