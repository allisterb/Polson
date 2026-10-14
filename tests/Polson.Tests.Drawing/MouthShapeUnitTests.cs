namespace Polson.Tests.Drawing;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

using Xunit;

/// <summary>
/// The mouth units speech needs: AU18 (pucker) and AU22 (funnel) round the lips, AU24 presses them shut, AU28 tucks the
/// lower lip under the upper teeth. Each raises one of the mouth's 0–1 fractions, which the mouth drawer reads.
/// </summary>
/// <remarks>As in <see cref="ActionUnitTests"/>, the magnitudes are the studio's and are not asserted; what each moves is.</remarks>
public class MouthShapeUnitTests : TestsRuntime
{
    static readonly ConstructiveDrawingToolkit Toolkit = new();

    static Dictionary<string, object?> Head(params (string Unit, float Weight)[] units) => Toolkit.ApplyActionUnits(
        Toolkit.CreateLoomisHead(300f, 300f, 600f, 0f, 0f), units.ToDictionary(u => u.Unit, u => (object?)u.Weight));

    static Dictionary<string, object?> Mouth(Dictionary<string, object?> head) => (Dictionary<string, object?>)head["mouthGuides"]!;

    static float Get(Dictionary<string, object?> head, string key) => Convert.ToSingle(Mouth(head)[key]);

    static float Spread(Dictionary<string, object?> head) =>
        Convert.ToSingle(((Dictionary<string, object?>)Mouth(head)["rightCorner"]!)["x"]) - Convert.ToSingle(((Dictionary<string, object?>)Mouth(head)["leftCorner"]!)["x"]);

    static Dictionary<string, object?> Draw(Dictionary<string, object?> head) =>
        Toolkit.DrawComicMouth(new SkiaCanvas(600, 600).GetContext("2d"), Mouth(head));

    [Fact]
    public void PuckerAndFunnelRoundTheLipsAndDrawTheCornersIn()
    {
        var rest = Head();
        var pucker = Head(("AU18", 1f));
        var funnel = Head(("AU22", 1f));
        Assert.Equal(0f, Get(rest, "round"));
        Assert.Equal(1f, Get(pucker, "round"));
        Assert.Equal(1f, Get(funnel, "round"));
        Assert.True(Spread(pucker) < Spread(funnel) && Spread(funnel) < Spread(rest), "the pucker draws the corners in furthest");

        // The pucker stays closed; the funnel opens around a round mouth.
        Assert.False((bool)Draw(pucker)["open"]!);
        Assert.True((bool)Draw(funnel)["open"]!);
    }

    /// <summary>A rounded opening stops short of the corners, where the lips meet, so the lips ring it.</summary>
    [Fact]
    public void ARoundedOpeningStopsShortOfTheCorners()
    {
        var plain = Draw(Head(("AU25", 0.5f), ("AU26", 0.5f)));
        var round = Draw(Head(("AU22", 1f), ("AU26", 0.5f)));
        static float Width(Dictionary<string, object?> parts) => ((CanvasPath)parts["cavity"]!).Path.Bounds.Width;
        var o = Head(("AU22", 1f), ("AU26", 0.5f));
        Assert.True(Width(round) < Spread(o) * 0.8f, $"the opening is {Width(round):0.0} wide in a mouth {Spread(o):0.0} wide");
        Assert.True(Width(plain) > Spread(Head(("AU25", 0.5f), ("AU26", 0.5f))) * 0.95f);
    }

    /// <summary>Pressed lips meet whatever else is opening them: M, B and P read only if they do.</summary>
    [Fact]
    public void PressedLipsShutTheMouthWhateverOpensIt()
    {
        Assert.True((bool)Draw(Head(("AU26", 0.8f), ("AU25", 0.6f)))["open"]!);
        var pressed = Head(("AU26", 0.8f), ("AU25", 0.6f), ("AU24", 1f));
        Assert.Equal(1f, Get(pressed, "press"));
        Assert.False((bool)Draw(pressed)["open"]!);
    }

    /// <summary>A tucked lower lip shows the upper teeth resting on it: F and V.</summary>
    [Fact]
    public void ATuckedLowerLipShowsTheUpperTeeth()
    {
        var tucked = Head(("AU28", 1f));
        Assert.Equal(1f, Get(tucked, "tuck"));
        var parts = Draw(tucked);
        Assert.True((bool)parts["open"]!);
        Assert.False(((CanvasPath)parts["teeth"]!).Path.IsEmpty);
        Assert.False((bool)Draw(Head())["open"]!);
    }

    [Fact]
    public void TheFractionsAddAndStopAtOne()
    {
        Assert.Equal(0.3f, Get(Head(("AU18", 0.3f)), "round"), 5);
        Assert.Equal(0.8f, Get(Head(("AU18", 0.3f), ("AU22", 0.5f)), "round"), 5);
        Assert.Equal(1f, Get(Head(("AU18", 0.8f), ("AU22", 0.8f)), "round"), 5);
    }

    [Fact]
    public void TheFractionsBlendAsFractions()
    {
        var rest = Head();
        var half = Toolkit.BlendHead(rest, rest, Head(("AU24", 1f)), 0.5f);
        Assert.Equal(0.5f, Get(half, "press"), 4);
    }

    /// <summary>Hamm, p. 11: the lip creases show up more in "o" and "u", and nearly disappear in a laugh.</summary>
    [Fact]
    public void CreasesShowInARoundMouthAndFadeInALaugh()
    {
        static bool HasCreases(Dictionary<string, object?> head, float option = 0f) =>
            !((CanvasPath)Toolkit.DrawComicMouth(new SkiaCanvas(600, 600).GetContext("2d"), Mouth(head),
                new Dictionary<string, object?> { ["creases"] = option })["creases"]!).Path.IsEmpty;

        Assert.False(HasCreases(Head()));
        Assert.True(HasCreases(Head(("AU18", 1f))), "a pucker shows its creases with the option off");
        Assert.True(HasCreases(Head(), 1f));
        Assert.False(HasCreases(Head(("AU12", 1f), ("AU25", 1f)), 1f), "a laugh stretches them out");
    }

    /// <summary>
    /// Hamm, p. 12: the outer corners of the eyes and the base of the lower lip make a near-equilateral triangle, in the
    /// straight mouth and the normal laugh alike, because a laugh parts the lips by lifting the upper one. Speech mouths
    /// that do not drop the jaw leave the base where it was too.
    /// </summary>
    [Fact]
    public void HammsTriangleHoldsAtRestAndThroughALaugh()
    {
        static (float Ratio, float BaseY) Triangle(Dictionary<string, object?> head)
        {
            static (float X, float Y) P(object? p) => (Convert.ToSingle(((Dictionary<string, object?>)p!)["x"]), Convert.ToSingle(((Dictionary<string, object?>)p!)["y"]));
            var near = P(((Dictionary<string, object?>)head["nearEye"]!)["outer"]);
            var far = P(((Dictionary<string, object?>)head["farEye"]!)["outer"]);
            var baseY = Get(head, "lowerLipY");
            var baseX = P(Mouth(head)["center"]).X;
            var top = MathF.Sqrt(MathF.Pow(near.X - far.X, 2) + MathF.Pow(near.Y - far.Y, 2));
            var side = MathF.Sqrt(MathF.Pow(near.X - baseX, 2) + MathF.Pow(near.Y - baseY, 2));
            return (side / top, baseY);
        }

        var rest = Triangle(Head());
        Assert.InRange(rest.Ratio, 0.93f, 1.07f);
        foreach (var units in new[] { new[] { ("AU12", 1f), ("AU6", 0.8f), ("AU25", 0.8f) }, [("AU24", 1f)], [("AU28", 1f)], [("AU18", 1f)] })
            Assert.Equal(rest.BaseY, Triangle(Head(units)).BaseY, 3);
    }

    /// <summary>
    /// A mouth at rest draws exactly as before the fractions existed: a head without them (an older head, or one a script
    /// built by hand) and one with them at 0 render the same pixels.
    /// </summary>
    [Fact]
    public void AtRestTheMouthIsUnchanged()
    {
        foreach (var units in new[] { Array.Empty<(string, float)>(), [("AU12", 0.8f)], [("AU25", 0.5f), ("AU26", 0.6f)] })
        {
            var head = Head(units);
            var bare = new Dictionary<string, object?>(Mouth(head));
            foreach (var key in new[] { "round", "press", "tuck" }) bare.Remove(key);
            Assert.Equal(Pixels(Mouth(head)), Pixels(bare));
        }

        static string Pixels(Dictionary<string, object?> mouth)
        {
            var canvas = new SkiaCanvas(600, 600);
            Toolkit.DrawComicMouth(canvas.GetContext("2d"), mouth, new Dictionary<string, object?> { ["tone"] = 1f, ["creases"] = 1f });
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canvas.SkBitmap.Bytes));
        }
    }
}
