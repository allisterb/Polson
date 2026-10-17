namespace Polson.Tests.Animation;

using System;

using Polson.Animation;
using Polson.Drawing.Svg;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// The shadow (<c>comp.shadow</c>, Synfig's shade layer): behind the stack by default, offset and softened, inverted for
/// an inner shadow, idle at no amount, stopping at its group, the SVG route, and Synfig's renderer.
/// </summary>
public class MotionShadowTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    /// <summary>With no blur the shadow is the shape moved, at three quarters, behind the shape: the shape itself is untouched.</summary>
    [Fact]
    public void AHardShadowIsTheShapeMovedBehindIt()
    {
        var comp = Scene();
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 100d, 80d }), ("color", "#e63946")));
        g.Shadow(Opts(("offset", new[] { 20d, 15d }), ("size", 0d)));

        using var frame = comp.Render(0);
        Assert.Equal(SKColor.Parse("#e63946"), frame.SkBitmap.GetPixel(70, 50));         // the shape, untouched
        var shadow = frame.SkBitmap.GetPixel(110, 90);                                    // moved past its corner
        Assert.InRange((int)shadow.Red, 62, 66);                                          // black at 0.75 over white
        Assert.Equal(SKColors.White, frame.SkBitmap.GetPixel(130, 20));
    }

    [Fact]
    public void TheDefaultShadowIsSoftAndBehind()
    {
        var comp = Scene();
        var g = comp.Group();
        g.Circle(Opts(("origin", new[] { 80d, 60d }), ("radius", 30d), ("color", "#2a9d8f")));
        g.Shadow();

        using var frame = comp.Render(0);
        Assert.Equal(SKColor.Parse("#2a9d8f"), frame.SkBitmap.GetPixel(80, 60));
        var near = frame.SkBitmap.GetPixel(106, 86).Red;     // just past the rim on the shadow side
        var far = frame.SkBitmap.GetPixel(54, 34).Red;       // the other side: no shadow
        output.WriteLine($"shadow side {near}, other side {far}");
        Assert.True(near < 200 && far > 245);
    }

    /// <summary>Inverted and blended onto the shape it is an inner shadow: the shape darkens along the edge away from the offset, and nothing outside changes.</summary>
    [Fact]
    public void InvertedOntoIsAnInnerShadow()
    {
        var comp = Scene();
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 140d, 110d }), ("color", "#f1faee")));
        g.Shadow(Opts(("invert", true), ("blend", "onto"), ("offset", new[] { 8d, 8d }), ("size", 4d)));

        using var frame = comp.Render(0);
        Assert.True(frame.SkBitmap.GetPixel(44, 34).Red < 200, "the top-left inside edge is not shaded");
        Assert.Equal(SKColor.Parse("#f1faee"), frame.SkBitmap.GetPixel(120, 90));
        Assert.Equal(SKColors.White, frame.SkBitmap.GetPixel(20, 20));
    }

    [Fact]
    public void AShadowAtNoAmountChangesNothingAndStopsAtItsGroup()
    {
        var plain = Scene();
        plain.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 100d, 80d }), ("color", "#e63946")));
        var idle = Scene();
        idle.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 100d, 80d }), ("color", "#e63946")));
        idle.Shadow(Opts(("amount", 0d)));
        using (var a = plain.Render(0))
        using (var b = idle.Render(0))
            Assert.Equal(0, Differing(a.SkBitmap, b.SkBitmap, 0));

        var comp = Scene();
        comp.Rectangle(Opts(("point1", new[] { 120d, 20d }), ("point2", new[] { 160d, 60d }), ("color", "#1d3557")));   // outside the group
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 20d, 20d }), ("point2", new[] { 60d, 60d }), ("color", "#1d3557")));
        g.Shadow(Opts(("offset", new[] { 10d, 10d }), ("size", 0d)));
        using var frame = comp.Render(0);
        Assert.True(frame.SkBitmap.GetPixel(65, 65).Red < 100);
        Assert.Equal(SKColors.White, frame.SkBitmap.GetPixel(165, 65));
    }

    [Fact]
    public void ToSifWritesSynfigsShade()
    {
        var comp = Scene();
        comp.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 100d, 80d }), ("color", "#e63946")));
        comp.Shadow(Opts(("type", "gaussian")));
        var sif = comp.ToSif();
        Assert.Contains("type=\"shade\"", sif);
        Assert.Contains("name=\"origin\"", sif);
        Assert.Contains("<integer value=\"12\"", sif);    // behind
    }

    /// <summary>The default shadow goes to SVG as a filter under the stack and draws close to the frame; an inverted one is refused.</summary>
    [Fact]
    public void TheDefaultShadowGoesToSvg()
    {
        var comp = Scene();
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 100d, 80d }), ("color", "#e63946")));
        g.Shadow(Opts(("type", "gaussian"), ("size", 6d)));

        var svg = comp.ToSvg();
        Assert.Contains("feOffset", svg);
        using var ours = comp.Render(0);
        using var theirs = SKBitmap.Decode(SvgRenderPipeline.RenderToImage(svg, comp.Width, comp.Height, "png", 100, null, TimeSpan.Zero));
        var differing = Differing(ours.SkBitmap, theirs, 16);
        output.WriteLine($"{differing:P2} differ from the SVG by more than 16");
        Assert.True(differing < 0.02);

        var inner = Scene();
        inner.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 100d, 80d }), ("color", "#e63946")));
        inner.Shadow(Opts(("invert", true), ("blend", "onto")));
        Assert.Contains("cannot say", Assert.Throws<InvalidOperationException>(() => inner.ToSvg()).Message);
    }

    /// <summary>
    /// Synfig draws the same, for each blur kind with the offset keyed, an inner shadow, and a shadow composited over its
    /// stack. Synfig drops a layer it cannot read without a word, so its frame must also differ from the same scene with
    /// no shadow. <b>The inner shadow differs on purpose</b>: Synfig renders the inverted shade only within the bounding box
    /// of what is below, so an edge lying on that box — the rectangle's top and left here — gets no inner shadow in Synfig,
    /// while the circle, inside the box, does. Ours shades every edge alike; the two are compared on the circle, and
    /// Synfig's missing shade on the rectangle is pinned so a change in Synfig shows.
    /// </summary>
    [Theory]
    [InlineData("fastGaussian", false, "behind")]
    [InlineData("gaussian", false, "behind")]
    [InlineData("box", false, "behind")]
    [InlineData("cross", false, "behind")]
    [InlineData("gaussian", true, "onto")]
    [InlineData("fastGaussian", false, "composite")]
    public void SynfigShadesTheSame(string type, bool invert, string blend)
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        MotionComposition Build(bool shadow)
        {
            var comp = motion.Composition(Opts(("width", 240d), ("height", 160d), ("fps", 1d), ("duration", 2d)));
            comp.Fill(Opts(("color", "#f2efe8")));
            var g = comp.Group();
            g.Rectangle(Opts(("point1", new[] { 40d, 30d }), ("point2", new[] { 120d, 100d }), ("color", "#e9c46a")));
            g.Circle(Opts(("origin", new[] { 170d, 80d }), ("radius", 35d), ("color", "#264653")));
            if (shadow)
                g.Shadow(Opts(("type", type), ("invert", invert), ("blend", blend), ("size", 8d), ("color", "#3a0ca3"),
                    ("offset", motion.Nodes.Linear("vector", new[] { 4d, 3d }, new[] { 6d, 6d }))));
            return comp;
        }

        var comp = Build(true);
        using var plain = Build(false).Render(0);
        var theirs = SynfigOracle.Render(synfig, comp, $"shade-{type}-{blend}", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"shade-{type}-{blend}-{i}");
            if (invert)
            {
                Assert.True(ours.SkBitmap.GetPixel(44, 34).Red < 200, "ours shades the rectangle's top-left inside edge");
                Assert.True(bitmap.GetPixel(44, 34).Red > 220, "synfig now shades the rectangle's edge on its bounding box too: update the docs");
            }
            using var oursPart = invert ? Crop(ours.SkBitmap, 128) : ours.SkBitmap.Copy();
            using var theirsPart = invert ? Crop(bitmap, 128) : bitmap.Copy();
            var differing = Differing(oursPart, theirsPart, 8);
            output.WriteLine($"{type} {blend}{(invert ? " inverted" : "")} frame {i}: {Differing(oursPart, theirsPart, 24):P2} differ by more than 24, {differing:P2} by more than 8");
            Assert.True(Differing(plain.SkBitmap, bitmap, 8) > 0.01, $"synfig's frame {i} has no shadow: did it drop the layer?");
            Assert.True(differing < 0.02, $"{differing:P2} differ from synfig by more than 8 at frame {i}");
        }
    }

    private static SKBitmap Crop(SKBitmap b, int fromX)
    {
        var part = new SKBitmap(b.Width - fromX, b.Height);
        b.ExtractSubset(part, SKRectI.Create(fromX, 0, b.Width - fromX, b.Height));
        return part.Copy();
    }

    private static MotionComposition Scene()
    {
        var comp = new MotionToolkit().Composition(Opts(("width", 200d), ("height", 140d), ("fps", 1d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }
    #endregion
}
