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
/// The linear and radial gradient layers: colour along the gradient as Synfig's compiled gradient computes it — loop,
/// zigzag, premultiplied alpha — a gradient filling a shape, the refusals, the SVG route, and Synfig's renderer.
/// </summary>
public class MotionGradientTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    [Fact]
    public void ALinearGradientRunsBetweenItsPoints()
    {
        var comp = Scene();
        comp.LinearGradient(Opts(("point1", new[] { 40d, 0d }), ("point2", new[] { 160d, 0d }), ("stops", new object[] { "#000000", "#ffffff" })));

        using var frame = comp.Render(0);
        Assert.Equal(0, frame.SkBitmap.GetPixel(10, 50).Red);              // before point1: the first colour
        Assert.Equal(255, frame.SkBitmap.GetPixel(190, 50).Red);           // past point2: the last
        Assert.InRange(frame.SkBitmap.GetPixel(100, 50).Red, 125, 131);    // halfway
        Assert.InRange(frame.SkBitmap.GetPixel(70, 50).Red, 62, 67);       // a quarter
    }

    [Fact]
    public void StopsTakePositionsAndSitInOrder()
    {
        var comp = Scene();
        // Given out of order, and two at 0.5 for a hard edge: red to the middle, then blue.
        comp.LinearGradient(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 200d, 0d }),
            ("stops", new object[] { new object[] { 1d, "#0000ff" }, new object[] { 0.5d, "#ff0000" }, new object[] { 0.5d, "#0000ff" }, new object[] { 0d, "#ff0000" } })));

        using var frame = comp.Render(0);
        Assert.Equal(SKColors.Red, frame.SkBitmap.GetPixel(95, 50));
        Assert.Equal(SKColors.Blue, frame.SkBitmap.GetPixel(105, 50));
    }

    [Theory]
    [InlineData(false, false, 0.25, 64)]      // plain: a quarter of the way to white
    [InlineData(false, false, 1.25, 255)]     // past the end: clamped
    [InlineData(true, false, 1.25, 64)]       // looped: x − floor(x)
    [InlineData(false, true, 0.25, 128)]      // zigzag: out to white by the middle...
    [InlineData(false, true, 0.75, 128)]      // ...and back
    [InlineData(false, true, 1.25, 0)]        // and clamped to its last stop, black, beyond
    [InlineData(true, true, 1.25, 128)]       // zigzag looped: out and back again
    public void LoopAndZigzagMapDistanceAsSynfigDoes(bool loop, bool zigzag, double x, int red)
    {
        var comp = Scene();
        comp.LinearGradient(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 80d, 0d }), ("loop", loop), ("zigzag", zigzag)));

        using var frame = comp.Render(0);
        Assert.InRange(frame.SkBitmap.GetPixel((int)Math.Round(x * 80), 50).Red, red - 4, red + 4);
    }

    /// <summary>
    /// Synfig interpolates premultiplied colour: transparent to red is red fading in, not red darkening through black on
    /// the way. On white, halfway is pink; straight interpolation would draw it muddy.
    /// </summary>
    [Fact]
    public void AlphaFadesPremultipliedAsSynfigInterpolates()
    {
        var comp = Scene();
        comp.LinearGradient(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 200d, 0d }), ("stops", new object[] { "#00000000", "#ff0000" })));

        using var frame = comp.Render(0);
        var mid = frame.SkBitmap.GetPixel(100, 50);
        Assert.InRange((int)mid.Red, 250, 255);
        Assert.InRange((int)mid.Green, 122, 134);
    }

    [Fact]
    public void ARadialGradientRunsOutFromItsOrigin()
    {
        var comp = Scene();
        comp.RadialGradient(Opts(("origin", new[] { 100d, 50d }), ("radius", 40d), ("stops", new object[] { "#ffffff", "#000000" })));

        using var frame = comp.Render(0);
        Assert.InRange(frame.SkBitmap.GetPixel(100, 50).Red, 248, 255);     // the pixel's centre is 0.7 from the origin
        Assert.InRange(frame.SkBitmap.GetPixel(120, 50).Red, 124, 132);
        Assert.InRange(frame.SkBitmap.GetPixel(100, 70).Red, 124, 132);
        Assert.Equal(0, frame.SkBitmap.GetPixel(150, 50).Red);
    }

    /// <summary>The documented way to fill a shape: the gradient in a group with the shape, blended onto it, and nowhere else.</summary>
    [Fact]
    public void AGradientBlendedOntoAShapeFillsOnlyTheShape()
    {
        var comp = Scene();
        var head = comp.Group();
        head.Ellipse(Opts(("origin", new[] { 100d, 50d }), ("radius", new[] { 60d, 30d }), ("color", "#000000")));
        head.LinearGradient(Opts(("point1", new[] { 40d, 0d }), ("point2", new[] { 160d, 0d }), ("blend", "onto"),
            ("stops", new object[] { "#8a6a00", "#fff3b0", "#8a6a00" })));

        using var frame = comp.Render(0);
        Assert.Equal(SKColors.White, frame.SkBitmap.GetPixel(10, 10));           // outside the shape: the page
        Assert.True(Near(frame.SkBitmap.GetPixel(100, 50), SKColor.Parse("#fff3b0")));
        Assert.True(Near(frame.SkBitmap.GetPixel(50, 50), SKColor.Parse("#9a7c1a")));
    }

    [Fact]
    public void KeyedPointsSweepTheGradient()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        comp.LinearGradient(Opts(("point1", motion.Nodes.Linear("vector", new[] { 100d, 0d }, new[] { 0d, 0d })),
            ("point2", motion.Nodes.Linear("vector", new[] { 100d, 0d }, new[] { 100d, 0d }))));

        using var at0 = comp.Render(0);
        using var at1 = comp.Render(1);
        Assert.InRange(at0.SkBitmap.GetPixel(50, 50).Red, 124, 132);
        Assert.Equal(0, at1.SkBitmap.GetPixel(50, 50).Red);
    }

    [Theory]
    [InlineData("empty", "stops are colours")]
    [InlineData("pos", "a stop sits from 0 to 1")]
    [InlineData("shape", "is a colour, [pos, color]")]
    [InlineData("colour", "CSS colour")]
    public void BadStopsAreRefusedWithWhatIsWrong(string what, string message)
    {
        object stops = what switch
        {
            "empty" => Array.Empty<object>(),
            "pos" => new object[] { new object[] { 1.5d, "#000000" } },
            "shape" => new object[] { 3d },
            _ => new object[] { "#000000", "notacolour" },
        };
        var e = Assert.Throws<ArgumentException>(() => Scene().LinearGradient(Opts(("stops", stops))));
        Assert.Contains(message, e.Message);
    }

    /// <summary>The SVG route: a gradient in defs with its stops, its points animated, drawn the same by Svg.Skia.</summary>
    [Fact]
    public void ToSvgWritesAGradientInDefs()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        comp.LinearGradient(Opts(("point1", new[] { 40d, 0d }), ("point2", motion.Nodes.Linear("vector", new[] { 60d, 0d }, new[] { 100d, 0d })),
            ("stops", new object[] { "#1d3557", "#f4a261" })));
        comp.RadialGradient(Opts(("origin", new[] { 150d, 50d }), ("radius", 30d), ("blend", "multiply"), ("amount", 0.5), ("loop", true),
            ("stops", new object[] { "#e63946", "#ffffff" })));

        var svg = comp.ToSvg();
        Assert.Contains("<linearGradient", svg);
        Assert.Contains("<radialGradient", svg);
        Assert.Contains("spreadMethod=\"repeat\"", svg);
        Assert.Contains("attributeName=\"x2\"", svg);
        foreach (var t in new[] { 0d, 0.5, 1d })
        {
            using var ours = comp.Render(t);
            using var theirs = SKBitmap.Decode(SvgRenderPipeline.RenderToImage(svg, comp.Width, comp.Height, "png", 100, null, TimeSpan.FromSeconds(t)));
            var differing = Differing(ours.SkBitmap, theirs, 8);
            output.WriteLine($"t {t}: {differing:P2} differ by more than 8");
            Assert.True(differing < 0.01, $"{differing:P2} differ at {t}");
        }
    }

    [Fact]
    public void ToSifWritesSynfigsGradientLayers()
    {
        var comp = Scene();
        comp.LinearGradient(Opts(("point1", new[] { 1d, 2d }), ("point2", new[] { 3d, 4d }), ("zigzag", true),
            ("stops", new object[] { new object[] { 0d, "#ff0000" }, new object[] { 0.25d, "#0000ff80" } })));
        comp.RadialGradient(Opts(("origin", new[] { 5d, 6d }), ("radius", 7d), ("loop", true)));

        var sif = comp.ToSif();
        Assert.Contains("type=\"linear_gradient\"", sif);
        Assert.Contains("type=\"radial_gradient\"", sif);
        Assert.Contains("<color pos=\"0.25\">", sif.Replace("\r", ""));
        Assert.Contains("name=\"center\"", sif);
        Assert.Contains("name=\"p2\"", sif);
    }

    /// <summary>
    /// Synfig draws the same: a radial gradient, a gradient onto a region with a keyed sweep, and one fading
    /// premultiplied — gradients whose colour runs straight between two stops, where Synfig's averaging changes nothing.
    /// </summary>
    [Fact]
    public void SynfigDrawsGradientsTheSame()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 240d), ("height", 160d), ("fps", 1d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.RadialGradient(Opts(("origin", new[] { 60d, 50d }), ("radius", 70d), ("amount", 0.8),
            ("stops", new object[] { "#1d3557", "#a8dadc" })));
        var panel = comp.Group();
        panel.Region(Opts(("path", "M130 10 H230 V90 H130 Z"), ("color", "#000000")));
        panel.LinearGradient(Opts(("blend", "onto"), ("stops", new object[] { "#e63946", "#f1faee" }),
            ("point1", motion.Nodes.Linear("vector", new[] { 20d, 0d }, new[] { 130d, 0d })),
            ("point2", motion.Nodes.Linear("vector", new[] { 20d, 0d }, new[] { 230d, 0d }))));
        comp.LinearGradient(Opts(("point1", new[] { 0d, 100d }), ("point2", new[] { 0d, 160d }), ("stops", new object[] { "#2a9d8f00", "#2a9d8f" })));

        var theirs = SynfigOracle.Render(synfig, comp, "gradient", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"gradient-{i}");
            // Within 8 levels everywhere; a few per cent of pixels are 3–8 levels apart, rounding in the two renderers' 8-bit stops.
            var differing = Differing(ours.SkBitmap, bitmap, 8);
            output.WriteLine($"frame {i}: {Differing(ours.SkBitmap, bitmap, 4):P2} differ by more than 4, {differing:P2} by more than 8");
            Assert.True(differing < 0.001, $"{differing:P2} differ from synfig by more than 8 at frame {i}");
        }
    }

    /// <summary>
    /// What the remarks on <see cref="MotionGradientLayer"/> claim of Synfig, measured: its linear layer averages each
    /// pixel over ±0.1 of the gradient's length when the file's units are pixels, so a hard stop halfway along a 200 px
    /// gradient becomes a ramp about 40 px wide there. Ours stays a hard edge. A Synfig that fixes this fails here, and
    /// the remarks and the SDK reference then say less than they should.
    /// </summary>
    [Fact]
    public void SynfigSoftensALinearHardStopOverAFifthOfTheGradient()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var comp = new MotionToolkit().Composition(Opts(("width", 200d), ("height", 20d), ("fps", 1d), ("duration", 0d)));
        comp.LinearGradient(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 200d, 0d }),
            ("stops", new object[] { new object[] { 0d, "#000000" }, new object[] { 0.5d, "#000000" }, new object[] { 0.5d, "#ffffff" }, new object[] { 1d, "#ffffff" } })));

        using var theirs = Assert.Single(SynfigOracle.Render(synfig, comp, "hard-stop", output));
        using var ours = comp.Render(0);
        int Ramp(SKBitmap b) => Enumerable.Range(0, 200).Count(x => b.GetPixel(x, 10).Red is > 12 and < 243);
        output.WriteLine($"ramp: ours {Ramp(ours.SkBitmap)} px, synfig {Ramp(theirs)} px");
        Assert.InRange(Ramp(ours.SkBitmap), 0, 2);
        Assert.InRange(Ramp(theirs), 30, 44);
    }

    private static MotionComposition Scene(MotionToolkit? motion = null)
    {
        var comp = (motion ?? new MotionToolkit()).Composition(Opts(("width", 200d), ("height", 100d), ("fps", 1d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }
    #endregion
}
