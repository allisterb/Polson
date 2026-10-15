namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;
using Polson.Animation;
using Polson.Drawing.Skia;
using Xunit;

using static MotionCompositionTests;

/// <summary>
/// <c>Drawing.applyFaceChannels</c> and <c>Drawing.drawFace</c>: the face layer's two steps, callable on any head. A figure
/// drawn every frame builds its own head to follow the body, so without these it could not have what <c>comp.face</c>
/// gives a free-standing head: anim3's keeper got default features and a hand-rolled mouth.
/// </summary>
public class FaceOnAnyHeadTests : TestsRuntime
{
    #region Fields
    static readonly ConstructiveDrawingToolkit Toolkit = new();

    /// <summary>Every kind of channel: an expression, a unit, a one-sided unit, an eye-wheel setting and a pose.</summary>
    static Dictionary<string, object?> Channels() =>
        Opts(("joy", 0.6d), ("AU45", 0.5d), ("near.AU2", 0.8d), ("upperLid", 2d), ("O", 1d));

    static Dictionary<string, object?> Poses() => Opts(("O", Opts(("AU26", 0.55d), ("AU22", 0.6d))));

    /// <summary>A look away from every default, so a dropped option shows as different pixels.</summary>
    static Dictionary<string, object?> Look() => Opts(("skin", "#e8c9a0"), ("ink", "#202430"),
        ("nose", Opts(("treatment", "male3"))), ("mouth", Opts(("full", 0.5d))), ("brow", Opts(("weight", 1.2d))));
    #endregion

    #region Tests
    /// <summary>
    /// A head built by hand, moved by <c>applyFaceChannels</c> and drawn by <c>drawFace</c>, is the face layer's own frame,
    /// pixel for pixel: the layer is built from the same two calls, so the routes cannot drift.
    /// </summary>
    [Fact]
    public void AHandBuiltFaceIsTheFaceLayersFramePixelForPixel()
    {
        Dictionary<string, object?> Comp() => Opts(("width", 400d), ("height", 400d), ("duration", 1d));
        var layer = new MotionToolkit().Composition(Comp());
        layer.Face(Opts(("origin", new[] { 200d, 200d }), ("height", 300d), ("yaw", 20d), ("character", Opts(("jawShape", 0.4d))),
            ("channels", Channels()), ("poses", Poses()), ("look", Look())));

        var byHand = new MotionToolkit().Composition(Comp());
        byHand.Drawn((ctx, v, t) =>
        {
            var head = Toolkit.CreateParametricHead(Toolkit.CreateLoomisHead(200f, 200f, 300f, 20f, 0f), Opts(("jawShape", 0.4f)));
            head = Toolkit.ApplyFaceChannels(head, Channels(), Opts(("poses", Poses())));
            Toolkit.DrawFace(ctx, head, Look());
        });

        using var a = layer.Render(0);
        using var b = byHand.Render(0);
        var pa = a.SkBitmap.GetPixelSpan().ToArray();
        var pb = b.SkBitmap.GetPixelSpan().ToArray();
        Assert.Equal(pa.Length, pb.Length);
        var differing = Enumerable.Range(0, pa.Length / 4).Count(i => pa[i * 4] != pb[i * 4] || pa[i * 4 + 1] != pb[i * 4 + 1]
            || pa[i * 4 + 2] != pb[i * 4 + 2] || pa[i * 4 + 3] != pb[i * 4 + 3]);
        Assert.Equal(0, differing);
        Assert.True(pa.Where((_, i) => i % 4 == 3).Count(x => x > 0) > 20000, "the face was drawn at all");
    }

    /// <summary>Each kind of channel does what its own call does: a unit, an expression, a side, the eye wheel, a pose.</summary>
    [Fact]
    public void EachChannelIsTheCallItNames()
    {
        var head = Toolkit.CreateLoomisHead(200f, 200f, 300f, 20f, 0f);
        (float X, float Y) P(Dictionary<string, object?> h, string group, string point)
        {
            var p = (IDictionary<string, object?>)((IDictionary<string, object?>)h[group]!)[point]!;
            return (Convert.ToSingle(p["x"]), Convert.ToSingle(p["y"]));
        }
        float V(Dictionary<string, object?> h, string group, string key) => Convert.ToSingle(((IDictionary<string, object?>)h[group]!)[key]);

        Assert.Equal(P(Toolkit.ApplyActionUnits(head, Opts(("AU12", 0.8f))), "mouthGuides", "rightCorner"),
            P(Toolkit.ApplyFaceChannels(head, Opts(("AU12", 0.8d))), "mouthGuides", "rightCorner"));
        Assert.Equal(P(Toolkit.ApplyFacialExpression(head, "joy", 0.6f), "mouthGuides", "rightCorner"),
            P(Toolkit.ApplyFaceChannels(head, Opts(("joy", 0.6d))), "mouthGuides", "rightCorner"));
        Assert.Equal(V(Toolkit.EyeWheel(head, Opts(("upperLid", 4f))), "nearEye", "height"),
            V(Toolkit.ApplyFaceChannels(head, Opts(("upperLid", 4d))), "nearEye", "height"), 4);

        // A side: near.AU45 shuts the near eye (its closure, which the eye drawer shuts it by) and leaves the far one.
        var wink = Toolkit.ApplyFaceChannels(head, Opts(("near.AU45", 1d)));
        Assert.Equal(1f, V(wink, "nearEye", "closure"));
        Assert.False(((IDictionary<string, object?>)wink["farEye"]!).TryGetValue("closure", out var far) && Convert.ToSingle(far) > 0f, "the far eye stays open");

        // A pose blends its units in by its weight: half of O is half its units.
        var half = Toolkit.ApplyFaceChannels(head, Opts(("O", 0.5d)), Opts(("poses", Poses())));
        var byUnits = Toolkit.ApplyActionUnits(head, Opts(("AU26", 0.275f), ("AU22", 0.3f)));
        Assert.Equal(P(byUnits, "jaw", "chinNear"), P(half, "jaw", "chinNear"));

        // The head handed in is never written to.
        Assert.Equal(P(Toolkit.CreateLoomisHead(200f, 200f, 300f, 20f, 0f), "jaw", "chinNear"), P(head, "jaw", "chinNear"));
    }

    /// <summary>A head fitted to a figure keeps its fit through the channels, so it can still be drawn in the figure's lean.</summary>
    [Fact]
    public void AFiguresHeadKeepsItsFitAndDraws()
    {
        var fig = Toolkit.CreateMannequinFigure(200f, 40f, 520f, Opts(("pose", Opts(("spineDeg", 14d))))!);
        var head = Toolkit.ApplyFaceChannels(Toolkit.CreateHeadForFigure(fig), Opts(("joy", 1d), ("AU45", 1d)));
        Assert.NotNull(head["fit"]);

        using var canvas = new SkiaCanvas(400, 600);
        var drawn = Toolkit.DrawFace(canvas.GetContext("2d"), head, Opts(("nose", Opts(("treatment", "female4")))));
        Assert.IsType<CanvasPath>(drawn["silhouette"]);
        Assert.IsType<CanvasPath>(drawn["mass"]);
    }

    /// <summary>A wrong name is refused with the names that would do, rather than drawing a face that ignores it.</summary>
    [Fact]
    public void AWrongNameIsRefusedByName()
    {
        var head = Toolkit.CreateLoomisHead(200f, 200f, 300f, 20f, 0f);
        var unknown = Assert.Throws<ArgumentException>(() => Toolkit.ApplyFaceChannels(head, Opts(("smile", 1d))));
        Assert.Contains("is not a channel", unknown.Message);

        var shadowing = Opts(("poses", Opts(("joy", Opts(("AU12", 1d))))));
        var taken = Assert.Throws<ArgumentException>(() => Toolkit.ApplyFaceChannels(head, null, shadowing));
        Assert.Contains("name a channel already has", taken.Message);

        using var canvas = new SkiaCanvas(10, 10);
        var look = Assert.Throws<ArgumentException>(() => Toolkit.DrawFace(canvas.GetContext("2d"), head, Opts(("hair", "red"))));
        Assert.Contains("look", look.Message);
    }
    #endregion
}
