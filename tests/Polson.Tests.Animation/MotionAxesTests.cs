namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;
using Xunit;

using static MotionCompositionTests;

/// <summary>
/// <c>comp.axes</c>: data mapped onto the composition, axes and labelled ticks drawn, and functions plotted, filled,
/// approximated and touched by tangents, all from layers the composition already has.
/// </summary>
public class MotionAxesTests : TestsRuntime
{
    #region Methods
    private static (MotionComposition Comp, MotionAxes Axes) Make(double duration = 1)
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 640d), ("height", 400d), ("fps", 12d), ("duration", duration)));
        comp.Fill(Opts(("color", "#ffffff")));
        var axes = comp.Axes(Opts(("x", new[] { -4d, 4d, 1d }), ("y", new[] { -2d, 2d, 1d }), ("origin", new[] { 320d, 200d }), ("size", new[] { 560d, 320d })));
        return (comp, axes);
    }

    private static bool Dark(Polson.Drawing.Skia.SkiaCanvas frame, double x, double y, int r = 3)
    {
        for (var dx = -r; dx <= r; dx++)
            for (var dy = -r; dy <= r; dy++)
            {
                var (px, py) = ((int)Math.Round(x) + dx, (int)Math.Round(y) + dy);
                if (px >= 0 && py >= 0 && px < frame.Width && py < frame.Height && frame.SkBitmap.GetPixel(px, py).Red < 200) return true;
            }
        return false;
    }

    [Fact]
    public void TestDataPointsMapOntoTheBoxAndTheAxesAreDrawnAndLabelled()
    {
        var (comp, axes) = Make();
        Assert.Equal(new[] { 40d, 360d }, axes.Point(-4, -2));
        Assert.Equal(new[] { 600d, 40d }, axes.Point(4, 2));
        Assert.Equal(new[] { 320d, 200d }, axes.Point(0, 0));

        var frame = comp.Render(0);
        Assert.True(Dark(frame, 100, 200, 1));                          // the x axis
        Assert.True(Dark(frame, 320, 80, 1));                           // the y axis
        var labels = comp.Texts().Cast<Dictionary<string, object?>>().Where(t => (string)t["kind"]! == "tick").Select(t => (string)t["text"]!).ToArray();
        Assert.Equal(["−4", "−3", "−2", "−1", "1", "2", "3", "4", "−2", "−1", "1", "2"], labels);
    }

    /// <summary>sin x passes through the points it should; 1/x is broken at its asymptote, not drawn through it.</summary>
    [Fact]
    public void TestAPlotPassesThroughItsPointsAndBreaksWhereItLeavesTheRange()
    {
        var (comp, axes) = Make();
        axes.Plot(Math.Sin, Opts(("color", "#000000")));
        axes.Plot(v => 1 / v, Opts(("color", "#000000"), ("range", new[] { -4d, 4d })));
        var frame = comp.Render(0);

        foreach (var v in new[] { -3.5, -1.57, 0.7, 2.2, 3.9 })
        {
            var p = axes.Point(v, Math.Sin(v));
            Assert.True(Dark(frame, p[0], p[1]), $"sin x is not drawn at x = {v}");
        }
        // Between the branches of 1/x near 0 the plot leaves the range: nothing joins (−0.1, −2) to (0.1, 2) across the middle.
        var middle = axes.Point(0.02, 1.4);
        Assert.False(Dark(frame, middle[0] + 30, middle[1], 1), "1/x was joined across its asymptote");
    }

    [Fact]
    public void TestRiemannRectanglesApproachTheIntegralAndATangentHasTheSlope()
    {
        var (_, axes) = Make();
        Assert.Equal(1.0 / 3, axes.Riemann(v => v * v, 0, 1, 500, Opts(("rule", "mid"))), 4);
        Assert.Equal(0.285, axes.Riemann(v => v * v, 0, 1, 10, Opts(("rule", "left"))), 6);
        axes.Area(v => v * v, 0, 1);
        var tangent = axes.Tangent(v => v * v, 1);
        Assert.Equal(2, (double)tangent["slope"]!, 5);
        Assert.Equal(1, (double)tangent["y"]!, 9);
    }

    /// <summary>A curve keyed from trimEnd 0 to 1 writes on: nothing of it at the start, all of it at the end.</summary>
    [Fact]
    public void TestACurveWritesOnByItsTrim()
    {
        var motion = new MotionToolkit();
        var (comp, axes) = Make(duration: 2);
        axes.Plot(Math.Sin, Opts(("color", "#000000"), ("trimEnd", motion.Nodes.Animated("real", new object[]
            { Opts(("time", 0d), ("value", 0d)), Opts(("time", 2d), ("value", 1d)) }))));
        var late = axes.Point(3.5, Math.Sin(3.5));
        Assert.False(Dark(comp.Render(0), late[0], late[1], 1));
        Assert.True(Dark(comp.Render(2), late[0], late[1]));
    }

    [Fact]
    public void TestAxesExportToSynfigAndRefuseWhatTheyCannotDraw()
    {
        var (comp, axes) = Make();
        axes.Plot(Math.Cos);
        Assert.Contains("type=\"outline\"", comp.ToSif());

        var motion = new MotionToolkit();
        var other = motion.Composition(Opts(("width", 100d), ("height", 100d), ("fps", 12d), ("duration", 1d)));
        Assert.Throws<ArgumentException>(() => other.Axes(Opts(("x", new[] { 1d, 0d, 1d }), ("y", new[] { 0d, 1d, 1d }), ("origin", new[] { 50d, 50d }), ("size", new[] { 80d, 80d }))));
        Assert.Throws<ArgumentException>(() => other.Axes(Opts(("x", new[] { 0d, 1000d, 1d }), ("y", new[] { 0d, 1d, 1d }), ("origin", new[] { 50d, 50d }), ("size", new[] { 80d, 80d }))));
        Assert.Throws<ArgumentException>(() => axes.Plot(v => 100 + v));                     // never inside the y range
        Assert.Throws<ArgumentException>(() => axes.Riemann(v => v, 0, 1, 0));
    }

    /// <summary>
    /// What each axis measures, typeset at its far end and listed as kind 'axis'; a tip at each far end when asked. Titles
    /// are drawn with the tick labels turned off, since they say what the axis is and the ticks only how far.
    /// </summary>
    [Fact]
    public void TestAxesCarryTitlesAndArrowTips()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 1280d), ("height", 720d), ("fps", 12d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        var axes = comp.Axes(Opts(("x", new[] { 0d, 10d, 2d }), ("y", new[] { -2d, 2d, 1d }), ("origin", new[] { 640d, 360d }), ("size", new[] { 800d, 400d }),
            ("color", "#000000"), ("xLabel", @"\text{Time}"), ("yLabel", @"\text{Amplitude}"), ("arrows", true)));
        var titles = comp.Texts().Cast<Dictionary<string, object?>>().Where(t => (string)t["kind"]! == "axis").Select(t => (string)t["text"]!).ToArray();
        Assert.Equal(["Time", "Amplitude"], titles);
        var tip = axes.Point(10, 0);
        Assert.True(Dark(comp.Render(0), tip[0] + 6, tip[1], 1), "no arrow tip past the x axis's end");

        var bare = motion.Composition(Opts(("width", 1280d), ("height", 720d), ("fps", 12d), ("duration", 1d)));
        bare.Fill(Opts(("color", "#ffffff")));
        bare.Axes(Opts(("x", new[] { 0d, 10d, 2d }), ("y", new[] { 0d, 2d, 1d }), ("origin", new[] { 640d, 360d }), ("size", new[] { 800d, 400d }),
            ("labels", false), ("xLabel", "t")));
        Assert.Equal(["axis"], bare.Texts().Cast<Dictionary<string, object?>>().Select(t => (string)t["kind"]!));
        Assert.False(Dark(bare.Render(0), axes.Point(10, 0)[0] + 6, 360, 1), "a tip drawn without arrows: true");
    }

    /// <summary>
    /// ftcexplainer4 dimmed its axes to #475569 and the tick labels went with them, at 2.4 : 1 on its ground: the read-back
    /// missed all eleven. Labels take their own labelColor, and a size below 16 px at 720 lines is refused.
    /// </summary>
    [Fact]
    public void TestTickLabelsTakeTheirOwnColourAndAReadableSize()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 1280d), ("height", 720d), ("fps", 12d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#0f172a")));
        comp.Axes(Opts(("x", new[] { 0d, 2d, 1d }), ("y", new[] { 0d, 4d, 1d }), ("origin", new[] { 640d, 360d }), ("size", new[] { 400d, 300d }),
            ("color", "#475569"), ("labelColor", "#ffffff"), ("labelSize", 24d)));
        var frame = comp.Render(0);
        var label = comp.Texts().Cast<Dictionary<string, object?>>().First(t => (string)t["kind"]! == "tick" && (string)t["text"]! == "2");
        Assert.NotNull(label["from"]);
        // The x axis label "2" sits under (840, 510); somewhere near it a pixel is near white, which the dim line colour is not.
        var bright = Enumerable.Range(500, 40).SelectMany(y => Enumerable.Range(825, 30).Select(x => frame.SkBitmap.GetPixel(x, y))).Max(c => c.Red);
        Assert.True(bright > 200, $"the brightest pixel near the label is {bright}");

        var e = Assert.Throws<ArgumentException>(() => comp.Axes(Opts(("x", new[] { 0d, 2d, 1d }), ("y", new[] { 0d, 4d, 1d }),
            ("origin", new[] { 640d, 360d }), ("size", new[] { 400d, 300d }), ("labelSize", 12d))));
        Assert.Contains("below 16", e.Message);
        comp.Axes(Opts(("x", new[] { 0d, 2d, 1d }), ("y", new[] { 0d, 4d, 1d }), ("origin", new[] { 640d, 360d }), ("size", new[] { 160d, 120d })));
        Assert.Contains(comp.Texts().Cast<Dictionary<string, object?>>(), t => (string)t["kind"]! == "tick");   // a small box's default is raised, not refused
    }

    /// <summary>
    /// ftcexplainer1 gave origin as the box's lower-left corner, [80, 480] for a 480 × 320 box in 1280 × 720, and every graph
    /// ran 160 px off the left. A box never wholly in the frame is refused, saying where it is and what the corner reading
    /// meant; axes that slide in, through a keyed group, are in the frame at the end and pass.
    /// </summary>
    [Fact]
    public void TestABoxNeverInTheFrameIsRefusedAndOneThatSlidesInIsNot()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 1280d), ("height", 720d), ("fps", 12d), ("duration", 2d)));
        var options = Opts(("x", new[] { 0d, 3d, 1d }), ("y", new[] { 0d, 5d, 1d }), ("origin", new[] { 80d, 480d }), ("size", new[] { 480d, 320d }));
        var e = Assert.Throws<ArgumentException>(() => comp.Group().Axes(options));
        Assert.Contains("off the left of the frame", e.Message);
        Assert.Contains("(-160, 320) to (320, 640)", e.Message);
        Assert.Contains("origin is [320, 320]", e.Message);
        comp.Axes(Opts(("x", new[] { 0d, 3d, 1d }), ("y", new[] { 0d, 5d, 1d }), ("origin", new[] { 320d, 320d }), ("size", new[] { 480d, 320d })));

        var sliding = comp.Group(Opts(("offset", motion.Nodes.Animated("vector", new object[]
            { Opts(("time", 0d), ("value", new[] { -1000d, 0d })), Opts(("time", 2d), ("value", new[] { 0d, 0d })) }))));
        sliding.Axes(Opts(("x", new[] { 0d, 3d, 1d }), ("y", new[] { 0d, 5d, 1d }), ("origin", new[] { 320d, 320d }), ("size", new[] { 480d, 320d })));
        var gone = comp.Group(Opts(("offset", new[] { -1000d, 0d })));
        Assert.Throws<ArgumentException>(() => gone.Axes(Opts(("x", new[] { 0d, 3d, 1d }), ("y", new[] { 0d, 5d, 1d }), ("origin", new[] { 320d, 320d }), ("size", new[] { 480d, 320d }))));
    }
    #endregion
}
