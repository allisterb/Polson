namespace Polson.Tests.Animation;

using System;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// The luma key (<c>comp.lumaKey</c>): brightness becomes alpha, the luma matte it exists for, and Synfig's renderer on
/// the same <c>.sif</c>.
/// </summary>
public class MotionLumaKeyTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    /// <summary>White stays, black goes, grey is half there; and a colour is lifted to full luminance, keeping its hue.</summary>
    [Fact]
    public void BrightnessBecomesAlpha()
    {
        SKColor Keyed(string colour)
        {
            var comp = new MotionToolkit().Composition(Opts(("width", 8d), ("height", 8d)));
            var g = comp.Group();
            g.Fill(Opts(("color", colour)));
            g.LumaKey();
            using var frame = comp.Render(0);
            return frame.SkBitmap.GetPixel(4, 4);
        }

        Assert.Equal(SKColors.White, Keyed("#ffffff"));
        Assert.Equal(0, Keyed("#000000").Alpha);
        Assert.InRange((int)Keyed("#808080").Alpha, 126, 130);
        var red = Keyed("#c06060");
        Assert.True(red.Red > red.Green && Math.Abs(red.Green - red.Blue) <= 2, $"a keyed red keeps its hue: {red}");
    }

    /// <summary>
    /// The luma matte: a dark-to-light ramp keyed into an alpha, a colour blended onto it, so the colour shows as much as
    /// the ramp is bright — nothing at the dark end, all of it at the light end, about half in the middle.
    /// </summary>
    [Fact]
    public void ALumaMatteShowsWhatIsOntoItAsTheMatteIsBright()
    {
        var comp = Matted(new MotionToolkit());
        using var frame = comp.Render(0);
        var b = frame.SkBitmap;
        var (dark, mid, light) = (b.GetPixel(2, 40), b.GetPixel(120, 40), b.GetPixel(237, 40));
        Assert.True(dark.Red >= 250 && dark.Blue >= 250, $"at the dark end the page shows: {dark}");
        Assert.True(Math.Abs(light.Red - 0x1d) <= 4 && Math.Abs(light.Blue - 0x57) <= 4, $"at the light end the colour shows: {light}");
        Assert.InRange((int)mid.Red, 0x1d + 60, 255 - 60);                      // half the colour, half the page
        Assert.Contains("cannot be written as animated SVG", Assert.Throws<InvalidOperationException>(() => comp.ToSvg()).Message);
    }

    /// <summary>Synfig keys the same matte the same: its lumakey layer, written natively to the .sif.</summary>
    [Fact]
    public void SynfigKeysTheSame()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var comp = Matted(new MotionToolkit(), fps: 1);
        using var theirs = Assert.Single(SynfigOracle.Render(synfig, comp, "lumakey", output));
        using var ours = comp.Render(0);
        SynfigOracle.Dump(ours.SkBitmap, theirs, "lumakey");
        var differing = Differing(ours.SkBitmap, theirs, 24);
        output.WriteLine($"lumakey: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, theirs, 8):P2} by more than 8");
        Assert.True(differing < 0.02, $"{differing:P2} differ from synfig");
    }

    /// <summary>A white page; a group of a left-to-right ramp, the key, and a navy fill onto what the key left.</summary>
    private static MotionComposition Matted(MotionToolkit motion, double fps = 24)
    {
        var comp = motion.Composition(Opts(("width", 240d), ("height", 80d), ("fps", fps), ("duration", 0d)));
        comp.Fill(Opts(("color", "#ffffff")));
        var g = comp.Group();
        g.LinearGradient(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 240d, 0d }), ("stops", new object[] { "#000000", "#ffffff" })));
        g.LumaKey();
        g.Fill(Opts(("color", "#1d3557"), ("blend", "onto")));
        return comp;
    }
    #endregion
}
