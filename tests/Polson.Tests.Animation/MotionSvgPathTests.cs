namespace Polson.Tests.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// SVG path data as regions and outlines (<c>{ path }</c>, <c>Motion.pathPoints</c>), and the ellipse: each drawn against
/// Skia drawing the same SVG directly, and against Synfig's renderer where it is present.
/// </summary>
public class MotionSvgPathTests(ITestOutputHelper output) : TestsRuntime
{
    #region Fields
    // Every kind of segment: lines, a quadratic, a cubic, an arc, a relative move, and a subpath with no Z. The arc's
    // ellipse overlaps the first shape wound the other way, so nonzero leaves the overlap empty and the fill goes through
    // Skia's Simplify — whose own arcs, re-drawn as short quadratics, are 0.09% off the exact path and ours 0.18%.
    private const string Mixed = "M20 20 L90 20 Q120 40 90 70 C70 90 40 90 30 70 Z m80 30 a25 15 0 1 0 50 0 a25 15 0 1 0 -50 0 Z M150 20 L190 20 L170 50";

    // An outer square and an inner one wound the same way: nonzero fills the middle, evenodd leaves it empty.
    private const string SameWay = "M20 20 H140 V140 H20 Z M50 50 H110 V110 H50 Z";

    // The same with the inner square wound the other way: a hole under either rule.
    private const string OtherWay = "M20 20 H140 V140 H20 Z M50 50 V110 H110 V50 Z";

    // A ring with an island in its hole, and a second shape beside it.
    private const string Nested = "M20 20 H140 V140 H20 Z M40 40 V120 H120 V40 Z M65 65 H95 V95 H65 Z M150 60 H190 V100 H150 Z";
    #endregion

    #region Methods
    [Theory]
    [InlineData(Mixed, "nonzero")]
    [InlineData(SameWay, "nonzero")]
    [InlineData(SameWay, "evenodd")]
    [InlineData(OtherWay, "nonzero")]
    [InlineData(OtherWay, "evenodd")]
    [InlineData(Nested, "nonzero")]
    [InlineData(Nested, "evenodd")]
    public void ARegionFromAPathFillsWhatSkiaFillsFromIt(string d, string rule)
    {
        var comp = Scene();
        comp.Region(Opts(("path", d), ("fillRule", rule), ("color", "#1d3557")));

        using var ours = comp.Render(0);
        using var theirs = Reference(d, rule, invert: false);
        SynfigOracle.Dump(ours.SkBitmap, theirs, $"path-{rule}-{d.Length}");
        var differing = Differing(ours.SkBitmap, theirs, 24);
        output.WriteLine($"{rule}: {differing:P3} differ");
        Assert.True(differing < 0.002, $"{differing:P3} of pixels differ");
    }

    [Theory]
    [InlineData(OtherWay, "nonzero")]
    [InlineData(Nested, "evenodd")]
    [InlineData(Mixed, "nonzero")]
    public void AnInvertedRegionFromAPathFillsEverythingElse(string d, string rule)
    {
        var comp = Scene();
        comp.Region(Opts(("path", d), ("fillRule", rule), ("color", "#1d3557"), ("invert", true)));

        using var ours = comp.Render(0);
        using var theirs = Reference(d, rule, invert: true);
        Assert.True(Differing(ours.SkBitmap, theirs, 24) < 0.002);
    }

    /// <summary>A region of several contours is one group, so a fade fades the whole shape once rather than each piece over the others.</summary>
    [Fact]
    public void ARegionOfSeveralContoursFadesAsOne()
    {
        var comp = Scene();
        comp.Region(Opts(("path", "M20 20 H100 V100 H20 Z M60 60 H140 V140 H60 Z"), ("color", "#000000"), ("amount", 0.5)));

        using var frame = comp.Render(0);
        var overlap = frame.SkBitmap.GetPixel(80, 80);
        var single = frame.SkBitmap.GetPixel(30, 30);
        Assert.Equal(single, overlap);
    }

    [Fact]
    public void AnOutlineFromAPathStrokesWhatSkiaStrokes()
    {
        var comp = Scene();
        comp.Outline(Opts(("path", Mixed), ("color", "#1d3557"), ("width", 4d)));

        using var ours = comp.Render(0);
        using var path = SKPath.ParseSvgPathData(Mixed);
        using var theirs = Blank();
        using (var canvas = new SKCanvas(theirs))
        using (var paint = new SKPaint { Color = SKColor.Parse("#1d3557"), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Miter })
            canvas.DrawPath(path, paint);

        var differing = Differing(ours.SkBitmap, theirs, 24);
        output.WriteLine($"{differing:P3} differ");
        Assert.True(differing < 0.003, $"{differing:P3} of pixels differ");
    }

    /// <summary>A line keeps zero tangents, a curve keeps its controls, and a path that returns to its start before Z closes on that point.</summary>
    [Fact]
    public void PathPointsKeepCornersCurvesAndTheClosingPoint()
    {
        var contours = new MotionToolkit().PathPoints("M0 0 L30 0 Q60 0 60 30 L0 0 Z M100 0 L130 0");
        Assert.Equal(2, contours.Count);

        var first = contours[0];
        Assert.True((bool)first["loop"]!);
        var points = (List<Dictionary<string, object?>>)first["points"]!;
        Assert.Equal(3, points.Count);                                    // the returning L0 0 merged into the start
        Assert.Equal(new[] { 0d, 0d }, (double[])points[0]["t2"]!);       // a line leaves the start
        Assert.Equal(new[] { 0d, 0d }, (double[])points[1]["t1"]!);       // and arrives at the corner straight
        Assert.Equal(new[] { 60d, 0d }, (double[])points[1]["t2"]!);      // the quadratic's control, 2/3 of the way, times 3
        Assert.Equal(new[] { 0d, 60d }, (double[])points[2]["t1"]!);
        Assert.Equal(new[] { 0d, 0d }, (double[])points[0]["t1"]!);       // the closing line arrives straight

        Assert.False((bool)contours[1]["loop"]!);
    }

    [Theory]
    [InlineData(SameWay, "nonzero", false)]
    [InlineData(SameWay, "evenodd", true)]
    [InlineData(OtherWay, "nonzero", true)]
    public void PathPointsMarkHolesByTheFillRule(string d, string rule, bool hole)
    {
        var contours = new MotionToolkit().PathPoints(d, Opts(("fillRule", rule)));
        Assert.False((bool)contours[0]["hole"]!);
        Assert.Equal(hole, (bool)contours[1]["hole"]!);
    }

    /// <summary>The points a script gets back draw the same region the path does, so it can change one and keep the rest.</summary>
    [Fact]
    public void PathPointsFeedARegion()
    {
        var d = "M20 20 C80 0 140 60 140 140 L20 140 Z";
        var a = Scene();
        a.Region(Opts(("path", d), ("color", "#e63946")));
        var b = Scene();
        var contour = new MotionToolkit().PathPoints(d)[0];
        b.Region(Opts(("points", contour["points"]), ("loop", contour["loop"]), ("color", "#e63946")));

        using var x = a.Render(0);
        using var y = b.Render(0);
        Assert.Equal(0, Differing(x.SkBitmap, y.SkBitmap, 0));
    }

    [Theory]
    [InlineData("points-and-path", "points or path, not both")]
    [InlineData("loop", "drop loop")]
    [InlineData("rule-alone", "fillRule goes with path")]
    [InlineData("bad-rule", "'nonzero' or 'evenodd'")]
    [InlineData("garbage", "could not be read")]
    [InlineData("move-only", "draws nothing")]
    public void APathIsRefusedWithWhatIsWrong(string what, string message)
    {
        var options = what switch
        {
            "points-and-path" => Opts(("path", SameWay), ("points", new object[] { new[] { 0d, 0d }, new[] { 1d, 1d } })),
            "loop" => Opts(("path", SameWay), ("loop", false)),
            "rule-alone" => Opts(("points", new object[] { new[] { 0d, 0d }, new[] { 1d, 1d } }), ("fillRule", "evenodd")),
            "bad-rule" => Opts(("path", SameWay), ("fillRule", "oddeven")),
            "garbage" => Opts(("path", "M 10 banana")),
            _ => Opts(("path", "M10 10")),
        };

        var e = Assert.Throws<ArgumentException>(() => Scene().Region(options));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void AnEllipseFillsWhatSkiaFillsAsAnOval()
    {
        var comp = Scene();
        comp.Ellipse(Opts(("origin", new[] { 100d, 80d }), ("radius", new[] { 70d, 30d }), ("angle", 30d), ("color", "#2a9d8f")));

        using var ours = comp.Render(0);
        using var theirs = Blank();
        using (var canvas = new SKCanvas(theirs))
        using (var paint = new SKPaint { Color = SKColor.Parse("#2a9d8f"), IsAntialias = true })
        {
            canvas.Translate(100, 80);
            canvas.RotateDegrees(30);
            canvas.DrawOval(0, 0, 70, 30, paint);
        }

        var differing = Differing(ours.SkBitmap, theirs, 24);
        output.WriteLine($"{differing:P3} differ");
        Assert.True(differing < 0.002, $"{differing:P3} of pixels differ");
    }

    [Fact]
    public void AnEllipseTakesANumberForACircleAndAKeyedVector()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        comp.Ellipse(Opts(("origin", new[] { 100d, 80d }), ("radius", 20d), ("color", "#000000")));
        comp.Ellipse(Opts(("origin", new[] { 40d, 40d }), ("color", "#e63946"),
            ("radius", motion.Nodes.Linear("vector", new[] { 20d, 0d }, new[] { 10d, 10d }))));

        using var frame = comp.Render(0);
        Assert.True(Near(frame.SkBitmap.GetPixel(100, 99), SKColors.Black));
        Assert.True(Near(frame.SkBitmap.GetPixel(119, 80), SKColors.Black) || Near(frame.SkBitmap.GetPixel(118, 80), SKColors.Black));
        Assert.False(Near(frame.SkBitmap.GetPixel(100, 102), SKColors.Black));

        // Growing 20 a second in x only: wide at one second, as tall as it started.
        using var later = comp.Render(1);
        Assert.True(Near(later.SkBitmap.GetPixel(68, 40), SKColor.Parse("#e63946")));
        Assert.False(Near(later.SkBitmap.GetPixel(40, 53), SKColor.Parse("#e63946")));
    }

    [Fact]
    public void AnEllipseRefusesARoundNodeForItsRadius()
    {
        var motion = new MotionToolkit();
        var e = Assert.Throws<ArgumentException>(() => Scene(motion).Ellipse(Opts(("radius", motion.Nodes.Linear("real", 1d, 10d)))));
        Assert.Contains("comp.circle", e.Message);
    }

    /// <summary>
    /// Synfig draws the same: a path of every segment kind with a ring and an island, an inverted one, and a turned
    /// ellipse growing over three frames. Synfig drops a layer it cannot read without a word, so its frame must also
    /// differ from the empty scene.
    /// </summary>
    [Fact]
    public void SynfigDrawsPathsAndEllipsesTheSame()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 200d), ("height", 160d), ("fps", 1d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Region(Opts(("path", Nested), ("fillRule", "evenodd"), ("color", "#1d3557")));
        comp.Region(Opts(("path", Mixed), ("color", "#e63946"), ("amount", 0.7)));
        var g = comp.Group(Opts(("offset", new[] { 0d, 100d }), ("scale", new[] { 0.3d, 0.3d })));
        g.Region(Opts(("path", OtherWay), ("color", "#2a9d8f"), ("invert", true)));
        comp.Ellipse(Opts(("origin", new[] { 150d, 120d }), ("angle", 25d), ("color", "#f4a261"),
            ("radius", motion.Nodes.Linear("vector", new[] { 10d, 4d }, new[] { 20d, 8d }))));

        var theirs = SynfigOracle.Render(synfig, comp, "svg-path", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        var empty = motion.Composition(Opts(("width", 200d), ("height", 160d), ("fps", 1d), ("duration", 2d)));
        empty.Fill(Opts(("color", "#f2efe8")));
        using var blank = empty.Render(0);

        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"svg-path-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"frame {i}: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, bitmap, 2):P2} by more than 2");
            Assert.True(Differing(blank.SkBitmap, bitmap, 24) > 0.1, $"synfig's frame {i} is nearly empty: did it drop a layer?");
            Assert.True(differing < 0.01, $"{differing:P2} differ from synfig at frame {i}");
        }
    }

    private static MotionComposition Scene(MotionToolkit? motion = null)
    {
        var comp = (motion ?? new MotionToolkit()).Composition(Opts(("width", 200d), ("height", 160d), ("fps", 1d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }

    private static SKBitmap Blank()
    {
        var bitmap = new SKBitmap(200, 160);
        bitmap.Erase(SKColors.White);
        return bitmap;
    }

    private static SKBitmap Reference(string d, string rule, bool invert)
    {
        var bitmap = Blank();
        using var canvas = new SKCanvas(bitmap);
        using var path = SKPath.ParseSvgPathData(d);
        path.FillType = (rule, invert) switch
        {
            ("evenodd", false) => SKPathFillType.EvenOdd,
            ("evenodd", true) => SKPathFillType.InverseEvenOdd,
            (_, false) => SKPathFillType.Winding,
            _ => SKPathFillType.InverseWinding
        };
        using var paint = new SKPaint { Color = SKColor.Parse("#1d3557"), IsAntialias = true };
        canvas.DrawPath(path, paint);
        return bitmap;
    }
    #endregion
}
