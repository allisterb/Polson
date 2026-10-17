namespace Polson.Tests.Animation;

using System;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// The camera (<c>comp.camera</c>) and 3D groups: a camera at rest changes nothing, a push-in and a lens zoom magnify, depth
/// gives parallax, orbit turns a plane edge-on, runs of planes sort by depth, the near plane and the space behind the eye,
/// frame coordinates, the refusals, and Synfig's renderer on the baked <c>.sif</c>.
/// </summary>
public class MotionCameraTests(ITestOutputHelper output) : TestsRuntime
{
    #region Fields
    private const double W = 240, H = 160;
    private static readonly double Standard = W * 50 / 36;   // the default lens and distance, in units
    #endregion

    #region Methods
    /// <summary>A camera at rest shows the z = 0 plane at exactly 100%: the same frame as no camera and no 3D.</summary>
    [Fact]
    public void ACameraAtRestChangesNothing()
    {
        var flat = Scene();
        Card(flat.Group(), "#1d3557");
        var seen = Scene();
        seen.Camera();
        Card(seen.Group(Opts(("z", 0d))), "#1d3557");

        using var a = flat.Render(0);
        using var b = seen.Render(0);
        Assert.Equal(0, Differing(a.SkBitmap, b.SkBitmap, 2));
    }

    /// <summary>Halving the distance doubles the size of a plane at z = 0, about the target; doubling the lens does the same without moving.</summary>
    [Theory]
    [InlineData("distance")]
    [InlineData("zoom")]
    public void APushInAndALensZoomBothMagnify(string how)
    {
        var comp = Scene();
        comp.Camera(how == "distance" ? Opts(("distance", Standard / 2)) : Opts(("zoom", Standard * 2)));
        Card(comp.Group(Opts(("z", 0d))), "#000000");                 // 40 × 30 at the centre

        using var frame = comp.Render(0);
        var box = Ink(frame.SkBitmap);
        output.WriteLine($"{how}: ink {box}");
        Assert.InRange(box.Width, 79, 82);
        Assert.InRange(box.Height, 59, 62);
        Assert.InRange(box.MidX, W / 2 - 1, W / 2 + 1);
    }

    /// <summary>A plane twice as far shows at half the size, and a pan moves it half as far: parallax from depth.</summary>
    [Fact]
    public void DepthGivesParallax()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        comp.Camera(Opts(("target", motion.Nodes.Linear("vector", new[] { 40d, 0d }, new[] { W / 2, H / 2 }))));
        Card(comp.Group(Opts(("z", Standard))), "#000000");          // as far behind z = 0 as the camera is in front

        using var at0 = comp.Render(0);
        using var at1 = comp.Render(1);
        var (b0, b1) = (Ink(at0.SkBitmap), Ink(at1.SkBitmap));
        output.WriteLine($"far plane: {b0} then {b1}");
        Assert.InRange(b0.Width, 19, 22);
        Assert.InRange(b0.MidX - b1.MidX, 19, 21);                 // the camera panned 40; the far plane moved 20
    }

    [Fact]
    public void OrbitTurnsAPlaneEdgeOn()
    {
        var comp = Scene();
        comp.Camera(Opts(("orbit", 89.5)));
        Card(comp.Group(Opts(("z", 0d))), "#000000");

        using var frame = comp.Render(0);
        var box = Ink(frame.SkBitmap);
        output.WriteLine($"edge-on ink {box}");
        Assert.True(box.IsEmpty || box.Width <= 3, $"a plane seen edge-on is {box.Width} wide");
    }

    /// <summary>A run of 3D groups draws far to near whatever order they were added in; a 2D layer between them ends the run.</summary>
    [Fact]
    public void PlanesSortByDepth()
    {
        foreach (var (front, expected) in new[] { (0d, "#e63946"), (400d, "#1d3557") })
        {
            var comp = Scene();
            comp.Camera();
            Card(comp.Group(Opts(("z", front))), "#e63946");          // added first
            Card(comp.Group(Opts(("z", 200d), ("scale", new[] { 1.5d, 1.5d }), ("origin", new[] { W / 2, H / 2 }), ("offset", new[] { W / 2, H / 2 }))), "#1d3557");

            using var frame = comp.Render(0);
            Assert.True(Near(frame.SkBitmap.GetPixel((int)(W / 2), (int)(H / 2)), SKColor.Parse(expected)), $"z {front}: the wrong card is in front");
        }
    }

    /// <summary>A plane wholly behind the eye draws nothing; one that passes through the near plane draws the part in front, without error.</summary>
    [Fact]
    public void TheNearPlaneCutsAndBehindIsGone()
    {
        var behind = Scene();
        behind.Camera();
        Card(behind.Group(Opts(("z", -Standard * 1.5))), "#000000");
        using (var frame = behind.Render(0))
            Assert.True(Ink(frame.SkBitmap).IsEmpty);

        var through = Scene();
        through.Camera();
        var floor = through.Group(Opts(("z", 0d), ("rotateX", 88d), ("origin", new[] { W / 2, H / 2 }), ("offset", new[] { W / 2, H / 2 + 20 })));
        floor.Rectangle(Opts(("point1", new[] { -2000d, -4000d }), ("point2", new[] { 2000d, 4000d }), ("color", "#2a9d8f")));
        using var seen = through.Render(0);
        Assert.False(Ink(seen.SkBitmap).IsEmpty);
    }

    /// <summary>A text in a 3D group reports its box in the frame through the camera: a push-in to half the distance doubles it.</summary>
    [Fact]
    public void TextBoundsFollowTheCamera()
    {
        if (!Installed("Arial")) return;
        var comp = Scene();
        comp.Camera(Opts(("distance", Standard / 2)));
        var title = comp.Group(Opts(("z", 0d))).Text(Opts(("text", "CAM"), ("family", "Arial"), ("size", 20d), ("origin", new[] { W / 2, H / 2 })));

        var local = title.BoundsAt(0)!;
        var framed = title.BoundsAt(0, Opts(("frame", true)))!;
        using var frame = comp.Render(0);
        var ink = Ink(frame.SkBitmap);
        Assert.Equal(2 * (double)local["width"]!, (double)framed["width"]!, 0);
        Assert.InRange((double)framed["x"]!, ink.Left - 1.5, ink.Left + 1.5);
    }

    [Fact]
    public void TheRefusalsSayWhatToDo()
    {
        var noCamera = Scene();
        Card(noCamera.Group(Opts(("z", 10d))), "#000000");
        Assert.Contains("add comp.camera()", Assert.Throws<InvalidOperationException>(() => noCamera.Render(0)).Message);

        var twice = Scene();
        twice.Camera();
        Assert.Contains("has a camera already", Assert.Throws<InvalidOperationException>(() => twice.Camera()).Message);
        Assert.Contains("not both", Assert.Throws<ArgumentException>(() => Scene().Camera(Opts(("zoom", 100d), ("focalLength", 35d)))).Message);

        var close = Scene();
        close.Camera(Opts(("distance", 0.5)));
        Card(close.Group(Opts(("z", 0d))), "#000000");
        Assert.Contains("in front of its target", Assert.Throws<ArgumentException>(() => close.Render(0)).Message);

        var svg = Scene();
        svg.Camera();
        Card(svg.Group(Opts(("z", 0d))), "#000000");
        Assert.Contains("no perspective", Assert.Throws<InvalidOperationException>(() => svg.ToSvg()).Message);
    }

    /// <summary>Two planes that change places in depth during the film cannot go to .sif, whose stack order is fixed.</summary>
    [Fact]
    public void ToSifRefusesPlanesThatChangePlaces()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        comp.Camera();
        Card(comp.Group(Opts(("z", motion.Nodes.Linear("real", 400d, -100d)), ("desc", "mover"))), "#e63946");
        Card(comp.Group(Opts(("z", 100d), ("desc", "still"))), "#1d3557");

        var e = Assert.Throws<InvalidOperationException>(() => comp.ToSif());
        Assert.Contains("'mover'", e.Message);
        Assert.Contains("'still'", e.Message);
    }

    /// <summary>
    /// Synfig draws the baked .sif the same: three planes at depths under a camera that pushes in and orbits, one plane
    /// turned and one inside a moved 2D group. Each plane is a group with no transformation of its own and a warp on top.
    /// </summary>
    [Fact]
    public void SynfigDrawsTheBakedCameraTheSame()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", W), ("height", H), ("fps", 1d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Camera(Opts(("distance", motion.Nodes.Linear("real", -60d, Standard)), ("orbit", motion.Nodes.Linear("angle", 6d, -6d))));
        var back = comp.Group(Opts(("z", 300d)));
        back.Rectangle(Opts(("point1", new[] { 20d, 20d }), ("point2", new[] { 220d, 140d }), ("color", "#a8dadc")));
        var middle = comp.Group(Opts(("z", 120d), ("rotateY", 25d), ("origin", new[] { 90d, 80d }), ("offset", new[] { 90d, 80d })));
        middle.Circle(Opts(("origin", new[] { 90d, 80d }), ("radius", 35d), ("color", "#1d3557")));
        var holder = comp.Group(Opts(("offset", new[] { 30d, 0d })));
        var front = holder.Group(Opts(("z", -40d)));
        front.Rectangle(Opts(("point1", new[] { 130d, 70d }), ("point2", new[] { 190d, 120d }), ("color", "#e63946")));

        var sif = comp.ToSif();
        Assert.Equal(3, sif.Split("type=\"warp\"").Length - 1);

        if (SynfigOracle.Find() is not { } synfig) return;
        var theirs = SynfigOracle.Render(synfig, comp, "camera", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"camera-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"frame {i}: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, bitmap, 8):P2} by more than 8");
            Assert.True(differing < 0.03, $"{differing:P2} differ from synfig at frame {i}");
        }
    }

    /// <summary>
    /// <c>keepSize</c>: a plane at depth looks at time 0 exactly as it would at z 0 — scaled about the camera's target, so an
    /// off-centre card stays where it was — and a push-in still shows its depth: it grows less than a plane at z 0.
    /// trailer4 pre-scaled every plane of its poster by hand to get this.
    /// </summary>
    [Fact]
    public void KeepSizeHoldsAPlaneAtDepthToItsFlatSizeAtRest()
    {
        var motion = new MotionToolkit();
        void OffCentre(MotionLayerList s) =>
            s.Rectangle(Opts(("point1", new[] { 80d, 60d }), ("point2", new[] { 140d, 100d }), ("color", "#1d3557")));

        // A gentle push (to three quarters of the distance), so the cards stay inside the frame and their ink is whole.
        var flat = Scene(motion);
        OffCentre(flat);
        var deep = Scene(motion);
        deep.Camera(Opts(("distance", motion.Nodes.Linear("real", -Standard / 4, Standard))));
        OffCentre(deep.Group(Opts(("z", 300d), ("keepSize", true))));
        var near = Scene(motion);
        near.Camera(Opts(("distance", motion.Nodes.Linear("real", -Standard / 4, Standard))));
        OffCentre(near.Group(Opts(("z", 0d))));

        using var a = flat.Render(0);
        using var b = deep.Render(0);
        Assert.Equal(0, Differing(a.SkBitmap, b.SkBitmap, 2));

        using var deep1 = deep.Render(1);
        using var near1 = near.Render(1);
        var (d, n, f) = (Ink(deep1.SkBitmap), Ink(near1.SkBitmap), Ink(a.SkBitmap));
        output.WriteLine($"at rest {f}; pushed: kept-size plane at z 300 {d}, plane at z 0 {n}");
        Assert.True(d.Width > f.Width + 2, "the push did not move the kept-size plane");
        Assert.True(d.Width < n.Width - 2, "the kept-size plane grew as much as one at z 0: its depth is lost");

        var e = Assert.Throws<ArgumentException>(() => Scene(motion).Group(Opts(("keepSize", true))));
        Assert.Contains("give the group a z", e.Message);
    }

    /// <summary>A kept-size plane goes to <c>.sif</c> through the same warp bake, and Synfig draws it the same.</summary>
    [Fact]
    public void SynfigDrawsAKeptSizePlaneTheSame()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", W), ("height", H), ("fps", 1d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Camera(Opts(("distance", motion.Nodes.Linear("real", -Standard / 2, Standard))));
        var back = comp.Group(Opts(("z", 250d), ("keepSize", true)));
        back.Rectangle(Opts(("point1", new[] { 30d, 25d }), ("point2", new[] { 210d, 135d }), ("color", "#a8dadc")));
        var front = comp.Group(Opts(("z", 40d), ("keepSize", true)));
        front.Rectangle(Opts(("point1", new[] { 100d, 60d }), ("point2", new[] { 150d, 100d }), ("color", "#e63946")));

        if (SynfigOracle.Find() is not { } synfig) return;
        var theirs = SynfigOracle.Render(synfig, comp, "camera-keepsize", output);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"camera-keepsize-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"frame {i}: {differing:P2} differ by more than 24");
            Assert.True(differing < 0.03, $"{differing:P2} differ from synfig at frame {i}");
        }
    }

    private static bool Installed(string family)
    {
        using var face = SKFontManager.Default.MatchFamily(family);
        return face is not null && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A shadow on a turned plane: ours works on the picture as the camera sees it, at the plane's scale where it crosses
    /// the frame's centre; Synfig, reading the baked .sif, works on the plane before the warp. Measured here, so the
    /// difference documented is the difference there is.
    /// </summary>
    [Fact]
    public void AShadowOnATurnedPlaneIsCloseToSynfigs()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var comp = new MotionToolkit().Composition(Opts(("width", W), ("height", H), ("fps", 1d), ("duration", 0d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Camera(Opts(("orbit", -10d)));
        var card = comp.Group(Opts(("z", 40d), ("rotateY", 15d), ("origin", new[] { W / 2, H / 2 }), ("offset", new[] { W / 2, H / 2 })));
        card.Rectangle(Opts(("point1", new[] { 70d, 45d }), ("point2", new[] { 170d, 115d }), ("color", "#e9c46a")));
        card.Shadow(Opts(("offset", new[] { 8d, 10d }), ("size", 10d)));

        using var theirs = Assert.Single(SynfigOracle.Render(synfig, comp, "camera-shadow", output));
        using var ours = comp.Render(0);
        SynfigOracle.Dump(ours.SkBitmap, theirs, "camera-shadow");
        var differing = Differing(ours.SkBitmap, theirs, 24);
        output.WriteLine($"shadow on a plane turned 15° and orbited 10°: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, theirs, 8):P2} by more than 8");
        Assert.True(differing < 0.03, $"{differing:P2} differ from synfig");
    }

    private static MotionComposition Scene(MotionToolkit? motion = null)
    {
        var comp = (motion ?? new MotionToolkit()).Composition(Opts(("width", W), ("height", H), ("fps", 1d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }

    /// <summary>A 40 × 30 card at the frame's centre.</summary>
    private static void Card(MotionLayerList stack, string color) =>
        stack.Rectangle(Opts(("point1", new[] { W / 2 - 20, H / 2 - 15 }), ("point2", new[] { W / 2 + 20, H / 2 + 15 }), ("color", color)));

    /// <summary>The box of every pixel darker than near-white, in pixel edges.</summary>
    private static SKRect Ink(SKBitmap b)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y) is var c && (c.Red < 200 || c.Green < 200 || c.Blue < 200))
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
        return maxX < 0 ? SKRect.Empty : new SKRect(minX, minY, maxX + 1, maxY + 1);
    }
    #endregion
}
