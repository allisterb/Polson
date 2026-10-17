namespace Polson.Tests.Animation;

using System;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// The corner pin (<c>comp.warp</c>, Synfig's warp layer): where the corners land, keyed corners, clipping, a pin inside a
/// moved group, the refusals, and Synfig's renderer.
/// </summary>
public class MotionWarpTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    [Fact]
    public void TheHomographyTakesTheRectangleToTheCorners()
    {
        SKPoint[] to = [new(30, 20), new(170, 40), new(150, 130), new(40, 110)];
        var map = MotionHomography.RectToQuad([10, 10, 110, 60], to[0], to[1], to[2], to[3])!.Value;
        SKPoint[] from = [new(10, 10), new(110, 10), new(110, 60), new(10, 60)];
        for (var i = 0; i < 4; i++)
        {
            var p = map.MapPoint(from[i]);
            Assert.Equal(to[i].X, p.X, 3);
            Assert.Equal(to[i].Y, p.Y, 3);
        }
        Assert.NotEqual(0, map.Persp0 + map.Persp1);           // a true perspective, not an affine map
    }

    [Fact]
    public void APinnedShapeFillsTheQuad()
    {
        var comp = Scene();
        Pinned(comp, [[30d, 20d], [170d, 40d], [150d, 130d], [40d, 110d]]).Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 100d, 100d }), ("color", "#1d3557")));

        using var frame = comp.Render(0);
        using var expected = Quad([new(30, 20), new(170, 40), new(150, 130), new(40, 110)]);
        var differing = Differing(frame.SkBitmap, expected, 24);
        output.WriteLine($"{differing:P3} differ from the quad");
        Assert.True(differing < 0.002);
    }

    [Fact]
    public void KeyedCornersMove()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        var pin = comp.Warp(Opts(("from", new[] { 0d, 0d, 100d, 100d }), ("to", new object[]
        {
            new[] { 20d, 20d }, motion.Nodes.Linear("vector", new[] { 60d, 0d }, new[] { 100d, 20d }), new[] { 100d, 100d }, new[] { 20d, 100d },
        })));
        pin.Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 100d, 100d }), ("color", "#000000")));

        using var at0 = comp.Render(0);
        using var at1 = comp.Render(1);
        Assert.False(Near(at0.SkBitmap.GetPixel(150, 25), SKColors.Black));
        Assert.True(Near(at1.SkBitmap.GetPixel(150, 25), SKColors.Black));
    }

    [Fact]
    public void ClipKeepsToTheRectangle()
    {
        foreach (var clip in new[] { true, false })
        {
            var comp = Scene();
            var pin = comp.Warp(Opts(("from", new[] { 0d, 0d, 100d, 100d }), ("clip", clip),
                ("to", new object[] { new[] { 50d, 20d }, new[] { 150d, 20d }, new[] { 150d, 120d }, new[] { 50d, 120d } })));
            pin.Circle(Opts(("origin", new[] { 100d, 50d }), ("radius", 30d), ("color", "#000000")));   // half outside the rectangle

            using var frame = comp.Render(0);
            Assert.Equal(!clip, Near(frame.SkBitmap.GetPixel(170, 70), SKColors.Black));
            Assert.True(Near(frame.SkBitmap.GetPixel(140, 70), SKColors.Black));
        }
    }

    /// <summary>A pin inside a moved, scaled group lands where the group carries it.</summary>
    [Fact]
    public void APinFollowsTheGroupItIsIn()
    {
        var comp = Scene();
        var g = comp.Group(Opts(("offset", new[] { 20d, 10d }), ("scale", new[] { 0.5d, 0.5d })));
        Pinned(g, [[60d, 40d], [340d, 80d], [300d, 260d], [80d, 220d]]).Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 100d, 100d }), ("color", "#1d3557")));

        using var frame = comp.Render(0);
        using var expected = Quad([new(50, 30), new(190, 50), new(170, 140), new(60, 120)]);
        Assert.True(Differing(frame.SkBitmap, expected, 24) < 0.002);
    }

    [Theory]
    [InlineData("line", "three lie in a line")]
    [InlineData("from", "has no area")]
    [InlineData("corners", "the four corners")]
    public void BadPinsAreRefused(string what, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => Scene().Warp(what switch
        {
            "line" => Opts(("from", new[] { 0d, 0d, 100d, 100d }), ("to", new object[] { new[] { 0d, 0d }, new[] { 50d, 50d }, new[] { 100d, 100d }, new[] { 0d, 100d } })),
            "from" => Opts(("from", new[] { 0d, 0d, 0d, 100d }), ("to", new object[] { new[] { 0d, 0d }, new[] { 1d, 0d }, new[] { 1d, 1d }, new[] { 0d, 1d } })),
            _ => Opts(("from", new[] { 0d, 0d, 100d, 100d }), ("to", new object[] { new[] { 0d, 0d } })),
        }));
        Assert.Contains(message, e.Message);
    }

    /// <summary>A blur inside a pin softens the pinned picture as it is seen; SVG, which has no perspective, is refused.</summary>
    [Fact]
    public void ABlurInsideAPinSoftensItAndSvgIsRefused()
    {
        var comp = Scene();
        var pin = Pinned(comp, [[30d, 20d], [170d, 40d], [150d, 130d], [40d, 110d]]);
        pin.Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 100d, 100d }), ("color", "#000000")));
        Assert.Contains("no perspective", Assert.Throws<InvalidOperationException>(() => comp.ToSvg()).Message);

        using var sharp = comp.Render(0);
        pin.Blur(Opts(("size", 8d)));
        using var soft = comp.Render(0);
        Assert.True(Differing(sharp.SkBitmap, soft.SkBitmap, 24) > 0.005, "the blur changed nothing");
        Assert.True(Near(soft.SkBitmap.GetPixel(95, 75), SKColors.Black), "the middle of the pinned shape is lost");
    }

    [Fact]
    public void ToSifWritesSynfigsWarpOnTopOfTheLayers()
    {
        var comp = Scene();
        Pinned(comp, [[30d, 20d], [170d, 40d], [150d, 130d], [40d, 110d]]).Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 100d, 100d }), ("color", "#000000")));

        var sif = comp.ToSif();
        Assert.Contains("type=\"warp\"", sif);
        Assert.Contains("name=\"dest_br\"", sif);
        Assert.True(sif.IndexOf("type=\"rectangle\"", StringComparison.Ordinal) < sif.IndexOf("type=\"warp\"", StringComparison.Ordinal),
            "the warp must sit above the layers it pins");
    }

    /// <summary>
    /// Synfig draws the same: a pin with keyed corners over shapes running past its rectangle (clipped), an unclipped pin
    /// inside a turned group. Synfig resamples what it pins (linearly: its default cubic rang a light halo round every
    /// pinned edge) and this draws it as vectors. More than 3 pixels from an edge they agree within 6 levels; along the
    /// edges, by more than 24 on about 2% of this frame.
    /// </summary>
    [Fact]
    public void SynfigPinsTheSame()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 240d), ("height", 160d), ("fps", 1d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        var pin = comp.Warp(Opts(("from", new[] { 0d, 0d, 100d, 100d }), ("to", new object[]
        {
            new[] { 20d, 15d }, motion.Nodes.Linear("vector", new[] { 10d, 5d }, new[] { 120d, 30d }), new[] { 110d, 140d }, new[] { 30d, 120d },
        })));
        pin.Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 100d, 100d }), ("color", "#1d3557")));
        pin.Circle(Opts(("origin", new[] { 80d, 50d }), ("radius", 35d), ("color", "#e63946")));
        var turned = comp.Group(Opts(("origin", new[] { 180d, 80d }), ("offset", new[] { 180d, 80d }), ("angle", 10d)));
        var free = turned.Warp(Opts(("clip", false), ("from", new[] { 150d, 50d, 210d, 110d }),
            ("to", new object[] { new[] { 150d, 40d }, new[] { 225d, 55d }, new[] { 215d, 120d }, new[] { 145d, 110d } })));
        free.Rectangle(Opts(("point1", new[] { 140d, 60d }), ("point2", new[] { 200d, 100d }), ("color", "#2a9d8f")));

        var theirs = SynfigOracle.Render(synfig, comp, "warp", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"warp-{i}");
            var (edges, inside) = (Differing(ours.SkBitmap, bitmap, 24), InteriorDiffering(ours.SkBitmap, bitmap, 6));
            output.WriteLine($"frame {i}: {edges:P2} differ by more than 24 (edges included); away from edges {InteriorDiffering(ours.SkBitmap, bitmap, 2):P3} by more than 2, {inside:P3} by more than 6");
            Assert.True(inside < 0.001, $"{inside:P3} of pixels away from edges differ from synfig by more than 6 at frame {i}");
            Assert.True(edges < 0.03, $"{edges:P2} differ from synfig at frame {i}");
        }
    }

    private static MotionComposition Scene(MotionToolkit? motion = null)
    {
        var comp = (motion ?? new MotionToolkit()).Composition(Opts(("width", 200d), ("height", 160d), ("fps", 1d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }

    // Unclipped: a clip on the shape's own edge would antialias that edge twice, and these tests measure where it lands.
    private static MotionGroup Pinned(MotionLayerList stack, double[][] to) =>
        stack.Warp(Opts(("from", new[] { 0d, 0d, 100d, 100d }), ("to", to.Cast<object>().ToArray()), ("clip", false)));

    /// <summary>
    /// The share of pixels differing by more than <paramref name="tolerance"/> away from any edge: those with no other
    /// colour within 3 pixels in <paramref name="a"/>. Three, because where a pin magnifies, Synfig's resampled edge bleeds
    /// up to that far into the shape (measured: a staircase band 17 levels light along the most stretched edge).
    /// </summary>
    private static double InteriorDiffering(SKBitmap a, SKBitmap b, int tolerance)
    {
        const int r = 3;
        int count = 0, n = 0;
        for (var y = r; y < a.Height - r; y++)
            for (var x = r; x < a.Width - r; x++)
            {
                var c = a.GetPixel(x, y);
                var flat = true;
                for (var dy = -r; dy <= r && flat; dy++)
                    for (var dx = -r; dx <= r && flat; dx++)
                        flat = a.GetPixel(x + dx, y + dy) == c;
                if (!flat) continue;
                n++;
                var d = b.GetPixel(x, y);
                if (Math.Abs(c.Red - d.Red) > tolerance || Math.Abs(c.Green - d.Green) > tolerance || Math.Abs(c.Blue - d.Blue) > tolerance) count++;
            }
        return count / (double)n;
    }

    private static SKBitmap Quad(SKPoint[] corners)
    {
        var bitmap = new SKBitmap(200, 160);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var path = new SKPath();
        path.AddPoly(corners);
        using var paint = new SKPaint { Color = SKColor.Parse("#1d3557"), IsAntialias = true };
        canvas.DrawPath(path, paint);
        return bitmap;
    }
    #endregion
}
