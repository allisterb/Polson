namespace Polson.Tests.Animation;

using System;
using System.Linq;

using Polson.Animation;
using Polson.Drawing.Svg;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// Wipes (<c>comp.wipe</c>): each type at completion 0, ½ and 1 against the mask it should be, feathers, the refusals, the
/// SVG route, and Synfig's renderer on each <c>.sif</c> form.
/// </summary>
public class MotionWipeTests(ITestOutputHelper output) : TestsRuntime
{
    #region Fields
    private const double W = 240, H = 160;
    private static readonly SKColor Content = SKColor.Parse("#1d3557");
    #endregion

    #region Methods
    [Theory]
    [InlineData("linear")]
    [InlineData("radial")]
    [InlineData("iris")]
    [InlineData("blinds")]
    public void AtNoCompletionNothingGoesAndAtFullEverythingDoes(string type)
    {
        using (var none = Wiped(Opts(("type", type), ("completion", 0d))).Render(0))
            Assert.True(Share(none.SkBitmap, Content) > 0.999, $"{type} at 0 erased something");
        using var all = Wiped(Opts(("type", type), ("completion", 1d))).Render(0);
        Assert.True(Share(all.SkBitmap, Content) < 0.001, $"{type} at 1 left something");
    }

    [Fact]
    public void ALinearWipeCrossesAlongItsAngle()
    {
        using var frame = Wiped(Opts(("type", "linear"), ("completion", 0.5))).Render(0);
        Assert.Equal(SKColors.White, frame.SkBitmap.GetPixel(100, 80));       // left of the middle: gone
        Assert.Equal(Content, frame.SkBitmap.GetPixel(140, 80));

        using var down = Wiped(Opts(("type", "linear"), ("angle", 180d), ("completion", 0.25))).Render(0);
        Assert.Equal(SKColors.White, down.SkBitmap.GetPixel(120, 30));        // the top quarter
        Assert.Equal(Content, down.SkBitmap.GetPixel(120, 50));
    }

    /// <summary>The feather is a linear ramp of its width centred on the edge.</summary>
    [Fact]
    public void AFeatherIsALinearRamp()
    {
        using var frame = Wiped(Opts(("type", "linear"), ("completion", 0.5), ("feather", 40d))).Render(0);
        var row = Enumerable.Range(0, (int)W).Select(x => frame.SkBitmap.GetPixel(x, 80).Red).ToArray();
        Assert.Equal(255, row[90]);                    // fully gone a feather's half and more before the edge
        Assert.Equal(Content.Red, row[150]);
        Assert.InRange(row[120], (Content.Red + 255) / 2 - 8, (Content.Red + 255) / 2 + 8);   // half at the edge
    }

    [Fact]
    public void ARadialWipeSweepsFromItsStart()
    {
        using var cw = Wiped(Opts(("type", "radial"), ("completion", 0.25))).Render(0);
        Assert.Equal(SKColors.White, cw.SkBitmap.GetPixel(160, 40));          // top right: swept
        Assert.Equal(Content, cw.SkBitmap.GetPixel(80, 40));                   // top left: not yet
        Assert.Equal(Content, cw.SkBitmap.GetPixel(160, 120));

        // Both ways: 45° either side of up at 0.25.
        using var both = Wiped(Opts(("type", "radial"), ("sweep", "both"), ("completion", 0.25))).Render(0);
        Assert.Equal(SKColors.White, both.SkBitmap.GetPixel(130, 20));
        Assert.Equal(SKColors.White, both.SkBitmap.GetPixel(110, 20));
        Assert.Equal(Content, both.SkBitmap.GetPixel(200, 60));
        Assert.Equal(Content, both.SkBitmap.GetPixel(120, 140));
    }

    [Fact]
    public void AnIrisOpensAndAnInvertedOneCloses()
    {
        using var open = Wiped(Opts(("type", "iris"), ("completion", 0.3))).Render(0);
        Assert.Equal(SKColors.White, open.SkBitmap.GetPixel(120, 80));
        Assert.Equal(Content, open.SkBitmap.GetPixel(5, 5));

        using var close = Wiped(Opts(("type", "iris"), ("invert", true), ("completion", 0.7))).Render(0);
        Assert.Equal(Content, close.SkBitmap.GetPixel(120, 80));
        Assert.Equal(SKColors.White, close.SkBitmap.GetPixel(5, 5));

        using var star = Wiped(Opts(("type", "iris"), ("points", 5d), ("innerRadius", 0.5), ("completion", 0.3))).Render(0);
        Assert.Equal(SKColors.White, star.SkBitmap.GetPixel(120, 80));
        Assert.Equal(Content, star.SkBitmap.GetPixel(5, 5));
    }

    [Fact]
    public void BlindsCloseBandByBand()
    {
        // angle 90: the blinds' direction is down (EffectCraft's), so the bands lie across it, horizontal.
        using var frame = Wiped(Opts(("type", "blinds"), ("angle", 90d), ("width", 40d), ("completion", 0.5))).Render(0);
        var column = Enumerable.Range(0, 80).Select(y => frame.SkBitmap.GetPixel(120, y) == SKColors.White).ToArray();
        Assert.True(column[5] && column[45], "the first half of each band is not gone");
        Assert.True(!column[25] && !column[65], "the second half of each band is gone");
    }

    [Theory]
    [InlineData("spiral", "has no type 'spiral'")]
    [InlineData("width", "A linear wipe has no 'width'")]
    [InlineData("blend", "has no 'blend'")]
    [InlineData("sweep", "'clockwise', 'counterclockwise' or 'both'")]
    public void BadWipesAreRefused(string what, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => Scene().Group().Wipe(what switch
        {
            "spiral" => Opts(("type", "spiral")),
            "width" => Opts(("type", "linear"), ("width", 20d)),
            "blend" => Opts(("type", "linear"), ("blend", "multiply")),
            _ => Opts(("type", "radial"), ("sweep", "sideways")),
        }));
        Assert.Contains(message, e.Message);
    }

    /// <summary>
    /// Hard-edged linear, radial and iris wipes go to SVG as the eraser shape, drawn the same at every frame; blinds are
    /// refused. Between frames SVG interpolates the shape's points, which is exact for a moving edge or a growing circle and
    /// not for a turning wedge, so the comparison is at frame times.
    /// </summary>
    [Theory]
    [InlineData("linear")]
    [InlineData("radial")]
    [InlineData("iris")]
    public void HardWipesGoToSvg(string type)
    {
        var motion = new MotionToolkit();
        var comp = Wiped(Opts(("type", type), ("completion", motion.Nodes.Linear("real", 0.8, 0.1))), motion, 1);
        var svg = comp.ToSvg();
        foreach (var t in new[] { 0d, 1d })
        {
            using var ours = comp.Render(t);
            using var theirs = SKBitmap.Decode(SvgRenderPipeline.RenderToImage(svg, comp.Width, comp.Height, "png", 100, null, TimeSpan.FromSeconds(t)));
            var differing = Differing(ours.SkBitmap, theirs, 24);
            output.WriteLine($"{type} svg t {t}: {differing:P2} differ");
            Assert.True(differing < 0.01);
        }
        Assert.Contains("change in number", Assert.Throws<InvalidOperationException>(() => Wiped(Opts(("type", "blinds"), ("completion", 0.5))).ToSvg()).Message);
    }

    /// <summary>Synfig draws each .sif form the same, the completion keyed over three frames.</summary>
    [Theory]
    [InlineData("linear", 0d)]
    [InlineData("linear", 30d)]
    [InlineData("radial", 0d)]
    [InlineData("radial", 40d)]
    [InlineData("iris", 0d)]
    [InlineData("iris", 12d)]
    [InlineData("star", 0d)]
    public void SynfigWipesTheSame(string kind, double feather)
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        var completion = motion.Nodes.Linear("real", 0.3, 0.2);
        var options = kind switch
        {
            "linear" => Opts(("type", "linear"), ("angle", 120d)),
            "radial" => Opts(("type", "radial"), ("startAngle", 30d), ("center", new[] { 110d, 70d })),
            "iris" => Opts(("type", "iris"), ("invert", true), ("center", new[] { 130d, 90d })),
            "star" => Opts(("type", "iris"), ("points", 5d), ("innerRadius", 0.5), ("rotation", 10d)),
            _ => Opts(("type", "blinds"), ("angle", 30d), ("width", 36d)),
        };
        options["completion"] = completion;
        options["feather"] = feather;
        var comp = Wiped(options, motion, 2);

        var theirs = SynfigOracle.Render(synfig, comp, $"wipe-{kind}-{feather}", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"wipe-{kind}-{feather}-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"{kind} feather {feather} frame {i}: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, bitmap, 8):P2} by more than 8");
            Assert.True(differing < 0.02, $"{differing:P2} differ from synfig at frame {i}");
        }
    }

    /// <summary>
    /// Blinds go to .sif as Synfig's looped linear gradient, which averages each pixel over a tenth of the gradient's length
    /// either way — a tenth of a band here — so Synfig draws each band's edges softer. It spreads the erasing rather than
    /// losing any: measured, both erase 19.9% of this frame, and 22% of pixels differ by more than 24 levels. The documented
    /// limit, pinned: a Synfig that stops softening fails here, and the docs then say too much.
    /// </summary>
    [Fact]
    public void SynfigSoftensBlinds()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        var comp = Wiped(Opts(("type", "blinds"), ("angle", 30d), ("width", 36d), ("feather", 8d), ("completion", 0.2)), motion, 0);
        using var theirs = Assert.Single(SynfigOracle.Render(synfig, comp, "wipe-blinds", output));
        using var ours = comp.Render(0);
        double Erased(SKBitmap b) => Enumerable.Range(0, b.Height).Sum(y => Enumerable.Range(0, b.Width).Sum(x => (b.GetPixel(x, y).Red - Content.Red) / (255.0 - Content.Red))) / (b.Width * b.Height);
        output.WriteLine($"blinds at 0.2, feather 8, width 36: ours erases {Erased(ours.SkBitmap):P1} of the frame, synfig {Erased(theirs):P1}; {Differing(ours.SkBitmap, theirs, 24):P1} differ by more than 24");
        Assert.InRange(Erased(ours.SkBitmap), 0.17, 0.23);
        Assert.Equal(Erased(ours.SkBitmap), Erased(theirs), 2);                    // the same erasing, spread
        Assert.True(Differing(ours.SkBitmap, theirs, 24) > 0.1, "synfig no longer softens the blinds: update the docs");
    }

    private static MotionComposition Scene(MotionToolkit? motion = null, double duration = 0)
    {
        var comp = (motion ?? new MotionToolkit()).Composition(Opts(("width", W), ("height", H), ("fps", 1d), ("duration", duration)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }

    /// <summary>White page; a group holding the content filling the frame and the wipe.</summary>
    private static MotionComposition Wiped(System.Collections.Generic.Dictionary<string, object?> wipe, MotionToolkit? motion = null, double duration = 0)
    {
        var comp = Scene(motion, duration);
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { W, H }), ("color", "#1d3557")));
        g.Wipe(wipe);
        return comp;
    }

    private static double Share(SKBitmap b, SKColor c) =>
        Enumerable.Range(0, b.Height).Sum(y => Enumerable.Range(0, b.Width).Count(x => b.GetPixel(x, y) == c)) / (double)(b.Width * b.Height);
    #endregion
}
