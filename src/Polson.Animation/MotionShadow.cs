namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// Synfig's shade layer: the stack below it as a shadow — its shape in one colour, moved by <c>offset</c> and blurred —
/// blended with that stack, by default behind it at three quarters.
/// </summary>
/// <remarks>
/// <b>Ported from Synfig</b> (<c>modules/lyr_std/shade.cpp</c>, GPL-2-or-later). Synfig replaces the colour of what is
/// below, keeping its alpha (<c>ColorMatrix::set_replace_color</c>: the shade colour's own alpha plays no part), inverts
/// the alpha when asked, translates by <c>origin</c> and blurs with one of the blur layer's kernels, which are reused here,
/// then blends the result with the stack, the shadow as the layer and the stack as what it lands on. Its defaults are
/// amount 0.75 and blend behind; its default size, 0.1 of its units, is invisible in pixels, so ours is 6 as the blur's is.
/// Like the blur it stops at its own group.
/// </remarks>
internal sealed class MotionShadowLayer : MotionLayer, IMotionStackFilter
{
    #region Constructors
    public MotionShadowLayer(MotionOptions o) : base(o)
    {
        offset = o.Node("offset", MotionType.Vector, new[] { 6d, 6d });
        size = MotionBlurLayer.ReadSize(o);
        type = o.Has("type") ? MotionBlurLayer.ParseType(o.Raw("type")?.ToString(), o.Who) : MotionBlurLayer.BlurType.FastGaussian;
        color = o.Node("color", MotionType.Color, "#000000");
        invert = o.Bool("invert", false);
    }
    #endregion

    #region Fields
    private readonly MotionNode offset, size, color;
    private readonly MotionBlurLayer.BlurType type;
    private readonly bool invert;
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() => [Amount, offset, size, color];

    protected override void Draw(SkiaCanvas surface, double time, double amount) =>
        throw new InvalidOperationException("A shadow is applied by its stack, to what is below it.");

    public bool IsIdle(SKMatrix matrix, double time) => AmountAt(time) <= 0;

    /// <summary>The blur's reach plus how far the shadow is moved, in device pixels.</summary>
    public (int X, int Y) Margin(SKMatrix matrix, double time)
    {
        var (sx, sy) = MotionBlurLayer.Pixels(size, matrix, time);
        var (bx, by) = MotionBlurLayer.MarginFor(type, sx, sy);
        var d = Shift(matrix, time);
        return (bx + (int)Math.Ceiling(Math.Abs(d.X)), by + (int)Math.Ceiling(Math.Abs(d.Y)));
    }

    public SkiaCanvas Apply(SkiaCanvas below, SKMatrix matrix, double time)
    {
        var amount = AmountAt(time);
        var (w, h) = (below.Width, below.Height);
        var result = new SkiaCanvas(w, h, below.SkBitmap.ColorType);
        result.SkCanvas.Clear(SKColors.Transparent);
        result.SkCanvas.DrawBitmap(below.SkBitmap, 0, 0);
        if (amount <= 0) return result;

        // The shape's coverage, moved: where the stack has alpha, or, inverted, everywhere it has none. A shadow is one
        // colour, so only its coverage is blurred — a quarter of the work of blurring colour.
        var d = Shift(matrix, time);
        var alpha = new float[w * h];
        using (var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul)))
        {
            using (var canvas = new SKCanvas(mask))
            using (var image = SKImage.FromBitmap(below.SkBitmap))
            {
                canvas.Clear(invert ? SKColors.Black : SKColors.Transparent);
                using var paint = new SKPaint { BlendMode = invert ? SKBlendMode.DstOut : SKBlendMode.SrcOver };
                canvas.DrawImage(image, d.X, d.Y, new SKSamplingOptions(SKFilterMode.Linear), paint);
            }
            var stride = mask.RowBytes;
            var bytes = mask.GetPixelSpan().ToArray();
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++) alpha[y * w + x] = bytes[y * stride + x] / 255f;
            });
        }

        var (sx, sy) = MotionBlurLayer.Pixels(size, matrix, time);
        if (sx > 1e-10 || sy > 1e-10) alpha = MotionBlurLayer.BlurredAlpha(alpha, w, h, type, sx, sy, MotionBlurLayer.MarginFor(type, sx, sy));

        // The colour at that coverage, premultiplied, built in a managed buffer row by row in parallel and copied in once.
        var ink = color.Evaluate(time);
        var (r, g, b) = ((float)ink[0] * 255, (float)ink[1] * 255, (float)ink[2] * 255);
        var rgba = new byte[w * h * 4];
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w, o = y * w * 4; x < w; x++, i++, o += 4)
            {
                var a = Math.Clamp(alpha[i], 0f, 1f);
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = ((byte)(r * a + 0.5f), (byte)(g * a + 0.5f), (byte)(b * a + 0.5f), (byte)(255 * a + 0.5f));
            }
        });
        using var shade = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        rgba.CopyTo(shade.GetPixelSpan());
        shade.NotifyPixelsChanged();

        // The shadow is the layer, the stack what it lands on. Synfig's composite, behind and onto at an amount are exactly
        // Skia's source-over, destination-over and source-atop with the source scaled by it, and those run natively; any
        // other blend takes the Synfig formula as a runtime blender, five times slower at 720p.
        var native = Blend.Name switch
        {
            "composite" => SKBlendMode.SrcOver,
            "behind" => SKBlendMode.DstOver,
            "onto" => SKBlendMode.SrcATop,
            _ => (SKBlendMode?)null,
        };
        if (native is { } mode)
        {
            using var paint = new SKPaint { BlendMode = mode, Color = SKColors.White.WithAlpha((byte)Math.Round(amount * 255)) };
            result.SkCanvas.DrawBitmap(shade, 0, 0, paint);
        }
        else
        {
            using var blender = Blend.Blender(amount);
            using var paint = new SKPaint { Blender = blender };
            result.SkCanvas.DrawBitmap(shade, 0, 0, paint);
        }
        return result;
    }

    /// <summary><c>offset</c> in device pixels, through the stack's own transform.</summary>
    private SKPoint Shift(SKMatrix matrix, double time)
    {
        var o = offset.Evaluate(time);
        return matrix.MapVector((float)o[0], (float)o[1]);
    }

    public override XElement ToSif(SifWriter sif) => Element("shade", "0.2", sif,
        sif.Param("color", color),
        sif.Param("origin", offset),
        sif.Param("size", size),
        SifWriter.Param("type", new XElement("integer", new XAttribute("value", (int)type))),
        SifWriter.Param("invert", SifWriter.Bool(invert)));

    /// <summary>
    /// A marker the stack wraps everything before it in: an SVG drop shadow — the stack's alpha blurred, moved, flooded with
    /// the colour at the amount, and merged under the stack. Only the default shadow, behind and not inverted, with a
    /// Gaussian kernel: anything else SVG's filters cannot say.
    /// </summary>
    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
    {
        var name = Desc is null ? "" : $" '{Desc}'";
        if (Blend.Name != "behind" || invert)
            throw new InvalidOperationException($"The shadow{name} {(invert ? "is inverted" : $"blends by '{Blend.Name}'")}, which SVG cannot say: "
                + "SVG draws a drop shadow behind. Capture the composition as frames instead.");
        if (type is not (MotionBlurLayer.BlurType.Gaussian or MotionBlurLayer.BlurType.FastGaussian))
            throw new InvalidOperationException($"The shadow{name} is a {MotionBlurLayer.Name(type)} blur, which SVG cannot draw: SVG blurs are Gaussian.");

        var ns = SvgAnimationWriter.Svg;
        var blur = new XElement(ns + "feGaussianBlur", new XAttribute("in", "SourceAlpha"), new XAttribute("result", "b"));
        svg.Attribute(blur, "stdDeviation", t =>
        {
            var s = size.Evaluate(map.Local(t));
            return $"{SvgAnimationWriter.N(Math.Abs(s[0]) * MotionBlurLayer.GaussAmplifier)} {SvgAnimationWriter.N(Math.Abs(s[1]) * MotionBlurLayer.GaussAmplifier)}";
        });
        var shift = new XElement(ns + "feOffset", new XAttribute("in", "b"), new XAttribute("result", "o"));
        svg.Attribute(shift, "dx", svg.Track(offset, 0, map));
        svg.Attribute(shift, "dy", svg.Track(offset, 1, map));
        var flood = new XElement(ns + "feFlood", new XAttribute("result", "f"));
        svg.Attribute(flood, "flood-color", t =>
        {
            var c = MotionTypes.ToSkColor(color.Evaluate(map.Local(t)));
            return $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
        });
        svg.Attribute(flood, "flood-opacity", svg.Track(Amount, 0, map, v => Math.Clamp(v, 0, 1)));
        var shadow = new XElement(ns + "feComposite", new XAttribute("in", "f"), new XAttribute("in2", "o"), new XAttribute("operator", "in"), new XAttribute("result", "s"));
        var merge = new XElement(ns + "feMerge",
            new XElement(ns + "feMergeNode", new XAttribute("in", "s")),
            new XElement(ns + "feMergeNode", new XAttribute("in", "SourceGraphic")));
        return new XElement(ns + "g", new XAttribute(SvgAnimationWriter.BelowFilter, svg.Filter(blur, shift, flood, shadow, merge)));
    }
    #endregion
}
