namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// Synfig's luma key: everything below it in its stack made as transparent as it is dark. A white pixel stays opaque, a
/// black one goes, a grey one is half there; and every pixel is brightened to full luminance, keeping its hue and chroma,
/// so a grey matte becomes white with its brightness for alpha.
/// </summary>
/// <remarks>
/// <b>Ported from Synfig</b> (<c>modules/mod_filter/lumakey.cpp</c>, GPL-2-or-later), on straight colour as Synfig holds
/// it: in YUV, alpha becomes alpha × Y and then Y becomes 1, <c>[r g b] = decode(1, u, v)</c>. It takes no parameters and
/// has no amount, as Synfig's has none. What it is for is a <b>luma matte</b>: a matte (an <c>Assets.matte</c>, a drawn
/// gradient, a photograph of smoke or ink) keyed into an alpha, and then what should show through it blended
/// <c>'onto'</c> it in the same group.
/// </remarks>
internal sealed class MotionLumaKeyLayer(MotionOptions o) : MotionLayer(o), IMotionStackFilter
{
    #region Methods
    public override IEnumerable<MotionNode> Nodes() => [Amount];

    protected override void Draw(SkiaCanvas surface, double time, double amount) =>
        throw new InvalidOperationException("A luma key is applied by its stack, to what is below it.");

    public SkiaCanvas Apply(SkiaCanvas below, SKMatrix matrix, double time)
    {
        var result = new SkiaCanvas(below.Width, below.Height, below.SkBitmap.ColorType);
        if (below.SkBitmap.ColorType == SKColorType.Rgba8888 && below.SkBitmap.AlphaType == SKAlphaType.Premul)
            Key8(below.SkBitmap, result.SkBitmap);
        else
        {
            using var keyed = KeyFloat(below.SkBitmap);
            result.SkCanvas.Clear(SKColors.Transparent);
            result.SkCanvas.DrawBitmap(keyed, 0, 0);
        }
        return result;
    }

    /// <summary>One pixel, straight: alpha × Y for alpha, then the colour at Y = 1 with its U and V kept.</summary>
    private static (float R, float G, float B, float A) Key(float r, float g, float b, float a)
    {
        var y = 0.299f * r + 0.587f * g + 0.114f * b;
        var u = -0.168736f * r - 0.331264f * g + 0.5f * b;
        var v = 0.5f * r - 0.418688f * g - 0.081312f * b;
        return (1 + 1.402f * v, 1 - 0.344136f * u - 0.714136f * v, 1 + 1.772f * u, a * y);
    }

    /// <summary>On 8-bit premultiplied colour, in row bands across the cores; straight colour clamped before it is stored.</summary>
    private static void Key8(SKBitmap source, SKBitmap target)
    {
        var (w, h) = (source.Width, source.Height);
        var src = source.GetPixelSpan().ToArray();
        var dst = new byte[src.Length];
        System.Threading.Tasks.Parallel.For(0, h, row =>
        {
            for (int x = 0, i = row * source.RowBytes; x < w; x++, i += 4)
            {
                var a = src[i + 3];
                if (a == 0) continue;
                var k = 1f / a;
                var (r, g, b, oa) = Key(src[i] * k, src[i + 1] * k, src[i + 2] * k, a / 255f);
                oa = Math.Clamp(oa, 0, 1);
                dst[i] = (byte)MathF.Round(Math.Clamp(r, 0, 1) * oa * 255);
                dst[i + 1] = (byte)MathF.Round(Math.Clamp(g, 0, 1) * oa * 255);
                dst[i + 2] = (byte)MathF.Round(Math.Clamp(b, 0, 1) * oa * 255);
                dst[i + 3] = (byte)MathF.Round(oa * 255);
            }
        });
        dst.CopyTo(target.GetPixelSpan());
        target.NotifyPixelsChanged();
    }

    /// <summary>On any other format: through straight float colour, nothing clamped.</summary>
    private static SKBitmap KeyFloat(SKBitmap source)
    {
        var bitmap = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.RgbaF32, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
        }
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bitmap.GetPixelSpan()).ToArray();
        System.Threading.Tasks.Parallel.For(0, source.Height, row =>
        {
            for (int x = 0, i = row * source.Width * 4; x < source.Width; x++, i += 4)
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = Key(px[i], px[i + 1], px[i + 2], px[i + 3]);
        });
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, byte>(px.AsSpan()).CopyTo(bitmap.GetPixelSpan());
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    public override XElement ToSif(SifWriter sif) =>
        new("layer", new XAttribute("type", "lumakey"), new XAttribute("active", "true"), new XAttribute("version", "0.2"),
            Desc is null ? null : new XAttribute("desc", Desc),
            SifWriter.Param("z_depth", new XElement("real", new XAttribute("value", "0"))));

    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map) =>
        throw new InvalidOperationException($"The luma key{(Desc is null ? "" : $" '{Desc}'")} cannot be written as animated SVG. "
            + "Write a .sif, or capture the composition as frames.");
    #endregion
}
