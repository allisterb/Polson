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
/// <c>comp.rectangle({ radius })</c>: Synfig's rectangle bevel, drawn as Synfig draws it (a quadratic at each corner), clamped
/// to a pill, against Synfig's renderer fixed and keyed, and out to SVG as the same path. trailer4 wrote its poster's
/// rounded rectangles as path data by hand, with <c>Q</c> corners.
/// </summary>
public class MotionRoundedRectangleTests(ITestOutputHelper output) : TestsRuntime
{
    #region Fields
    private const double W = 240, H = 160;
    #endregion

    #region Methods
    [Theory]
    [InlineData(18d, false)]
    [InlineData(18d, true)]
    public void SynfigRoundsTheCornersTheSame(double radius, bool keyed)
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", W), ("height", H), ("fps", 1d), ("duration", keyed ? 2d : 0d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Rectangle(Opts(("point1", new[] { 30d, 30d }), ("point2", new[] { 210d, 130d }), ("color", "#1d3557"),
            ("radius", keyed ? motion.Nodes.Linear("real", radius, 0d) : radius)));
        // A small card whose radius asks for more than half its height: it comes out a pill.
        comp.Rectangle(Opts(("point1", new[] { 60d, 60d }), ("point2", new[] { 180d, 100d }), ("color", "#e9c46a"), ("radius", 40d)));

        var sif = comp.ToSif();
        Assert.Contains("name=\"bevel\"", sif);

        if (SynfigOracle.Find() is not { } synfig) return;
        var theirs = SynfigOracle.Render(synfig, comp, $"rounded-{(keyed ? "keyed" : "fixed")}", output);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"rounded-{(keyed ? "keyed" : "fixed")}-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"radius {radius}{(keyed ? " keyed from 0" : "")} frame {i}: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, bitmap, 8):P2} by more than 8");
            Assert.True(differing < 0.005, $"{differing:P2} differ from synfig at frame {i}");
        }
    }

    /// <summary>The corner is rounded, and a radius past half the shorter side stops at a pill.</summary>
    [Fact]
    public void ACornerIsRoundAndAPillIsTheLimit()
    {
        var comp = new MotionToolkit().Composition(Opts(("width", W), ("height", H)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Rectangle(Opts(("point1", new[] { 20d, 20d }), ("point2", new[] { 220d, 60d }), ("color", "#000000"), ("radius", 500d)));
        comp.Rectangle(Opts(("point1", new[] { 20d, 90d }), ("point2", new[] { 120d, 140d }), ("color", "#000000"), ("radius", 15d)));

        using var frame = comp.Render(0);
        var b = frame.SkBitmap;
        Assert.Equal(SKColors.White, b.GetPixel(21, 21));          // the pill's corner is open
        Assert.Equal(SKColors.Black, b.GetPixel(40, 40));          // its middle is filled, the radius held to 20
        Assert.Equal(SKColors.White, b.GetPixel(21, 91));          // the rounded card's corner is open
        Assert.Equal(SKColors.Black, b.GetPixel(70, 115));
    }

    /// <summary>To SVG as a path with the same quadratic corners, not <c>rx</c>; Svg.Skia draws it as we do. Radius 0 stays a rect.</summary>
    [Fact]
    public void TheSvgRouteWritesTheSameCorners()
    {
        var comp = new MotionToolkit().Composition(Opts(("width", W), ("height", H)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Rectangle(Opts(("point1", new[] { 30d, 30d }), ("point2", new[] { 210d, 130d }), ("color", "#1d3557"), ("radius", 24d)));
        comp.Rectangle(Opts(("point1", new[] { 100d, 60d }), ("point2", new[] { 140d, 100d }), ("color", "#e63946")));

        var svg = comp.ToSvg();
        Assert.Contains("Q", svg);
        Assert.DoesNotContain("rx=", svg);
        Assert.Contains("<rect", svg);

        using var ours = comp.Render(0);
        using var theirs = SKBitmap.Decode(SvgRenderPipeline.RenderToImage(svg, comp.Width, comp.Height, "png", 100, null, TimeSpan.Zero));
        var differing = Differing(ours.SkBitmap, theirs, 24);
        output.WriteLine($"svg: {differing:P2} differ by more than 24");
        Assert.True(differing < 0.005, $"{differing:P2} differ from Svg.Skia's render");
    }
    #endregion
}
