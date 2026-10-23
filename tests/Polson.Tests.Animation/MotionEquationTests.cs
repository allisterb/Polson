namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;
using Polson.Drawing.Skia;
using Xunit;

using static MotionCompositionTests;

/// <summary>
/// <c>comp.equation</c>: LaTeX typeset as filled outlines in a group of their own, upright in a y-down or a y-up view, and
/// listed by <c>comp.texts()</c> by its linear form with the seconds it is on the frame.
/// </summary>
public class MotionEquationTests : TestsRuntime
{
    #region Methods
    /// <summary>Where a frame has ink: the box of every dark pixel, and how much of it is in the box's upper right.</summary>
    private static (SkiaSharp.SKRectI Box, double UpperRightShare) Ink(SkiaCanvas frame)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        var dark = new List<(int X, int Y)>();
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                if (frame.SkBitmap.GetPixel(x, y) is { Alpha: > 128, Red: < 128 })
                {
                    dark.Add((x, y));
                    (l, t, r, b) = (Math.Min(l, x), Math.Min(t, y), Math.Max(r, x), Math.Max(b, y));
                }
        if (dark.Count == 0) return (SkiaSharp.SKRectI.Empty, 0);
        var (cx, cy) = ((l + r) / 2, (t + b) / 2);
        var upperRight = dark.Count(p => p.X > cx + (r - cx) / 2 && p.Y < cy);
        var lowerRight = dark.Count(p => p.X > cx + (r - cx) / 2 && p.Y >= cy);
        return (new SkiaSharp.SKRectI(l, t, r, b), upperRight / (double)Math.Max(1, upperRight + lowerRight));
    }

    /// <summary><c>x^2</c>, centred on its origin: ink round the origin, the superscript up and to the right in either view.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TestAnEquationIsDrawnUprightWhereItIsPlaced(bool yUp)
    {
        var motion = new MotionToolkit();
        var comp = yUp
            ? motion.Composition(Opts(("width", 320d), ("height", 180d), ("fps", 12d), ("duration", 1d), ("view", new[] { -4d, 2.25d, 4d, -2.25d })))
            : motion.Composition(Opts(("width", 320d), ("height", 180d), ("fps", 12d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        var eq = comp.Equation(Opts(("latex", "x^2"), ("size", yUp ? 2d : 80d), ("origin", yUp ? new[] { 0d, 0d } : new[] { 160d, 90d })));

        Assert.Equal("x²", eq.Text);
        var (box, upperRight) = Ink(comp.Render(0));
        Assert.False(box.IsEmpty);
        Assert.InRange((box.Left + box.Right) / 2, 140, 180);          // centred on the origin (the frame's centre, either way)
        Assert.InRange((box.Top + box.Bottom) / 2, 70, 110);
        Assert.True(upperRight > 0.6, $"the exponent is not upper right: {upperRight:0.00} of the right-hand ink is above the middle");
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"polson-equation-{(yUp ? "yup" : "ydown")}.png"), comp.Render(0).ToImageBytes("png", 100));
    }

    /// <summary>Listed with its linear form and kind, on the frame while its group shows, gone when a group above fades it.</summary>
    [Fact]
    public void TestAnEquationIsListedByTextsWhileItShows()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 320d), ("height", 180d), ("fps", 24d), ("duration", 3d)));
        var scene = comp.Group(Opts(("amount", motion.Nodes.Animated("real", new object[]
            { Opts(("time", 1.95d), ("value", 1d)), Opts(("time", 2d), ("value", 0d)) }))));
        scene.Equation(Opts(("latex", @"\frac{1}{3}"), ("origin", new[] { 160d, 90d }), ("desc", "result")));
        comp.Text(Opts(("text", "TITLE"), ("family", "Arial"), ("size", 30d), ("origin", new[] { 160d, 30d })));

        var texts = comp.Texts().Cast<Dictionary<string, object?>>().ToArray();
        var eq = texts.Single(t => (string)t["kind"]! == "equation");
        Assert.Equal("1/3", eq["text"]);
        Assert.Equal("result", eq["desc"]);
        Assert.Equal(0d, (double)eq["from"]!);
        Assert.InRange((double)eq["to"]!, 1.9, 1.96);
        Assert.Equal("text", texts.Single(t => (string)t["text"]! == "TITLE")["kind"]);
    }

    [Fact]
    public void TestAnEquationExportsToSynfigAndRefusesWhatCannotBeTypeset()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 320d), ("height", 180d), ("fps", 12d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Equation(Opts(("latex", @"x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}"), ("origin", new[] { 160d, 90d }), ("size", 30d), ("color", "#1d3557")));
        Assert.Contains("type=\"region\"", comp.ToSif());
        var frame = comp.Render(0);
        Assert.Equal(SkiaSharp.SKColors.White, frame.SkBitmap.GetPixel(10, 10));                    // the fill, untouched
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "polson-equation-film.png"), frame.ToImageBytes("png", 100));

        Assert.Throws<ArgumentException>(() => comp.Equation(Opts(("latex", @"\frac{1}{2"))));
        Assert.Throws<ArgumentException>(() => comp.Equation(Opts(("latex", "x"),
            ("size", motion.Nodes.Animated("real", new object[] { Opts(("time", 0d), ("value", 10d)), Opts(("time", 1d), ("value", 40d)) })))));
    }
    #endregion
}
