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
/// Saturation and gamma are one pass on float colour, taken only when either is not its default; the matrix is a Skia
/// colour filter, which works on straight colour and keeps values outside [0, 1] through it.
/// <para>
/// <b>Measured, not read.</b> Synfig's current source clamps gamma's result to [0, 1]; the 1.5.5 binary does not, so a
/// channel saturation drove below 0 reaches the matrix still negative. Every pair of parameters matched either way; all six
/// together differed on 42% of pixels until gamma stopped clamping. A Synfig built from master will clamp there.
/// </para>
/// </remarks>
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

    public SkiaCanvas Apply(SkiaCanvas below, SKMatrix matrix, double time)
    {
        var (w, h) = (below.Width, below.Height);
        var source = below.SkBitmap;
        SKBitmap? bent = null;
        var (s, g) = (saturation.Evaluate(time)[0], gamma.Evaluate(time)[0]);
        if (Math.Abs(s - 1) > 1e-12 || Math.Abs(g - 1) > 1e-12) source = bent = SaturateAndBend(below.SkBitmap, (float)s, (float)(1 / g));

        using var filter = ColourMatrix(time);
        var result = new SkiaCanvas(w, h, below.SkBitmap.ColorType);
        result.SkCanvas.Clear(SKColors.Transparent);
        using (var paint = new SKPaint { ColorFilter = filter })
            result.SkCanvas.DrawBitmap(source, 0, 0, paint);
        bent?.Dispose();
        return result;
    }

    /// <summary>The colour matrix as a Skia colour filter.</summary>
    private SKColorFilter ColourMatrix(double time)
    {
        var m = Matrix(time);
        // Synfig's row-vector matrix as Skia's rows: R' = r·m00 + g·m10 + b·m20 + a·m30 + m40, and so on.
        var skia = new float[20];
        for (var c = 0; c < 4; c++)
            for (var k = 0; k < 5; k++)
                skia[c * 5 + k] = (float)m[k, c];
        return SKColorFilter.CreateColorMatrix(skia);
    }

    /// <summary>Synfig's <c>set_hue · set_exposure · set_brightness · set_contrast</c>, a 5 × 5 on row vectors.</summary>
    private double[,] Matrix(double time)
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
