namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// Synfig's colour correct layer: everything below it in its stack with its hue turned, its exposure, brightness and
/// contrast changed, its saturation scaled and its gamma bent.
/// </summary>
/// <remarks>
/// <b>Ported from Synfig</b> (<c>modules/mod_filter/colorcorrect.cpp</c>, <c>synfig/color/colormatrix.cpp</c>,
/// <c>rendering/software/task/taskpixelgammasw.cpp</c>, GPL-2-or-later), on straight colour as Synfig's surfaces hold it,
/// as <b>Synfig 1.5.5 applies them</b>:
/// <list type="number">
/// <item><b>saturation</b>: each channel pulled towards the brightest, <c>max − (max − c)·s</c>, in linear light (the canvas
/// gamma is 1 here, so on the colour as it is);</item>
/// <item><b>gamma</b>: <c>c^(1/γ)</c>, a negative channel as <c>−(−c)^(1/γ)</c>, <b>not clamped</b>;</item>
/// <item>one colour matrix, applied to <c>[r g b a 1]</c> as a row: <b>hue</b> (U and V turned in YUV) · <b>exposure</b>
/// (× eˣ) · <b>brightness</b> (+x) · <b>contrast</b> (about ½).</item>
/// </list>
/// On 8-bit colour the three are one managed pass when saturation or gamma is not its default, and Skia's colour filter
/// alone when neither is (<see cref="Apply"/>); on float colour, saturation and gamma are a pass of their own and the
/// matrix a Skia colour filter, which works on straight colour and keeps values outside [0, 1] through it.
/// <para>
/// <b>Measured, not read.</b> Synfig's current source clamps gamma's result to [0, 1]; the 1.5.5 binary does not, so a
/// channel saturation drove below 0 reaches the matrix still negative. Every pair of parameters matched either way; all six
/// together differed on 42% of pixels until gamma stopped clamping. A Synfig built from master will clamp there.
/// </para>
/// </remarks>
/// <summary>
/// A grade on its own, as <c>Motion.grade(options)</c> returns it: what a <c>comp.colorCorrect</c> with the same options
/// makes of one colour, so a check on the graded colours is one call rather than a rendered probe.
/// </summary>
/// <remarks>
/// trailer4 rendered a 4 × 4 composition with the same fill and grade and read a pixel back, which is how it caught its
/// grade turning the poster's magenta crimson.
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase form is the one documented in
/// <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public sealed class MotionGrade
{
    #region Constructors
    internal MotionGrade(MotionColorCorrectLayer layer) => this.layer = layer;
    #endregion

    #region Fields
    private readonly MotionColorCorrectLayer layer;
    #endregion

    #region Methods
    /// <summary>
    /// What the grade makes of a CSS colour at <paramref name="time"/> seconds (keyed options are read there), as
    /// <c>#RRGGBBAA</c> — the form <c>node.at</c> gives a colour, and one <c>Motion.contrast</c> reads.
    /// </summary>
    public string Apply(string color, double time = 0) =>
        (string)MotionTypes.ToJs(MotionType.Color, layer.Correct(MotionTypes.Read(color, MotionType.Color, "grade.apply's colour"), time));
    #endregion
}

internal sealed class MotionColorCorrectLayer(MotionOptions o) : MotionLayer(o), IMotionStackFilter
{
    #region Fields
    private readonly MotionNode hue = o.Node("hue", MotionType.Angle, 0d);
    private readonly MotionNode brightness = o.Node("brightness", MotionType.Real, 0d);
    private readonly MotionNode contrast = o.Node("contrast", MotionType.Real, 1d);
    private readonly MotionNode exposure = o.Node("exposure", MotionType.Real, 0d);
    private readonly MotionNode saturation = o.Node("saturation", MotionType.Real, 1d);
    private readonly MotionNode gamma = o.Node("gamma", MotionType.Real, 1d);
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() => [Amount, hue, brightness, contrast, exposure, saturation, gamma];

    protected override void Draw(SkiaCanvas surface, double time, double amount) =>
        throw new InvalidOperationException("A colour correction is applied by its stack, to what is below it.");

    /// <summary>At its defaults it changes nothing, and the stack is drawn in place.</summary>
    public bool IsIdle(SKMatrix matrix, double time) =>
        hue.Evaluate(time)[0] == 0 && brightness.Evaluate(time)[0] == 0 && contrast.Evaluate(time)[0] == 1
        && exposure.Evaluate(time)[0] == 0 && saturation.Evaluate(time)[0] == 1 && gamma.Evaluate(time)[0] == 1;

    /// <remarks>
    /// On 8-bit colour, as a film's frames are, the work is split into row bands across the cores: the matrix alone is Skia's
    /// filter band by band (pixel-identical to one draw, about 4× faster), and with saturation or gamma the whole correction
    /// is one managed pass (<see cref="Correct8"/>). Measured at 1280 × 720 on eight cores, the matrix went from 15 ms a
    /// frame to about 5, saturation from 47 and gamma from 57 to about 10. Any other format takes the float path.
    /// </remarks>
    public SkiaCanvas Apply(SkiaCanvas below, SKMatrix matrix, double time)
    {
        var (s, g) = (saturation.Evaluate(time)[0], gamma.Evaluate(time)[0]);
        var result = new SkiaCanvas(below.Width, below.Height, below.SkBitmap.ColorType);
        if (below.SkBitmap.ColorType == SKColorType.Rgba8888 && below.SkBitmap.AlphaType == SKAlphaType.Premul)
            Correct8(below.SkBitmap, result.SkBitmap, Matrix(time), (float)s, (float)(1 / g));
        else
            CorrectFloat(below.SkBitmap, result.SkCanvas, Matrix(time), (float)s, (float)(1 / g));
        return result;
    }

    /// <summary>
    /// The correction on any format: saturation and gamma a float pass of their own, taken only when either is not its
    /// default, then the matrix as a Skia colour filter drawn onto <paramref name="target"/>.
    /// </summary>
    internal static void CorrectFloat(SKBitmap source, SKCanvas target, double[,] m, float s, float power)
    {
        using var bent = s != 1 || power != 1 ? SaturateAndBend(source, s, power) : null;
        using var filter = ColourMatrix(m);
        using var paint = new SKPaint { ColorFilter = filter };
        target.DrawBitmap(bent ?? source, 0, 0, paint);
    }

    /// <summary>The bands a frame is split into: one per core, at least 32 rows each.</summary>
    private static int Bands(int height) => Math.Clamp(Math.Min(Environment.ProcessorCount, height / 32), 1, 64);

    /// <summary><paramref name="filter"/> drawn from <paramref name="source"/> into <paramref name="target"/>, band by band in parallel.</summary>
    private static void Banded(SKBitmap source, SKBitmap target, SKColorFilter filter)
    {
        var (w, h) = (source.Width, source.Height);
        using var pixels = source.PeekPixels();
        using var image = SKImage.FromPixels(pixels);
        var bands = Bands(h);
        var step = (h + bands - 1) / bands;
        System.Threading.Tasks.Parallel.For(0, bands, i =>
        {
            var (y0, y1) = (i * step, Math.Min(h, (i + 1) * step));
            if (y1 <= y0) return;
            using var band = new SKBitmap();
            band.InstallPixels(new SKImageInfo(w, y1 - y0, target.ColorType, target.AlphaType), target.GetPixels() + y0 * target.RowBytes, target.RowBytes);
            using var canvas = new SKCanvas(band);
            using var paint = new SKPaint { ColorFilter = filter, BlendMode = SKBlendMode.Src };
            canvas.DrawImage(image, SKRect.Create(0, y0, w, y1 - y0), SKRect.Create(0, 0, w, y1 - y0), paint);
        });
    }

    /// <summary>
    /// The whole correction on premultiplied 8-bit colour in one pass, band by band: unpremultiplied, saturated, bent, through
    /// the matrix <paramref name="m"/>, clamped and premultiplied again — Skia's matrix filter to within one level.
    /// </summary>
    /// <remarks>
    /// An opaque pixel's channel after saturation and gamma depends only on that channel and the pixel's brightest, both one
    /// of 256 levels, so those come from a 256 × 256 table made once a frame instead of three powers a pixel. Pixels that are
    /// not opaque — edges, translucent shapes — are worked exactly.
    /// </remarks>
    internal static void Correct8(SKBitmap source, SKBitmap target, double[,] m, float s, float power)
    {
        if (s == 1 && power == 1)
        {
            using var filter = ColourMatrix(m);
            Banded(source, target, filter);
            target.NotifyPixelsChanged();
            return;
        }

        var (w, h) = (source.Width, source.Height);
        float Bend(float c, float max)
        {
            if (s != 1 && max > 0) c = max - (max - c) * s;
            return power == 1 ? c : c < 0 ? -MathF.Pow(-c, power) : MathF.Pow(c, power);
        }
        var table = new float[256 * 256];
        System.Threading.Tasks.Parallel.For(0, 256, max =>
        {
            for (var c = 0; c <= max; c++) table[max * 256 + c] = Bend(c / 255f, max / 255f);
        });

        // Synfig's row-vector matrix: channel k of the result is r·m[0,k] + g·m[1,k] + b·m[2,k] + a·m[3,k] + m[4,k].
        var f = new float[20];
        for (var k = 0; k < 4; k++)
            for (var n = 0; n < 5; n++) f[k * 5 + n] = (float)m[n, k];
        var (rowIn, rowOut) = (source.RowBytes, target.RowBytes);
        var bands = Bands(h);
        var step = (h + bands - 1) / bands;
        System.Threading.Tasks.Parallel.For(0, bands, i =>
        {
            var from = source.GetPixelSpan();
            var to = target.GetPixelSpan();
            for (var y = i * step; y < Math.Min(h, (i + 1) * step); y++)
                for (int x = 0, p = y * rowIn, q = y * rowOut; x < w; x++, p += 4, q += 4)
                {
                    var a8 = from[p + 3];
                    if (a8 == 0) { to[q] = to[q + 1] = to[q + 2] = to[q + 3] = 0; continue; }
                    float r, g, b, a = a8 / 255f;
                    if (a8 == 255)
                    {
                        var max = Math.Max(from[p], Math.Max(from[p + 1], from[p + 2])) * 256;
                        (r, g, b) = (table[max + from[p]], table[max + from[p + 1]], table[max + from[p + 2]]);
                    }
                    else
                    {
                        var k = 1f / a8;
                        (r, g, b) = (from[p] * k, from[p + 1] * k, from[p + 2] * k);
                        var max = MathF.Max(r, MathF.Max(g, b));
                        (r, g, b) = (Bend(r, max), Bend(g, max), Bend(b, max));
                    }
                    var alpha = Math.Clamp(r * f[15] + g * f[16] + b * f[17] + a * f[18] + f[19], 0f, 1f) * 255;
                    to[q] = (byte)(Math.Clamp(r * f[0] + g * f[1] + b * f[2] + a * f[3] + f[4], 0f, 1f) * alpha + 0.5f);
                    to[q + 1] = (byte)(Math.Clamp(r * f[5] + g * f[6] + b * f[7] + a * f[8] + f[9], 0f, 1f) * alpha + 0.5f);
                    to[q + 2] = (byte)(Math.Clamp(r * f[10] + g * f[11] + b * f[12] + a * f[13] + f[14], 0f, 1f) * alpha + 0.5f);
                    to[q + 3] = (byte)(alpha + 0.5f);
                }
        });
        target.NotifyPixelsChanged();
    }

    /// <summary>
    /// One straight colour, <c>[r, g, b, a]</c> in 0–1, through the correction at a time, as a pixel of it goes: saturated,
    /// bent, through the matrix and clamped. What <see cref="MotionGrade.Apply"/> reports.
    /// </summary>
    internal double[] Correct(double[] rgba, double time)
    {
        var m = Matrix(time);
        var (s, power) = (saturation.Evaluate(time)[0], 1 / gamma.Evaluate(time)[0]);
        var (r, g, b, a) = (rgba[0], rgba[1], rgba[2], rgba[3]);
        var max = Math.Max(r, Math.Max(g, b));
        (r, g, b) = (Bend(r), Bend(g), Bend(b));
        return [.. Enumerable.Range(0, 4).Select(k => Math.Clamp(r * m[0, k] + g * m[1, k] + b * m[2, k] + a * m[3, k] + m[4, k], 0, 1))];

        double Bend(double c)
        {
            if (s != 1 && max > 0) c = max - (max - c) * s;
            return power == 1 ? c : c < 0 ? -Math.Pow(-c, power) : Math.Pow(c, power);
        }
    }

    /// <summary>The colour matrix as a Skia colour filter.</summary>
    private static SKColorFilter ColourMatrix(double[,] m)
    {
        // Synfig's row-vector matrix as Skia's rows: R' = r·m00 + g·m10 + b·m20 + a·m30 + m40, and so on.
        var skia = new float[20];
        for (var c = 0; c < 4; c++)
            for (var k = 0; k < 5; k++)
                skia[c * 5 + k] = (float)m[k, c];
        return SKColorFilter.CreateColorMatrix(skia);
    }

    /// <summary>Synfig's <c>set_hue · set_exposure · set_brightness · set_contrast</c>, a 5 × 5 on row vectors.</summary>
    internal double[,] Matrix(double time)
    {
        var a = hue.Evaluate(time)[0] * Math.PI / 180;
        var (sn, cs) = Math.SinCos(a);
        double[,] encode = { { 0.299, -0.168736, 0.5, 0, 0 }, { 0.587, -0.331264, -0.418688, 0, 0 }, { 0.114, 0.5, -0.081312, 0, 0 }, { 0, 0, 0, 1, 0 }, { 0, 0, 0, 0, 1 } };
        double[,] rotate = { { 1, 0, 0, 0, 0 }, { 0, cs, sn, 0, 0 }, { 0, -sn, cs, 0, 0 }, { 0, 0, 0, 1, 0 }, { 0, 0, 0, 0, 1 } };
        double[,] decode = { { 1, 1, 1, 0, 0 }, { 0, -0.344136, 1.772, 0, 0 }, { 1.402, -0.714136, 0, 0, 0 }, { 0, 0, 0, 1, 0 }, { 0, 0, 0, 0, 1 } };
        var e = Math.Exp(exposure.Evaluate(time)[0]);
        var b = brightness.Evaluate(time)[0];
        var k = contrast.Evaluate(time)[0];
        double[,] expose = { { e, 0, 0, 0, 0 }, { 0, e, 0, 0, 0 }, { 0, 0, e, 0, 0 }, { 0, 0, 0, 1, 0 }, { 0, 0, 0, 0, 1 } };
        double[,] brighten = { { 1, 0, 0, 0, 0 }, { 0, 1, 0, 0, 0 }, { 0, 0, 1, 0, 0 }, { 0, 0, 0, 1, 0 }, { b, b, b, 0, 1 } };
        double[,] contrastM = { { k, 0, 0, 0, 0 }, { 0, k, 0, 0, 0 }, { 0, 0, k, 0, 0 }, { 0, 0, 0, 1, 0 }, { 0.5 - 0.5 * k, 0.5 - 0.5 * k, 0.5 - 0.5 * k, 0, 1 } };
        return new[] { rotate, decode, expose, brighten, contrastM }.Aggregate(encode, Multiply);

        static double[,] Multiply(double[,] x, double[,] y)
        {
            var r = new double[5, 5];
            for (var i = 0; i < 5; i++)
                for (var j = 0; j < 5; j++)
                    for (var n = 0; n < 5; n++) r[i, j] += x[i, n] * y[n, j];
            return r;
        }
    }

    /// <summary>
    /// Synfig's saturation then its gamma, on straight float colour: each channel pulled towards the brightest of the three,
    /// then raised to <paramref name="power"/> (1/γ), sign kept and nothing clamped.
    /// </summary>
    private static SKBitmap SaturateAndBend(SKBitmap source, float s, float power)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.RgbaF32, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
        }
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bitmap.GetPixelSpan()).ToArray();
        System.Threading.Tasks.Parallel.For(0, source.Height, y =>
        {
            for (int x = 0, i = y * source.Width * 4; x < source.Width; x++, i += 4)
            {
                var max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                var min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                if (s != 1 && max > 0 && max != min)
                    for (var c = 0; c < 3; c++) px[i + c] = max - (max - px[i + c]) * s;
                if (power != 1)
                    for (var c = 0; c < 3; c++) px[i + c] = px[i + c] < 0 ? -MathF.Pow(-px[i + c], power) : MathF.Pow(px[i + c], power);
            }
        });
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, byte>(px.AsSpan()).CopyTo(bitmap.GetPixelSpan());
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    public override XElement ToSif(SifWriter sif) =>
        new("layer", new XAttribute("type", "colorcorrect"), new XAttribute("active", "true"), new XAttribute("version", "0.2"),
            Desc is null ? null : new XAttribute("desc", Desc),
            SifWriter.Param("z_depth", new XElement("real", new XAttribute("value", "0"))),
            sif.Param("hue_adjust", hue),
            sif.Param("brightness", brightness),
            sif.Param("contrast", contrast),
            sif.Param("exposure", exposure),
            sif.Param("saturation", saturation),
            sif.Param("gamma", gamma));

    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map) =>
        throw new InvalidOperationException($"The colour correction{(Desc is null ? "" : $" '{Desc}'")} cannot be written as animated SVG yet. "
            + "Write a .sif, or capture the composition as frames.");
    #endregion
}
