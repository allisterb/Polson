namespace Polson.Tests.Animation;

using System;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// Colour correction (<c>comp.colorCorrect</c>, Synfig's colorcorrect layer): each parameter, pairs, and all of them
/// against Synfig's renderer, over colour and over a semi-transparent shape; idle at the defaults; keyed; the group
/// boundary. The combinations are what found that Synfig 1.5.5's gamma does not clamp: every pair matched either way.
/// </summary>
public class MotionColorCorrectTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    [Theory]
    [InlineData("hue", 60d)]
    [InlineData("exposure", 0.5)]
    [InlineData("brightness", 0.15)]
    [InlineData("contrast", 1.6)]
    [InlineData("gamma", 2.2)]
    [InlineData("saturation", 0.3)]
    [InlineData("all", 0d)]
    [InlineData("negBrightness", 0d)]
    [InlineData("brightContrast", 0d)]
    [InlineData("satUp", 0d)]
    [InlineData("hueExposure", 0d)]
    [InlineData("gammaContrast", 0d)]
    [InlineData("satGamma", 0d)]
    [InlineData("satHue", 0d)]
    [InlineData("satExposure", 0d)]
    [InlineData("satContrast", 0d)]
    [InlineData("noSat", 0d)]
    public void SynfigCorrectsTheSame(string what, double value)
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var comp = Scene();
        var g = comp.Group();
        Content(g);
        g.ColorCorrect(what switch
        {
            "all" => Opts(("hue", -40d), ("exposure", 0.3), ("brightness", -0.05), ("contrast", 1.3), ("gamma", 1.5), ("saturation", 1.4)),
            "negBrightness" => Opts(("brightness", -0.15)),
            "brightContrast" => Opts(("brightness", 0.1), ("contrast", 1.3)),
            "satUp" => Opts(("saturation", 1.4)),
            "hueExposure" => Opts(("hue", -40d), ("exposure", 0.3)),
            "gammaContrast" => Opts(("gamma", 1.5), ("contrast", 1.3)),
            "satGamma" => Opts(("saturation", 1.4), ("gamma", 1.5)),
            "satHue" => Opts(("saturation", 1.4), ("hue", -40d)),
            "satExposure" => Opts(("saturation", 1.4), ("exposure", 0.3)),
            "satContrast" => Opts(("saturation", 1.4), ("contrast", 1.3)),
            "noSat" => Opts(("hue", -40d), ("exposure", 0.3), ("brightness", -0.05), ("contrast", 1.3), ("gamma", 1.5)),
            _ => Opts((what, value)),
        });

        using var theirs = Assert.Single(SynfigOracle.Render(synfig, comp, $"cc-{what}", output));
        using var ours = comp.Render(0);
        SynfigOracle.Dump(ours.SkBitmap, theirs, $"cc-{what}");
        using var plain = Plain().Render(0);
        var differing = Differing(ours.SkBitmap, theirs, 24);
        output.WriteLine($"{what} {value}: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, theirs, 8):P2} by more than 8; synfig vs uncorrected {Differing(plain.SkBitmap, theirs, 8):P2}");
        Assert.True(Differing(plain.SkBitmap, theirs, 8) > 0.05, "synfig's frame is uncorrected: did it drop the layer or the parameter?");
        // By more than 24: a gamma curve amplifies 8-bit steps on the grey ramp, so the finer figure reaches 2% there.
        Assert.True(differing < 0.01, $"{differing:P2} differ from synfig by more than 24");
    }

    [Fact]
    public void AtTheDefaultsItChangesNothing()
    {
        var comp = Scene();
        var g = comp.Group();
        Content(g);
        g.ColorCorrect();
        using var a = comp.Render(0);
        using var b = Plain().Render(0);
        Assert.Equal(0, Differing(a.SkBitmap, b.SkBitmap, 0));
    }

    [Fact]
    public void ExposureKeysAndItStopsAtItsGroup()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 100d), ("height", 50d), ("fps", 1d), ("duration", 1d)));
        comp.Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 50d, 50d }), ("color", "#404040")));       // outside the group
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 50d, 0d }), ("point2", new[] { 100d, 50d }), ("color", "#404040")));
        g.ColorCorrect(Opts(("exposure", motion.Nodes.Linear("real", 1d, 0d))));

        using var at0 = comp.Render(0);
        using var at1 = comp.Render(1);
        Assert.Equal(0x40, at1.SkBitmap.GetPixel(25, 25).Red);
        Assert.Equal(0x40, at0.SkBitmap.GetPixel(75, 25).Red);
        Assert.InRange(at1.SkBitmap.GetPixel(75, 25).Red, 0x40 * 2.718 - 2, 0x40 * 2.718 + 2);   // × e
    }

    /// <summary>
    /// On 8-bit colour the correction is one banded pass — Skia's filter band by band for the matrix alone, a managed pass
    /// with a 256 × 256 table for saturation and gamma — and must agree with the float path it replaced. trailer3 dropped
    /// saturation from its grade because the float path cost 47 ms a frame.
    /// </summary>
    [Theory]
    [InlineData("contrast")]
    [InlineData("saturation")]
    [InlineData("gamma")]
    [InlineData("all")]
    [InlineData("satUp")]
    public void TheEightBitPassAgreesWithTheFloatPass(string what)
    {
        var comp = Scene();
        var g = comp.Group();
        Content(g);
        g.ColorCorrect(what switch
        {
            "contrast" => Opts(("contrast", 0.93), ("brightness", 0.025)),
            "saturation" => Opts(("saturation", 0.85)),
            "gamma" => Opts(("gamma", 1.6)),
            "satUp" => Opts(("saturation", 1.4), ("gamma", 1.5)),
            _ => Opts(("hue", -40d), ("exposure", 0.3), ("brightness", -0.05), ("contrast", 1.3), ("gamma", 1.5), ("saturation", 1.4)),
        });
        var layer = comp.AllLayers().OfType<MotionColorCorrectLayer>().Single();
        var (s, power) = what switch { "saturation" => (0.85f, 1f), "gamma" => (1f, 1 / 1.6f), "satUp" => (1.4f, 1 / 1.5f), "all" => (1.4f, 1 / 1.5f), _ => (1f, 1f) };
        using var source = Plain().Render(0);
        using var eight = new SKBitmap(source.SkBitmap.Info);
        using var single = new SKBitmap(source.SkBitmap.Info);
        MotionColorCorrectLayer.Correct8(source.SkBitmap, eight, layer.Matrix(0), s, power);
        using (var canvas = new SKCanvas(single))
        {
            canvas.Clear(SKColors.Transparent);
            MotionColorCorrectLayer.CorrectFloat(source.SkBitmap, canvas, layer.Matrix(0), s, power);
        }
        var (x, y) = (eight.GetPixelSpan().ToArray(), single.GetPixelSpan().ToArray());
        var worst = x.Zip(y, (p, q) => Math.Abs(p - q)).Max();
        output.WriteLine($"{what}: worst channel difference {worst}");
        Assert.True(worst <= 1, $"{what}: the 8-bit pass differs from the float pass by {worst} levels");
    }

    /// <summary>
    /// <c>Motion.grade(options).apply(color)</c> is the colour a <c>comp.colorCorrect</c> with the same options draws, to a
    /// level: trailer4 rendered a 4 × 4 probe composition and read a pixel back to learn what its grade did to magenta.
    /// </summary>
    [Theory]
    [InlineData("contrast")]
    [InlineData("saturation")]
    [InlineData("gamma")]
    [InlineData("hue")]
    [InlineData("trailer4")]
    [InlineData("keyed")]
    public void AGradeAppliedToAColourIsWhatTheRenderDraws(string what)
    {
        var motion = new MotionToolkit();
        System.Collections.Generic.Dictionary<string, object?> Options() => what switch
        {
            "contrast" => Opts(("contrast", 0.93), ("brightness", 0.025)),
            "saturation" => Opts(("saturation", 0.6)),
            "gamma" => Opts(("gamma", 1.6)),
            "hue" => Opts(("hue", 40d), ("exposure", 0.2)),
            "trailer4" => Opts(("hue", 2d), ("contrast", 1.06), ("saturation", 1.04), ("gamma", 0.96), ("desc", "grade")),
            _ => Opts(("exposure", motion.Nodes.Linear("real", 0.5, 0d))),
        };
        var grade = motion.Grade(Options());
        foreach (var colour in new[] { "#C8205A", "#F2C14E", "#EFE6D2", "#141414", "#2a6f97" })
        {
            var comp = motion.Composition(Opts(("width", 4d), ("height", 4d), ("fps", 1d), ("duration", 1d)));
            comp.Fill(Opts(("color", colour)));
            comp.ColorCorrect(Options());
            var t = what == "keyed" ? 1d : 0d;
            using var frame = comp.Render(t);
            var drawn = frame.SkBitmap.GetPixel(2, 2);
            var applied = SKColor.Parse(grade.Apply(colour, t)[..7]);     // CSS #RRGGBBAA; Skia would read eight digits as #AARRGGBB
            output.WriteLine($"{what} {colour}: drawn {drawn}, applied {applied}");
            Assert.InRange(Math.Abs(drawn.Red - applied.Red), 0, 1);
            Assert.InRange(Math.Abs(drawn.Green - applied.Green), 0, 1);
            Assert.InRange(Math.Abs(drawn.Blue - applied.Blue), 0, 1);
        }
    }

    private static MotionComposition Scene() =>
        new MotionToolkit().Composition(Opts(("width", 240d), ("height", 120d), ("fps", 1d), ("duration", 0d)));

    private static MotionComposition Plain()
    {
        var comp = Scene();
        Content(comp.Group());
        return comp;
    }

    /// <summary>
    /// Six patches of colour, a grey ramp, and a semi-transparent disc over both. Patches, not a many-stop gradient: Synfig's
    /// linear gradient rounds every bend in its colour (see <see cref="MotionGradientTests"/>), which would measure the
    /// gradient, not the correction. A two-stop ramp has no bend.
    /// </summary>
    private static void Content(MotionLayerList g)
    {
        string[] patches = ["#ff3800", "#bdff00", "#00c060", "#20a0ff", "#2d00ff", "#ff40c0"];
        for (var i = 0; i < patches.Length; i++)
            g.Rectangle(Opts(("point1", new[] { 40d * i, 0d }), ("point2", new[] { 40d * i + 40, 60d }), ("color", patches[i])));
        var ramp = g.Group();
        ramp.Rectangle(Opts(("point1", new[] { 0d, 60d }), ("point2", new[] { 240d, 120d }), ("color", "#000000")));
        ramp.LinearGradient(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 240d, 0d }), ("blend", "onto"), ("stops", new object[] { "#000000", "#ffffff" })));
        g.Circle(Opts(("origin", new[] { 120d, 60d }), ("radius", 40d), ("color", "#e6394680")));
    }
    #endregion
}
