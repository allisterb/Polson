namespace Polson.Tests.Drawing;

using System;
using System.Collections.Generic;
using Polson.Drawing.Skia;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>createHeadForFigure</c> — the seam between Studio Manual 23's head and Manual 08's figure.
///
/// **What is tested is the arithmetic, not the drawing.** Placing a head on a body was three
/// conversions a caller had to do itself and one it had to know about, and each failed in a way a
/// render does not announce: a head too wide for its own shoulders still looks like a head, and a
/// neck stopping a tenth of a head unit short still looks like a neck until you see the gap. So
/// these assert against the figure's own stations rather than against remembered constants.
/// </summary>
public class HeadForFigureTests : TestsRuntime
{
    const float Height = 1600f;
    const float OriginX = 500f;
    const float OriginY = 100f;
    const float Unit = Height / 8f;          // one head unit

    static ConstructiveDrawingToolkit Kit => new();

    static Dictionary<string, object?> Figure(object? options = null) =>
        Kit.CreateMannequinFigure(OriginX, OriginY, Height, options);

    static Dictionary<string, object?> Fitted(Dictionary<string, object?> fig, object? options = null) =>
        Kit.CreateHeadForFigure(fig, options);

    static Dictionary<string, object?> Sub(Dictionary<string, object?> d, string key) =>
        (Dictionary<string, object?>)d[key]!;

    static float Num(Dictionary<string, object?> d, string key) => Convert.ToSingle(d[key]!);

    static (float X, float Y) Pt(Dictionary<string, object?> d, string key)
    {
        var p = Sub(d, key);
        return (Convert.ToSingle(p["x"]), Convert.ToSingle(p["y"]));
    }

    static SKRect Silhouette(Dictionary<string, object?> head, object? options = null) =>
        ((CanvasPath)Kit.CreateHeadGeometry(head, options)["silhouette"]!).Path.Bounds;

    /// <summary>
    /// The head lands where the figure's head mass is, at the size the figure says it is.
    /// </summary>
    /// <remarks>
    /// The trap is that <c>createLoomisHead</c>'s <c>originY</c> is the head's vertical <i>centre</i>
    /// while every proportion table measures from the crown — so the natural mistake puts the head
    /// half a unit low, which on an eight-head figure is a head resting on the sternum.
    /// </remarks>
    [Fact]
    public void TestTheHeadIsPlacedAndSizedToTheFigure()
    {
        var fig = Figure();
        var head = Fitted(fig);

        var (_, crownY) = Pt(head, "crown");
        var (_, chinY) = Pt(head, "chin");

        // The canon puts the crown at the figure's origin and the chin one head unit below it.
        Assert.Equal(OriginY, crownY, 1);
        Assert.Equal(OriginY + Unit, chinY, 1);
        Assert.Equal(Unit, Num(Sub(head, "fit"), "headHeight"), 1);
    }

    /// <summary>
    /// The default skull is the width the figure already has, and it is <c>comic</c> rather than the
    /// <c>loomis</c> that every other entry point defaults to.
    /// </summary>
    /// <remarks>
    /// The figure's head egg is <c>2 * rx</c> = <c>0.72 H</c>. Loomis is <c>0.857 H</c> and overhangs
    /// its own shoulders by 19%; the comic skull is <c>5/6</c> of Loomis, <c>0.714 H</c>. So the
    /// mannequin has been carrying a comic head all along, and the surprising default is the one that
    /// matches the body.
    /// </remarks>
    [Fact]
    public void TestTheSkullDefaultsToTheWidthTheFigureAlreadyHas()
    {
        var fig = Figure();
        var egg = Convert.ToSingle(Sub(fig, "head")["rx"]!) * 2f;

        var comic = Silhouette(Fitted(fig)).Width;
        var loomis = Silhouette(Fitted(fig, new Dictionary<string, object?> { ["skull"] = "loomis" })).Width;

        Assert.True(MathF.Abs(comic - egg) / egg < 0.08f,
            $"the default skull is {comic:F1} against the figure's own {egg:F1}");
        Assert.True(loomis > comic, $"loomis {loomis:F1} should be wider than comic {comic:F1}");
    }

    /// <summary>
    /// The neck reaches the figure's shoulder line — and the unfitted default does not.
    /// </summary>
    /// <remarks>
    /// This is the assertion the helper exists for. <c>createHeadGeometry</c>'s own default of
    /// <c>0.30</c> ends at <c>1.30 H</c> while the sternal notch is at <c>1.40 H</c>, so a head
    /// composed without the fit floats a tenth of a head unit clear of the body.
    /// </remarks>
    [Fact]
    public void TestTheNeckReachesTheShoulderLine()
    {
        var fig = Figure();
        var (_, sternumY) = Pt(fig, "sternum");

        var fitted = Silhouette(Fitted(fig)).Bottom;
        var canon = Silhouette(Kit.CreateLoomisHead(OriginX, OriginY + Unit * 0.5f, Unit, 0f, 0f)).Bottom;

        Assert.True(MathF.Abs(fitted - sternumY) < Unit * 0.06f,
            $"the fitted neck ends at {fitted:F1}, shoulder line {sternumY:F1}");
        Assert.True(canon < sternumY - Unit * 0.04f,
            $"the canon neck was expected to fall short of {sternumY:F1} but reached {canon:F1}");
    }

    /// <summary>
    /// A pose changes where the shoulders are, so it changes the neck the figure needs.
    /// </summary>
    [Fact]
    public void TestAPosedFigureGetsItsOwnNeck()
    {
        var upright = Num(Sub(Fitted(Figure()), "fit"), "neckLength");
        var posed = Num(Sub(Fitted(Figure(new Dictionary<string, object?>
        {
            ["pose"] = new Dictionary<string, object?> { ["spineDeg"] = 22f, ["neckDeg"] = -8f }
        })), "fit"), "neckLength");

        Assert.NotEqual(upright, posed, 3);
    }

    /// <summary>
    /// The roll is reported rather than applied, because nothing here can apply it correctly.
    /// </summary>
    /// <remarks>
    /// <c>figure.head.angleDeg</c> is <c>spineDeg + neckDeg</c> — a lean on the page, where
    /// <c>createLoomisHead</c> takes only yaw and pitch. A caller that ignores it draws an upright
    /// face on a leaning figure, which is exactly what Manual 08 §3 warns about.
    /// </remarks>
    [Fact]
    public void TestTheRollIsReportedForTheCallerToApply()
    {
        var fig = Figure(new Dictionary<string, object?>
        {
            ["pose"] = new Dictionary<string, object?> { ["spineDeg"] = 22f, ["neckDeg"] = -8f }
        });

        var fit = Sub(Fitted(fig), "fit");
        var expected = Convert.ToSingle(Sub(fig, "head")["angleDeg"]!);

        Assert.Equal(expected, Num(fit, "rollDeg"), 3);
        Assert.NotEqual(0f, Num(fit, "rollDeg"), 3);

        var (px, py) = Pt(fit, "pivot");
        var (hx, hy) = Pt(Sub(fig, "head"), "center");
        Assert.Equal(hx, px, 2);
        Assert.Equal(hy, py, 2);
    }

    /// <summary>
    /// <c>createHeadGeometry</c> takes its defaults from the fit, and an explicit option still wins.
    /// </summary>
    [Fact]
    public void TestGeometryFollowsTheFitUnlessOverridden()
    {
        var fig = Figure();
        var head = Fitted(fig);

        var followed = Silhouette(head).Bottom;
        var overridden = Silhouette(head, new Dictionary<string, object?> { ["neckLength"] = 0.05f }).Bottom;

        Assert.True(followed > overridden + Unit * 0.2f,
            $"an explicit neckLength did not win: fit {followed:F1}, override {overridden:F1}");

        // And the skull follows too, so a fitted head is not silently composed at Loomis width.
        var wide = Silhouette(head, new Dictionary<string, object?> { ["skull"] = "loomis" }).Width;
        Assert.True(wide > Silhouette(head).Width, "the fit's skull was not being used as the default");
    }

    /// <summary>Character parameters are applied, and they reach the silhouette.</summary>
    /// <remarks>
    /// <b>Measured at the jaw angle, not by the bounding box, and that is not a convenience.</b> The
    /// jaw stations are clamped to the cranial ball, so no jaw can ever widen the silhouette's box —
    /// a squared and a tapered head come back with byte-identical bounds and different outlines.
    /// Written the obvious way, this test asserts the jaw does nothing and passes when it is broken.
    /// </remarks>
    [Fact]
    public void TestCharacterParametersReachTheSilhouette()
    {
        var fig = Figure();
        var character = new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["jawShape"] = 0.9f }
        };

        // The parameters reached the head at all.
        var plainJaw = Pt(Sub(Fitted(fig), "jaw"), "nearStation");
        var squareJaw = Pt(Sub(Fitted(fig, character), "jaw"), "nearStation");
        Assert.True(MathF.Abs(plainJaw.X - squareJaw.X) > 0.5f,
            $"a parametric jaw did not move the jaw: {plainJaw.X:F2} against {squareJaw.X:F2}");

        // And they reach the outline, which is what makes two characters read as two people.
        Dictionary<string, object?> Shaped(float jawShape) => Fitted(fig, new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["jawShape"] = jawShape }
        });

        var squaredHead = Shaped(1f);
        var squared = (CanvasPath)Kit.CreateHeadGeometry(squaredHead)["silhouette"]!;
        var tapered = (CanvasPath)Kit.CreateHeadGeometry(Shaped(-1f))["silhouette"]!;

        // The jaw is clamped to the cranial ball, so it can never widen the bounding box — the two
        // characters have byte-identical bounds and different outlines. Probe at the jaw angle,
        // which is where the jaw is on the silhouette rather than inside it.
        var angle = Pt(Sub(squaredHead, "jaw"), "nearAngle");
        var axisX = Pt(squaredHead, "chin").X;
        var probeX = axisX + (angle.X - axisX) * 0.95f;

        Assert.True(squared.Path.Contains(probeX, angle.Y), "the squared jaw did not reach the silhouette");
        Assert.False(tapered.Path.Contains(probeX, angle.Y),
            "the tapered jaw reaches as far as the squared one — the two characters share an outline");
    }

    /// <summary>A longer chin gets a shorter neck, because the neck is measured after the character.</summary>
    /// <remarks>
    /// <para>
    /// <b>The payoff of an ordering decision taken before there was anything to pay off.</b> The neck
    /// is measured chin-to-sternum <i>after</i> <c>createParametricHead</c> runs, and that order was
    /// kept when no parameter reached the top-level <c>chin</c> — on the argument that one which ever
    /// did would otherwise hang the neck off a chin that no longer existed. <c>chinLength</c> is that
    /// parameter, and this is the assertion that it landed rather than merely that it could.
    /// </para>
    /// <para>
    /// Both directions, because a test of the long half alone passes on an implementation that
    /// clamps at the canon — and a short chin needing a <i>longer</i> neck is the half a reader is
    /// more likely to doubt.
    /// </para>
    /// </remarks>
    [Fact]
    public void TestAChinLengthChangesTheNeckItIsMeasuredFor()
    {
        var fig = Figure();

        float NeckFor(float chinLength) => Num(Sub(Fitted(fig, new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["chinLength"] = chinLength }
        }), "fit"), "neckLength");

        var longChin = NeckFor(1f);
        var canon = NeckFor(0f);
        var shortChin = NeckFor(-1f);

        Assert.True(longChin < canon,
            $"a long chin should leave less neck to the sternum: {longChin:F4} against {canon:F4}");
        Assert.True(shortChin > canon,
            $"a short chin should leave more: {shortChin:F4} against {canon:F4}");
    }

    /// <summary>A misspelled option is refused by name rather than silently drawing the canon.</summary>
    [Fact]
    public void TestAnUnknownOptionIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Fitted(Figure(), new Dictionary<string, object?> { ["neckLenght"] = 0.4f }));

        Assert.Contains("neckLenght", ex.Message);
        Assert.Contains("neckLength", ex.Message);
    }

    /// <summary>Something that is not a figure is refused, naming the call that makes one.</summary>
    [Fact]
    public void TestSomethingThatIsNotAFigureIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Kit.CreateHeadForFigure(new Dictionary<string, object?> { ["head"] = "not a head" }));

        Assert.Contains("createMannequinFigure", ex.Message);
    }

    /// <summary>
    /// A larger head grows from the chin: the chin stays where the figure's is, the head is the multiple of its height
    /// asked for, and the neck keeps its length in pixels while its length in head units shrinks to match. anim3 and
    /// anim4 both drew the head 1.5× by hand with a canvas transform and re-derived every point on it.
    /// </summary>
    [Fact]
    public void TestAScaledHeadGrowsFromItsChin()
    {
        var fig = Figure(new Dictionary<string, object?> { ["pose"] = new Dictionary<string, object?> { ["spineDeg"] = 14d } });
        var canon = Fitted(fig, new Dictionary<string, object?> { ["character"] = new Dictionary<string, object?> { ["jawShape"] = 0.5d } });
        var big = Fitted(fig, new Dictionary<string, object?> { ["scale"] = 1.5d, ["character"] = new Dictionary<string, object?> { ["jawShape"] = 0.5d } });

        Assert.Equal(Pt(canon, "chin").X, Pt(big, "chin").X, 1);
        Assert.Equal(Pt(canon, "chin").Y, Pt(big, "chin").Y, 1);
        Assert.Equal(Num(Sub(canon, "unit"), "H") * 1.5f, Num(Sub(big, "unit"), "H"), 2);
        Assert.True(Silhouette(big).Top < Silhouette(canon).Top - Unit * 0.4f, "the bigger head rises above the canon one");

        var (fc, fb) = (Sub(canon, "fit"), Sub(big, "fit"));
        Assert.Equal(1.5f, Num(fb, "scale"));
        Assert.Equal(Pt(fc, "pivot"), Pt(fb, "pivot"));
        Assert.Equal(Num(fc, "neckLength") * Num(fc, "headHeight"), Num(fb, "neckLength") * Num(fb, "headHeight"), 1);
    }

    [Fact]
    public void TestAScaleOutOfRangeIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Fitted(Figure(), new Dictionary<string, object?> { ["scale"] = 0d }));
        Assert.Contains("0.25 to 4", ex.Message);
    }

    /// <summary>
    /// headPoint turns a landmark into the lean the head is drawn in, so a hand can reach the mouth on a leaning figure:
    /// the point keeps its distance from the pivot and turns by exactly the roll.
    /// </summary>
    [Fact]
    public void TestHeadPointPutsALandmarkWhereItIsDrawn()
    {
        var head = Fitted(Figure(new Dictionary<string, object?> { ["pose"] = new Dictionary<string, object?> { ["spineDeg"] = 20d } }),
            new Dictionary<string, object?> { ["scale"] = 1.5d });
        var fit = Sub(head, "fit");
        var pivot = Pt(fit, "pivot");
        var roll = Num(fit, "rollDeg");
        Assert.True(Math.Abs(roll) > 5f, "the figure leans, so the head has a roll to apply");

        var corner = Pt(Sub(head, "mouthGuides"), "leftCorner");
        var placed = Kit.HeadPoint(head, Sub(Sub(head, "mouthGuides"), "leftCorner"));
        var (px, py) = (Convert.ToSingle(placed["x"]), Convert.ToSingle(placed["y"]));

        float Dist(float x, float y) => MathF.Sqrt((x - pivot.X) * (x - pivot.X) + (y - pivot.Y) * (y - pivot.Y));
        float Angle(float x, float y) => MathF.Atan2(y - pivot.Y, x - pivot.X) * 180f / MathF.PI;
        Assert.Equal(Dist(corner.X, corner.Y), Dist(px, py), 2);
        Assert.Equal(roll, Angle(px, py) - Angle(corner.X, corner.Y), 1);

        // A head with no fit is not turned.
        var free = Kit.CreateLoomisHead(100f, 100f, 200f, 0f);
        var same = Kit.HeadPoint(free, Sub(free, "chin"));
        Assert.Equal(Pt(free, "chin").X, Convert.ToSingle(same["x"]), 4);
    }

    /// <summary>Same figure, same head — a character has to survive a page of panels.</summary>
    [Fact]
    public void TestItIsDeterministic()
    {
        var a = Silhouette(Fitted(Figure()));
        var b = Silhouette(Fitted(Figure()));

        Assert.Equal(a.Left, b.Left, 4);
        Assert.Equal(a.Top, b.Top, 4);
        Assert.Equal(a.Right, b.Right, 4);
        Assert.Equal(a.Bottom, b.Bottom, 4);
    }
}
