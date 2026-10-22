namespace Polson.Tests.Animation;

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// The text layer: its layout against Synfig's renderer where it is present, the animators and their selectors, the
/// refusals, and the SVG route.
/// </summary>
public class MotionTextTests(ITestOutputHelper output) : TestsRuntime
{
    #region Oracle
    /// <summary>
    /// Plain text through both renderers: two lines, three alignments, a keyed size, compress and origin — the layout is
    /// Synfig's, so the glyphs should land on the same pixels. Unkerned, because Synfig 1.5.5 never kerns: it does not
    /// size its FreeType face, so the kerning FreeType gives it scales to nothing. Synfig drops an unknown layer silently,
    /// so its frame must also differ from the empty ground.
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0d, 0d)]
    [InlineData(1d, 1d)]
    public void SynfigAndSkiaSetTheSameText(double ox, double oy)
    {
        if (Synfig() is not { } synfig || !Installed("Arial")) return;

        var motion = new MotionToolkit();
        var comp = Ground(motion);
        comp.Text(Opts(("text", "AVATAR WAVE\nType, set."), ("family", "Arial"), ("origin", motion.Nodes.Linear("vector", new[] { 20d, 10d }, new[] { 190d, 115d })),
            ("orient", new[] { ox, oy }), ("color", "#1d3557"), ("kerning", false), ("vcompress", 1.2),
            ("size", motion.Nodes.Linear("real", 12, 36)), ("compress", motion.Nodes.Linear("real", 0.2, 1))));

        Compare(synfig, comp, $"text-{ox}-{oy}", 0.01);
    }

    /// <summary>The same text in a heavier weight, italic, inverted: the parameters Synfig reads as integers and a flag.</summary>
    [Fact]
    public void SynfigAndSkiaSetBoldItalicInverted()
    {
        if (Synfig() is not { } synfig || !Installed("Arial")) return;

        var motion = new MotionToolkit();
        var comp = Ground(motion);
        comp.Text(Opts(("text", "Bold italic"), ("family", "Arial"), ("weight", "bold"), ("style", "italic"), ("size", 40d),
            ("origin", new[] { 200d, 70d }), ("color", "#e63946"), ("kerning", false)));
        comp.Rectangle(Opts(("point1", new[] { 40d, 130d }), ("point2", new[] { 360d, 210d }), ("color", "#1d3557")));
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 40d, 130d }), ("point2", new[] { 360d, 210d }), ("color", "#f4a261")));
        g.Text(Opts(("text", "Knockout"), ("family", "Arial"), ("weight", 900), ("size", 48d), ("origin", new[] { 200d, 170d }),
            ("color", "#000000"), ("invert", true), ("blend", "alphaOver"), ("kerning", false)));

        Compare(synfig, comp, "text-styles", 0.01);
    }

    /// <summary>
    /// A face Windows files under a shared family but whose own name table gives it a family of its own: Franklin Gothic
    /// Heavy is "Franklin Gothic" at 900 here, and "Franklin Gothic Heavy" to fontconfig, which Synfig asks. Written by
    /// the shared name, trailer1's title opened in Synfig in a fallback face; the .sif now carries the font's own name.
    /// </summary>
    [Fact]
    public void SynfigFindsAFaceWindowsGroupsUnderAnotherFamily()
    {
        if (Synfig() is not { } synfig || !Installed("Franklin Gothic")) return;

        var motion = new MotionToolkit();
        var comp = Ground(motion);
        comp.Text(Opts(("text", "BE COOL"), ("family", "Franklin Gothic"), ("weight", "heavy"), ("size", 90d),
            ("origin", new[] { 200d, 120d }), ("color", "#141414"), ("kerning", false)));
        Assert.Contains("<string>Franklin Gothic Heavy</string>", comp.ToSif());

        Compare(synfig, comp, "text-legacy-family", 0.01);
    }

    /// <summary>Kerning, which Synfig does not do, tightens AV here: the kerned line is narrower.</summary>
    [Fact]
    public void KerningTightensAPair()
    {
        if (!Installed("Arial")) return;
        double Width(bool kerning)
        {
            var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 80d)));
            comp.Text(Opts(("text", "AVAVAV"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 10d, 40d }),
                ("orient", new[] { 0d, 0.5 }), ("kerning", kerning)));
            using var frame = comp.Render(0);
            return Enumerable.Range(0, 300).Last(x => Enumerable.Range(0, 80).Any(y => frame.SkBitmap.GetPixel(x, y).Alpha > 128));
        }

        Assert.True(Width(true) < Width(false) - 5, $"kerned {Width(true)}, unkerned {Width(false)}");
    }

    /// <summary>
    /// Animators written as Synfig can hold them — a group per letter, its motion sampled at the frame rate — rendered by
    /// Synfig against this, at the frames the samples were taken.
    /// </summary>
    [Fact]
    public void SynfigAndSkiaAnimateTheSameLetters()
    {
        if (Synfig() is not { } synfig || !Installed("Arial")) return;

        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 400d), ("height", 240d), ("fps", 4d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Text(Opts(("text", "Rise and turn"), ("family", "Arial"), ("size", 44d), ("origin", new[] { 200d, 120d }),
            ("color", "#1d3557"), ("kerning", false), ("animators", new object[]
            {
                Opts(("properties", Opts(("position", new[] { 0d, 60d }), ("rotation", 45d), ("opacity", 0d))),
                     // Offset from 100% to −100% over the second: a ramp that sweeps across the line.
                     ("selector", Opts(("shape", "rampUp"), ("offset", motion.Nodes.Linear("real", -200, 100))))),
                Opts(("properties", Opts(("fillColor", "#e63946"), ("scale", 1.4))),
                     ("selector", Opts(("basedOn", "words"), ("start", 50d), ("end", 100d)))),
            })));

        Compare(synfig, comp, "text-animated", 0.015);
    }
    /// <summary>
    /// Sampled keys keep only what a straight line between the kept keys cannot reproduce: a letter that rises and then
    /// holds for most of the film is a handful of keys, not one per frame — and still exact at every frame.
    /// </summary>
    [Fact]
    public void AHeldLetterIsAFewKeysNotOnePerFrame()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 120d), ("fps", 24d), ("duration", 10d)));
        comp.Text(Opts(("text", "Hi"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 60d }), ("animators", new object[]
        {
            Opts(("properties", Opts(("position", new[] { 0d, 40d }))),
                 ("selector", Opts(("shape", "rampUp"), ("offset", motion.Nodes.Animated("real", new object[]
                 {
                     Opts(("time", 0d), ("value", -100d), ("ease", "linear")), Opts(("time", 1d), ("value", 100d), ("ease", "linear")),
                 }))))),
        })));

        var sif = comp.ToSif();
        var waypoints = System.Text.RegularExpressions.Regex.Matches(sif, "<waypoint ").Count;
        output.WriteLine($"{waypoints} waypoints for two letters over {comp.FrameCount} frames");
        Assert.True(waypoints < 2 * 60, $"{waypoints} waypoints: the 9 s hold should cost two keys a value, not 216");
    }
    #endregion

    #region The handle
    /// <summary>
    /// <c>text.boundsAt(t)</c> is the letters as drawn: it agrees with the rendered ink to within a pixel, which is what
    /// both trailer runs had to reconstruct by other means for their title-safe checks.
    /// </summary>
    [Fact]
    public void BoundsAtIsTheInkAsDrawn()
    {
        if (!Installed("Arial")) return;
        var comp = new MotionToolkit().Composition(Opts(("width", 400d), ("height", 160d)));
        var title = comp.Text(Opts(("text", "Bounds, ag"), ("family", "Arial"), ("size", 60d), ("origin", new[] { 200d, 80d })));

        var b = title.BoundsAt(0)!;
        using var frame = comp.Render(0);
        var ink = InkBox(frame.SkBitmap);
        output.WriteLine($"boundsAt x {b["x"]:0.0} y {b["y"]:0.0} w {b["width"]:0.0} h {b["height"]:0.0}; ink {ink}");
        Assert.InRange((double)b["x"]!, ink.Left - 1, ink.Left + 1);
        Assert.InRange((double)b["y"]!, ink.Top - 1, ink.Top + 1);
        Assert.InRange((double)b["x"]! + (double)b["width"]!, ink.Right - 1, ink.Right + 1);
        Assert.InRange((double)b["y"]! + (double)b["height"]!, ink.Bottom - 1, ink.Bottom + 1);
    }

    /// <summary>Animators are applied: a fully selected offset moves the box, and a letter at zero opacity leaves it.</summary>
    [Fact]
    public void BoundsAtFollowsTheAnimators()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 400d), ("height", 200d), ("duration", 1d)));
        var title = comp.Text(Opts(("text", "MOVE"), ("family", "Arial"), ("size", 50d), ("origin", new[] { 200d, 60d }), ("animators", new object[]
        {
            // Offset −100 → 0: the square span starts wholly before the text (nothing selected) and ends over all of it.
            Opts(("properties", Opts(("position", new[] { 0d, 80d }))), ("selector", Opts(("offset", motion.Nodes.Linear("real", 100, -100))))),
        })));

        var before = title.BoundsAt(0)!;
        var after = title.BoundsAt(1)!;
        output.WriteLine($"y at 0 s {before["y"]:0.0}, at 1 s {after["y"]:0.0}");
        Assert.Equal((double)before["y"]! + 80, (double)after["y"]!, 3);
        Assert.Equal((double)before["x"]!, (double)after["x"]!, 3);

        var hidden = motion.Composition(Opts(("width", 400d), ("height", 200d)));
        var gone = hidden.Text(Opts(("text", "GONE"), ("family", "Arial"), ("animators", new object[] { Opts(("properties", Opts(("opacity", 0d)))) })));
        Assert.Null(gone.BoundsAt(0));
        Assert.Empty(gone.LettersAt(0));
    }

    /// <summary>
    /// <c>{ frame: true }</c> carries the box out through every group — a view of half the frame's pixels, a scaled and
    /// moved outer group whose clock runs twice as fast from half a second in, an inner group keyed on that clock — and
    /// agrees with the rendered ink to within a pixel. The stack's own box does not.
    /// </summary>
    [Fact]
    public void BoundsAtInTheFrameFollowsEveryGroupAbove()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 400d), ("height", 160d), ("duration", 1d), ("view", new[] { 0d, 0d, 200d, 80d })));
        var camera = comp.Group(Opts(("offset", new[] { 20d, 10d }), ("scale", new[] { 0.8d, 0.8d }), ("timeDilation", 2d), ("timeOffset", 0.5)));
        var drift = camera.Group(Opts(("offset", motion.Nodes.Linear("vector", new[] { 40d, 0d }, new[] { 0d, 0d }))));
        var title = drift.Text(Opts(("text", "Frame"), ("family", "Arial"), ("size", 24d), ("origin", new[] { 60d, 30d })));

        foreach (var t in new[] { 0d, 0.25 })
        {
            var b = title.BoundsAt(t, Opts(("frame", true)))!;
            using var frame = comp.Render(t);
            var ink = InkBox(frame.SkBitmap);
            output.WriteLine($"t {t}: frame box x {b["x"]:0.0} y {b["y"]:0.0} w {b["width"]:0.0} h {b["height"]:0.0}; ink {ink}");
            Assert.InRange((double)b["x"]!, ink.Left - 1, ink.Left + 1);
            Assert.InRange((double)b["y"]!, ink.Top - 1, ink.Top + 1);
            Assert.InRange((double)b["x"]! + (double)b["width"]!, ink.Right - 1, ink.Right + 1);
            Assert.InRange((double)b["y"]! + (double)b["height"]!, ink.Bottom - 1, ink.Bottom + 1);
        }

        Assert.NotEqual((double)title.BoundsAt(0)!["x"]!, (double)title.BoundsAt(0, Opts(("frame", true)))!["x"]!, 1);
        Assert.Equal(title.LettersAt(0).Count, title.LettersAt(0, Opts(("frame", true))).Count);
    }

    /// <summary>A text in a switch that is showing another layer is not in the frame.</summary>
    [Fact]
    public void ATextASwitchHidesIsNotInTheFrame()
    {
        if (!Installed("Arial")) return;
        var comp = new MotionToolkit().Composition(Opts(("width", 200d), ("height", 100d), ("duration", 1d)));
        var cards = comp.Switch(Opts(("active", new object[] { new object[] { 0d, "title" }, new object[] { 0.5d, "end" } })));
        var title = cards.Text(Opts(("text", "Title"), ("family", "Arial"), ("size", 20d), ("origin", new[] { 100d, 50d }), ("desc", "title")));
        cards.Fill(Opts(("color", "#000000"), ("desc", "end")));

        Assert.NotNull(title.BoundsAt(0.25, Opts(("frame", true))));
        Assert.Null(title.BoundsAt(0.75, Opts(("frame", true))));
        Assert.NotNull(title.BoundsAt(0.75));
    }

    /// <summary>Each letter with its box and what it counts as; spaces have no ink and are left out.</summary>
    [Fact]
    public void LettersAtReportsEachVisibleLetter()
    {
        if (!Installed("Arial")) return;
        var comp = new MotionToolkit().Composition(Opts(("width", 400d), ("height", 200d)));
        var text = comp.Text(Opts(("text", "Be cool\nnow"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 200d, 100d }), ("kerning", false)));

        var letters = text.LettersAt(0);
        Assert.Equal("Becoolnow", string.Concat(letters.Select(l => (string)l["text"]!)));
        Assert.Equal([0, 0, 1, 1, 1, 1, 2, 2, 2], letters.Select(l => (int)l["word"]!));
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 1, 1], letters.Select(l => (int)l["line"]!));
        Assert.True((double)letters[^1]["y"]! > (double)letters[0]["y"]!, "the second line sits below the first");
        Assert.Equal("Be cool\nnow", text.Text);
    }
    #endregion

    #region Animators
    /// <summary>The classic reveal: opacity 0, the range's start keyed 0 → 100, so letters appear left to right.</summary>
    [Fact]
    public void ARevealShowsLettersLeftToRight()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 80d), ("duration", 1d)));
        comp.Text(Opts(("text", "REVEAL"), ("family", "Arial"), ("size", 48d), ("origin", new[] { 150d, 40d }), ("animators", new object[]
        {
            Opts(("properties", Opts(("opacity", 0d))), ("selector", Opts(("start", motion.Nodes.Linear("real", 100, 0)), ("smoothness", 0d)))),
        })));

        Assert.Equal(0, Ink(comp, 0, 0, 300));                               // all selected: nothing shows
        Assert.True(Ink(comp, 0.5, 0, 110) > 0, "the first letters should show at 0.5 s");
        Assert.Equal(0, Ink(comp, 0.5, 190, 300));                           // the last ones not yet
        Assert.True(Ink(comp, 1, 190, 300) > 0, "everything should show at the end");
    }

    [Fact]
    public void APositionAnimatorMovesSelectedLettersOnly()
    {
        if (!Installed("Arial")) return;
        var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 160d)));
        comp.Text(Opts(("text", "HH HH"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 40d }), ("kerning", false),
            ("animators", new object[]
            {
                Opts(("properties", Opts(("position", new[] { 0d, 80d }))), ("selector", Opts(("basedOn", "words"), ("start", 50d)))),
            })));

        using var frame = comp.Render(0);
        // The first word stays near y 40; the second drops 80.
        Assert.True(InkRows(frame.SkBitmap, 0, 150).Max() < 70);
        Assert.True(InkRows(frame.SkBitmap, 150, 300).Min() > 90);
    }

    /// <summary>
    /// <c>text.settledAt</c>: a cascade that finishes at 1 s is still from then to the film's end, and fading out ends the
    /// hold; a text in a moving group is still in its own stack, but never on screen. trailer4 found its tagline's rest by
    /// diffing boxes frame by frame.
    /// </summary>
    [Fact]
    public void SettledAtFindsWhenTheTextStopsMoving()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 160d), ("fps", 24d), ("duration", 4d)));
        var title = comp.Text(Opts(("text", "LAND"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 60d }),
            ("amount", motion.Nodes.Animated("real", new object[] { Opts(("time", 3d), ("value", 1d)), Opts(("time", 3.5d), ("value", 0d)) })),
            ("animators", new object[]
            {
                Opts(("properties", Opts(("position", new[] { 0d, 60d }))),
                    ("selector", Opts(("offset", motion.Nodes.Animated("real", new object[] { Opts(("time", 0d), ("value", -100d)), Opts(("time", 1d), ("value", 100d)) }))))),
            })));

        var s = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(title.SettledAt());
        output.WriteLine($"settled at {s["at"]}, until {s["until"]}, {s["seconds"]} s");
        Assert.InRange((double)s["at"]!, 1 - 1e-9, 1 + 1 / 24d + 1e-9);
        Assert.InRange((double)s["until"]!, 2.9, 3.0 + 1e-9);         // amount starts falling after 3 s

        var mover = comp.Group(Opts(("offset", motion.Nodes.Linear("vector", new[] { 20d, 0d }, new[] { 0d, 0d }))));
        var riding = mover.Text(Opts(("text", "RIDE"), ("family", "Arial"), ("size", 30d), ("origin", new[] { 100d, 120d })));
        Assert.Equal(0d, (double)riding.SettledAt()!["at"]!);
        Assert.Null(riding.SettledAt(0, Opts(("frame", true))));
    }

    /// <summary>
    /// productad1 cut each scene away by its group's <c>amount</c> going to 0; the name inside was reported held to the
    /// film's end (10 s against 2 s true). In frame mode a group above fades its text as the text's own amount does.
    /// </summary>
    [Fact]
    public void SettledAtOnTheFrameEndsWhenAGroupAboveFadesOut()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 160d), ("fps", 24d), ("duration", 4d)));
        var scene = comp.Group(Opts(("amount", motion.Nodes.Animated("real", new object[]
            { Opts(("time", 1.95d), ("value", 1d)), Opts(("time", 2d), ("value", 0d)) }))));
        var name = scene.Text(Opts(("text", "NAME"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 80d })));

        var own = name.SettledAt()!;
        Assert.Equal(4d, (double)own["until"]!, 3);                   // in its own stack nothing changes

        var shown = name.SettledAt(0, Opts(("frame", true)))!;
        Assert.Equal(0d, (double)shown["at"]!);
        Assert.InRange((double)shown["until"]!, 1.9, 1.96);          // the scene's fade ends the hold
    }

    /// <summary>
    /// <c>comp.texts()</c>: every text the film sets and the seconds it is on the frame — groups included, a scene cut away
    /// by its group's amount ending its interval, a text faded in partway starting late, one never shown with no times.
    /// The ground truth <c>Verify.film</c> holds a read-back against.
    /// </summary>
    [Fact]
    public void TextsListsEveryTextAndWhenItIsOnTheFrame()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 160d), ("fps", 24d), ("duration", 4d)));
        comp.Text(Opts(("text", "TITLE"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 40d }), ("desc", "title")));
        var scene = comp.Group(Opts(("amount", motion.Nodes.Animated("real", new object[]
            { Opts(("time", 1.95d), ("value", 1d)), Opts(("time", 2d), ("value", 0d)) }))));
        scene.Text(Opts(("text", "$40.4B"), ("family", "Arial"), ("size", 30d), ("origin", new[] { 150d, 90d })));
        comp.Text(Opts(("text", "LATE"), ("family", "Arial"), ("size", 30d), ("origin", new[] { 150d, 130d }),
            ("amount", motion.Nodes.Animated("real", new object[] { Opts(("time", 2.95d), ("value", 0d)), Opts(("time", 3d), ("value", 1d)) }))));
        comp.Text(Opts(("text", "NEVER"), ("family", "Arial"), ("size", 30d), ("origin", new[] { 150d, 150d }), ("amount", 0d)));

        var texts = comp.Texts().Cast<System.Collections.Generic.Dictionary<string, object?>>().ToArray();

        Assert.Equal(["TITLE", "$40.4B", "LATE", "NEVER"], texts.Select(t => (string)t["text"]!));
        Assert.Equal("title", texts[0]["desc"]);
        Assert.Equal((0d, 4d), ((double)texts[0]["from"]!, (double)texts[0]["to"]!));
        Assert.Equal(0d, (double)texts[1]["from"]!);
        Assert.InRange((double)texts[1]["to"]!, 1.8, 1.96);                   // the scene's fade ends it on the frame
        Assert.InRange((double)texts[2]["from"]!, 2.95, 3.05);
        Assert.Null(texts[3]["from"]);
        Assert.Empty((object?[])texts[3]["intervals"]!);
    }

    [Fact]
    public void ARandomizedSelectorIsSeededAndRepeatable()
    {
        if (!Installed("Arial")) return;
        string Render(int seed)
        {
            var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 80d)));
            comp.Text(Opts(("text", "SCRAMBLE"), ("family", "Arial"), ("size", 36d), ("origin", new[] { 150d, 40d }), ("animators", new object[]
            {
                Opts(("properties", Opts(("opacity", 0d))), ("selector", Opts(("end", 50d), ("randomize", true), ("seed", seed)))),
            })));
            using var frame = comp.Render(0);
            return Convert.ToBase64String(frame.SkBitmap.Bytes);
        }

        Assert.Equal(Render(3), Render(3));
        Assert.NotEqual(Render(3), Render(4));
    }
    #endregion

    #region Selector maths
    /// <summary>EffectCraft's own cases for the ported maths, which is what the port is held to.</summary>
    [Fact]
    public void TheRangeSelectorMatchesEffectCraftsCases()
    {
        var r = new MotionTextSelectors.Range(0, 0.375);
        Assert.Equal(1, MotionTextSelectors.RangeValue(r, 0, 4), 9);
        Assert.Equal(0.5, MotionTextSelectors.RangeValue(r, 1, 4), 9);
        Assert.Equal(0, MotionTextSelectors.RangeValue(r, 2, 4), 9);
        Assert.Equal(1, MotionTextSelectors.RangeValue(r with { Smoothness = 0 }, 1, 4), 9);
        Assert.Equal(0, MotionTextSelectors.RangeValue(r with { Smoothness = 0, End = 0.3 }, 1, 4), 9);

        var up = Enumerable.Range(0, 5).Select(i => MotionTextSelectors.RangeValue(new(0, 1, Shape: MotionTextSelectors.Shape.RampUp), i, 5)).ToArray();
        Assert.Equal(0.1, up[0], 9);
        Assert.Equal(0.9, up[4], 9);
        Assert.Equal(1, MotionTextSelectors.RangeValue(new(0, 0.2, Shape: MotionTextSelectors.Shape.RampUp), 4, 5), 9);
        Assert.Equal(1, MotionTextSelectors.RangeValue(new(0, 0.2, Offset: 0.6), 3, 5), 9);
        Assert.Equal(-0.5, MotionTextSelectors.RangeValue(new(Amount: -0.5), 2, 5), 9);
    }

    [Fact]
    public void TheModesAndEasesMatchEffectCraftsCases()
    {
        Assert.Equal(1, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Add, 0.5, 0.75));
        Assert.Equal(0.25, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Intersect, 0.5, 0.5));
        Assert.Equal(0.7, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Difference, 0.3, 1), 9);
        Assert.Equal(0, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Subtract, MotionTextSelectors.Initial(MotionTextSelectors.Mode.Subtract), 1));

        Assert.True(MotionTextSelectors.Ease(0.1, 0, 1) < 0.1);
        Assert.True(MotionTextSelectors.Ease(0.9, 1, 0) > 0.9);
        var v = Enumerable.Range(0, 21).Select(i => MotionTextSelectors.Ease(i / 20.0, -1, 1)).ToArray();
        Assert.True(v.Zip(v.Skip(1)).All(p => p.Second >= p.First));

        var order = MotionTextSelectors.RandomOrder(20, 7);
        Assert.Equal(Enumerable.Range(0, 20), order.Order());
        Assert.Equal(order, MotionTextSelectors.RandomOrder(20, 7));
        Assert.NotEqual(order, MotionTextSelectors.RandomOrder(20, 8));

        var w = new MotionTextSelectors.Wiggly(Seed: 3);
        Assert.Equal(MotionTextSelectors.WigglyValue(w, 1.234, 5, 0), MotionTextSelectors.WigglyValue(w, 1.234, 5, 0));
        Assert.True(Math.Abs(MotionTextSelectors.WigglyValue(w, 1, 2, 0) - MotionTextSelectors.WigglyValue(w, 1.001, 2, 0)) < 0.02);
        var c1 = w with { Correlation = 1 };
        Assert.Equal(MotionTextSelectors.WigglyValue(c1, 0.7, 0, 0), MotionTextSelectors.WigglyValue(c1, 0.7, 9, 0));
    }
    #endregion

    #region Refusals
    [Fact]
    public void AMissingFamilyIsRefusedWithWhatToUse()
    {
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Polson Nonexistent Face 7Q"))));
        Assert.Contains("not installed", e.Message);
        Assert.Contains("Skia.Font.families()", e.Message);

        if (!Installed("Franklin Gothic")) return;
        e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Franklin Gothic Heavy"))));
        Assert.Contains("'Franklin Gothic'", e.Message);
        Assert.Contains("weight 900", e.Message);
    }

    [Theory]
    [InlineData("selector", "mystery", "has no 'mystery'")]
    [InlineData("shape", "zigzag", "'zigzag' is not")]
    [InlineData("basedOn", "paragraphs", "'paragraphs' is not")]
    public void ABadSelectorIsRefusedByName(string key, string value, string expected)
    {
        if (!Installed("Arial")) return;
        var selector = key == "selector" ? Opts(("mystery", 1d)) : Opts((key, value));
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Arial"), ("animators", new object[]
        {
            Opts(("properties", Opts(("opacity", 0d))), ("selector", selector)),
        }))));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void InvertAndAnimatorsAreRefusedTogether()
    {
        if (!Installed("Arial")) return;
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Arial"), ("invert", true),
            ("animators", new object[] { Opts(("properties", Opts(("opacity", 0d)))) }))));
        Assert.Contains("cannot invert and animate", e.Message);
    }
    #endregion

    #region SVG
    /// <summary>Outlines, not text: the SVG needs no font, and draws what this draws, the origin's motion exact.</summary>
    [Fact]
    public void TheSvgRouteWritesOutlinesThatDrawTheSame()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 120d), ("fps", 4d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Text(Opts(("text", "Outlines"), ("family", "Arial"), ("size", 40d), ("color", "#1d3557"),
            ("origin", motion.Nodes.Linear("vector", new[] { 60d, 0d }, new[] { 120d, 60d }))));

        var svg = comp.ToSvg();
        Assert.DoesNotContain("<text", svg);
        Assert.Contains("<path", svg);
        foreach (var t in new[] { 0, 0.4, 1 })
        {
            using var ours = comp.Render(t);
            using var theirs = SKBitmap.Decode(Polson.Drawing.Svg.SvgRenderPipeline.RenderToImage(svg, comp.Width, comp.Height, "png", 100, null, TimeSpan.FromSeconds(t)));
            var differing = Differing(ours.SkBitmap, theirs, 48);
            output.WriteLine($"svg {t}s: {differing:P2} differ by more than 48");
            Assert.True(differing < 0.01, $"{differing:P2} differ at {t}s");
        }
    }

    [Fact]
    public void TheSvgRouteAnimatesLettersAndRefusesWhatItCannotSay()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 120d), ("fps", 4d), ("duration", 1d)));
        comp.Text(Opts(("text", "Up"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 60d }), ("animators", new object[]
        {
            Opts(("properties", Opts(("position", new[] { 0d, -30d }))), ("selector", Opts(("offset", motion.Nodes.Linear("real", 100, -100))))),
        })));
        Assert.Contains("animateTransform", comp.ToSvg());

        var keyed = motion.Composition(Opts(("duration", 1d)));
        keyed.Text(Opts(("text", "Grow"), ("family", "Arial"), ("size", motion.Nodes.Linear("real", 10, 20)), ("desc", "title")));
        var e = Assert.Throws<InvalidOperationException>(() => keyed.ToSvg());
        Assert.Contains("'title'", e.Message);
        Assert.Contains("size", e.Message);

        var blurred = motion.Composition(Opts(("duration", 1d)));
        blurred.Text(Opts(("text", "Soft"), ("family", "Arial"), ("animators", new object[] { Opts(("properties", Opts(("blur", 4d)))) })));
        e = Assert.Throws<InvalidOperationException>(() => blurred.ToSvg());
        Assert.Contains("blur", e.Message);
    }
    #endregion

    #region Private
    private static int Ink(MotionComposition comp, double t, int x0, int x1)
    {
        using var frame = comp.Render(t);
        var bmp = frame.SkBitmap;
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = x0; x < x1; x++)
                if (bmp.GetPixel(x, y).Alpha > 64) n++;
        return n;
    }

    /// <summary>The box of every pixel with any coverage, edges inclusive, as a rectangle of pixel edges.</summary>
    private static SKRect InkBox(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 0)
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
        return new SKRect(minX, minY, maxX + 1, maxY + 1);
    }

    private static int[] InkRows(SKBitmap bmp, int x0, int x1) =>
        [.. Enumerable.Range(0, bmp.Height).Where(y => Enumerable.Range(x0, x1 - x0).Any(x => bmp.GetPixel(x, y).Alpha > 128))];

    private static MotionComposition Ground(MotionToolkit motion)
    {
        var comp = motion.Composition(Opts(("width", 400d), ("height", 240d), ("fps", 2d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        return comp;
    }

    private void Compare(string synfig, MotionComposition comp, string name, double bar)
    {
        var theirs = RenderWithSynfig(synfig, comp, name);
        Assert.Equal(comp.FrameCount, theirs.Length);
        var drewAny = 0.0;
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i / comp.Fps);
            using var bitmap = theirs[i];
            Dump(ours.SkBitmap, bitmap, $"{name}-{i}");

            using var ground = new SKBitmap(bitmap.Width, bitmap.Height);
            using (var c = new SKCanvas(ground)) c.Clear(SKColor.Parse("#f2efe8"));
            var drawn = Differing(ground, bitmap, 24);
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"{name} frame {i}: {differing:P2} differ from synfig by more than 24, "
                + $"{Differing(ours.SkBitmap, bitmap, 96):P2} by more than 96; synfig drew on {drawn:P2}");
            drewAny = Math.Max(drewAny, drawn);
            Assert.True(differing < bar, $"{name} differs from synfig in {differing:P2} at frame {i}");
        }
        // A frame may rightly be empty (every letter faded out); a run in which Synfig drew nothing at all means it dropped the layer.
        Assert.True(drewAny > 0.01, "synfig drew nothing in any frame: did it drop the layer?");
    }

    private static bool Installed(string family)
    {
        using var face = SKFontManager.Default.MatchFamily(family);
        return face is not null && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase);
    }

    private SKBitmap[] RenderWithSynfig(string synfig, MotionComposition comp, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "polson-synfig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var sif = comp.SaveSif(Path.Combine(dir, name + ".sif"));
            var start = new ProcessStartInfo(synfig)
            {
                WorkingDirectory = Path.GetDirectoryName(synfig)!,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            foreach (var a in new[] { "-q", sif, "-t", "png", "-o", Path.Combine(dir, name + ".png") }) start.ArgumentList.Add(a);

            using var process = Process.Start(start)!;
            var err = process.StandardError.ReadToEndAsync();
            var outp = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "synfig did not finish in two minutes");
            var log = string.Join("\n", (err.Result + outp.Result).Split('\n')
                .Where(l => !l.Contains("info:") && !l.Contains("Unable to find module") && l.Trim().Length > 0));
            if (log.Length > 0) output.WriteLine(log);
            Assert.True(process.ExitCode == 0, $"synfig exited {process.ExitCode}: {log}");
            Assert.DoesNotContain("error", log, StringComparison.OrdinalIgnoreCase);

            var files = Directory.GetFiles(dir, name + ".*.png").Order(StringComparer.Ordinal).ToArray();
            if (files.Length == 0) files = Directory.GetFiles(dir, name + ".png");
            // Through .NET, not SKBitmap.Decode(path): see MotionBlendTests.
            return [.. files.Select(f => SKBitmap.Decode(File.ReadAllBytes(f)))];
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Dump(SKBitmap ours, SKBitmap theirs, string name)
    {
        if (Environment.GetEnvironmentVariable("POLSON_SYNFIG_DUMP") is not { Length: > 0 } dump) return;
        Directory.CreateDirectory(dump);
        File.WriteAllBytes(Path.Combine(dump, $"ours-{name}.png"), ours.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        File.WriteAllBytes(Path.Combine(dump, $"theirs-{name}.png"), theirs.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    private static string? Synfig()
    {
        var path = Environment.GetEnvironmentVariable("POLSON_SYNFIG");
        if (string.IsNullOrEmpty(path))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "reference"))) dir = dir.Parent;
            path = dir is null ? null : Path.Combine(dir.FullName, "reference", "apps", "synfig", "bin", "synfig.exe");
        }

        return path is not null && File.Exists(path) ? path : null;
    }
    #endregion
}
