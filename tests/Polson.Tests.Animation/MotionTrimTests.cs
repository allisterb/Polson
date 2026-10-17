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
/// Write-on (<c>trimStart</c>, <c>trimEnd</c>, <c>trimOffset</c> on an outline): the part drawn, a keyed write-on, a trim
/// wrapping past the end, a looped seam, a taper, the SVG route, and Synfig's renderer reading the advanced outline's dash.
/// </summary>
public class MotionTrimTests(ITestOutputHelper output) : TestsRuntime
{
    #region Fields
    private const string Wave = "M30 120 C80 20 160 20 210 80 S300 150 330 60";
    #endregion

    #region Methods
    /// <summary>A trim draws exactly the stretch of the path between its fractions: what Skia strokes of that segment.</summary>
    [Theory]
    [InlineData(0.25, 0.75, 0d)]
    [InlineData(0.0, 0.4, 0d)]
    [InlineData(0.0, 0.3, 0.85)]       // runs past the end and wraps to the start
    public void ATrimDrawsTheStretchBetweenItsFractions(double s, double e, double offset)
    {
        var comp = Scene();
        comp.Outline(Opts(("path", Wave), ("width", 10d), ("color", "#1d3557"), ("trimStart", s), ("trimEnd", e), ("trimOffset", offset)));

        using var ours = comp.Render(0);
        using var expected = Blank();
        using (var canvas = new SKCanvas(expected))
        using (var path = SKPath.ParseSvgPathData(Wave))
        using (var measure = new SKPathMeasure(path))
        using (var paint = new SKPaint { Color = SKColor.Parse("#1d3557"), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, StrokeCap = SKStrokeCap.Round })
        {
            var L = measure.Length;
            var a = ((s + offset) % 1) * L;
            var len = (e - s) * L;
            // Each piece into a path of its own: SkiaSharp's GetSegment replaces its destination.
            using var head = new SKPath();
            measure.GetSegment((float)a, (float)Math.Min(L, a + len), head, true);
            canvas.DrawPath(head, paint);
            if (a + len > L)
            {
                using var tail = new SKPath();
                measure.GetSegment(0, (float)(a + len - L), tail, true);
                canvas.DrawPath(tail, paint);
            }
        }
        var differing = Differing(ours.SkBitmap, expected, 24);
        output.WriteLine($"{s}..{e} +{offset}: {differing:P3} differ");
        Assert.True(differing < 0.001);
    }

    [Fact]
    public void AKeyedTrimEndWritesTheLineOn()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion, 1);
        comp.Outline(Opts(("points", new object[] { new[] { 20d, 80d }, new[] { 340d, 80d } }), ("width", 8d), ("color", "#000000"),
            ("trimEnd", motion.Nodes.Linear("real", 1d, 0d))));

        using var at0 = comp.Render(0);
        using var half = comp.Render(0.5);
        using var at1 = comp.Render(1);
        Assert.True(Ink(at0.SkBitmap) < 0.001, "something is drawn at trimEnd 0");
        Assert.True(Near(half.SkBitmap.GetPixel(150, 80), SKColors.Black) && !Near(half.SkBitmap.GetPixel(210, 80), SKColors.Black));
        Assert.True(Near(at1.SkBitmap.GetPixel(330, 80), SKColors.Black));
    }

    /// <summary>On a looped path a trim past the seam is one stroke round it, not two pieces.</summary>
    [Fact]
    public void ALoopedTrimRunsRoundItsSeam()
    {
        var comp = Scene();
        comp.Outline(Opts(("path", "M100 40 H260 V130 H100 Z"), ("width", 6d), ("color", "#000000"), ("roundTips", false),
            ("trimStart", 0d), ("trimEnd", 0.2), ("trimOffset", 0.9)));

        using var frame = comp.Render(0);
        Assert.True(Near(frame.SkBitmap.GetPixel(100, 60), SKColors.Black), "the stretch before the seam is missing");
        Assert.True(Near(frame.SkBitmap.GetPixel(140, 40), SKColors.Black), "the stretch after the seam is missing");
        Assert.False(Near(frame.SkBitmap.GetPixel(260, 80), SKColors.Black));
    }

    [Fact]
    public void ATrimWithNoLengthDrawsNothing()
    {
        var comp = Scene();
        comp.Outline(Opts(("path", Wave), ("width", 10d), ("color", "#000000"), ("trimStart", 0.6), ("trimEnd", 0.4)));
        using var frame = comp.Render(0);
        Assert.True(Ink(frame.SkBitmap) < 0.0001);
    }

    /// <summary>A tapered outline trimmed keeps the taper of the part it shows: thin at the thin end, wide at the wide end.</summary>
    [Fact]
    public void ATaperTrimmedKeepsItsWidths()
    {
        var comp = Scene();
        comp.Outline(Opts(("points", new object[] { Opts(("point", new[] { 20d, 80d }), ("width", 0.2)), Opts(("point", new[] { 340d, 80d }), ("width", 2d)) }),
            ("width", 10d), ("color", "#000000"), ("trimStart", 0.25), ("trimEnd", 1d)));

        using var frame = comp.Render(0);
        int Thickness(int x) => Enumerable.Range(0, 160).Count(y => frame.SkBitmap.GetPixel(x, y).Red < 128);
        Assert.Equal(0, Thickness(60));                                  // trimmed away
        Assert.InRange(Thickness(110), 6, 9);                            // a quarter in: about 0.65 of 10
        Assert.InRange(Thickness(320), 18, 21);                          // the wide end
    }

    /// <summary>An even trim goes to SVG as a dash, drawn the same; a trimmed taper is refused.</summary>
    [Fact]
    public void AnEvenTrimGoesToSvg()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion, 1);
        comp.Outline(Opts(("path", Wave), ("width", 10d), ("color", "#1d3557"), ("trimEnd", motion.Nodes.Linear("real", 0.8, 0.1))));

        var svg = comp.ToSvg();
        Assert.Contains("stroke-dasharray", svg);
        foreach (var t in new[] { 0d, 0.5, 1d })
        {
            using var ours = comp.Render(t);
            using var theirs = SKBitmap.Decode(SvgRenderPipeline.RenderToImage(svg, comp.Width, comp.Height, "png", 100, null, TimeSpan.FromSeconds(t)));
            var differing = Differing(ours.SkBitmap, theirs, 24);
            output.WriteLine($"svg t {t}: {differing:P3} differ");
            Assert.True(differing < 0.005);
        }

        var tapered = Scene();
        tapered.Outline(Opts(("points", new object[] { Opts(("point", new[] { 20d, 80d }), ("width", 0.2)), Opts(("point", new[] { 340d, 80d }), ("width", 2d)) }),
            ("width", 10d), ("trimEnd", 0.5)));
        Assert.Contains("tapers and is trimmed", Assert.Throws<InvalidOperationException>(() => tapered.ToSvg()).Message);
    }

    /// <summary>
    /// Synfig draws the same from the advanced outline's dash: the probe's trims, a write-on keyed over the film, a trim
    /// wrapping past the end, one round a looped seam with flat tips, and a trimmed taper.
    /// </summary>
    [Theory]
    [InlineData("trims")]
    [InlineData("writeOn")]
    [InlineData("wrap")]
    [InlineData("loop")]
    [InlineData("taper")]
    public void SynfigTrimsTheSame(string what)
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        var comp = Scene(motion, 2);
        switch (what)
        {
            case "trims":
                comp.Outline(Opts(("path", Wave), ("width", 10d), ("color", "#1d3557"), ("trimStart", 0.1), ("trimEnd", 0.9)));
                break;
            case "writeOn":
                comp.Outline(Opts(("path", Wave), ("width", 10d), ("color", "#1d3557"), ("trimEnd", motion.Nodes.Linear("real", 0.45, 0.05))));
                break;
            case "wrap":
                comp.Outline(Opts(("path", Wave), ("width", 10d), ("color", "#1d3557"), ("trimEnd", 0.3), ("trimOffset", motion.Nodes.Linear("real", 0.1, 0.75))));
                break;
            case "loop":
                comp.Outline(Opts(("path", "M100 40 H260 V130 H100 Z"), ("width", 8d), ("color", "#1d3557"), ("roundTips", false),
                    ("trimEnd", 0.3), ("trimOffset", motion.Nodes.Linear("real", 0.05, 0.85))));
                break;
            default:
                comp.Outline(Opts(("points", new object[] { Opts(("point", new[] { 20d, 130d }), ("width", 0.3), ("t1", new[] { 200d, -200d })), Opts(("point", new[] { 340d, 40d }), ("width", 2d), ("t1", new[] { 200d, 0d })) }),
                    ("width", 10d), ("color", "#1d3557"), ("trimStart", 0.2), ("trimEnd", 0.85)));
                break;
        }

        var theirs = SynfigOracle.Render(synfig, comp, $"trim-{what}", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"trim-{what}-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"{what} frame {i}: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, bitmap, 8):P2} by more than 8");
            Assert.True(differing < 0.01, $"{differing:P2} differ from synfig at frame {i}");
        }
    }

    private static MotionComposition Scene(MotionToolkit? motion = null, double duration = 0)
    {
        var comp = (motion ?? new MotionToolkit()).Composition(Opts(("width", 360d), ("height", 160d), ("fps", 1d), ("duration", duration)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }

    private static SKBitmap Blank()
    {
        var b = new SKBitmap(360, 160);
        b.Erase(SKColors.White);
        return b;
    }

    private static double Ink(SKBitmap b) =>
        Enumerable.Range(0, b.Height).Sum(y => Enumerable.Range(0, b.Width).Count(x => b.GetPixel(x, y).Red < 200)) / (double)(b.Width * b.Height);
    #endregion
}
