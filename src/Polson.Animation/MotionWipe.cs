// The wipes' parameters and edges follow EffectCraft (crates/effects/src/misc.rs: Linear Wipe, Radial Wipe, Venetian
// Blinds; crates/effects/src/transition.rs: Iris Wipe), Copyright the ArtCraft Team and contributors, MIT OR Apache-2.0:
// a mask that is the identity at completion 0, with a linear feather k = clamp((d − edge) / feather + ½).

namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// A wipe: everything below it in its stack hidden as <c>completion</c> runs from 0 to 1 — linear, radial, iris, blinds or
/// matte.
/// It is an eraser, Synfig's alpha over, so it stops at its own group and goes to <c>.sif</c> as a Synfig layer that erases.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>linear</b>: an edge crossing the frame along <c>angle</c> (90: left to right), from beyond one side to beyond
/// the other, feather included, as EffectCraft's Linear Wipe. With <c>over</c>, a box, it crosses the box instead and
/// erases only inside it, so a key's duration is the time the edge takes over the shape rather than over the frame.</item>
/// <item><b>radial</b>: a sweep about <c>center</c> from <c>startAngle</c> (clockwise from up), <c>clockwise</c>,
/// <c>counterclockwise</c> or <c>both</c>. Unlike EffectCraft's, its edge starts half a feather before the start line and
/// ends half a feather past the end, so it is the identity at 0 and empty at 1 with a feather, as the linear one is.</item>
/// <item><b>iris</b>: a circle, or a polygon or star of <c>points</c> (<c>innerRadius</c> a fraction of the outer), turned
/// by <c>rotation</c>, growing from <c>center</c> until it covers the frame; <c>invert</c> erases outside it instead, a shape
/// closing down. Its feather is Synfig's shape feather, a blur of the shape, so it goes out exactly as a feathered circle
/// or region.</item>
/// <item><b>blinds</b>: bands across <c>angle</c>, <c>width</c> apart, each closing as completion rises, as Venetian
/// Blinds.</item>
/// <item><b>matte</b>: a picture's greys decide the order, the darkest erased first and the lightest last, as After
/// Effects' Gradient Wipe; <c>invert</c> takes the lightest first. The picture lies between <c>tl</c> and <c>br</c> (the
/// frame when left out) and erases only there; its feather is a share of the grey range, 0 to 1. Synfig has no such
/// layer, so the <c>.sif</c> holds each distinct mask baked as a picture.</item>
/// </list>
/// Distances are in the stack's units, and the frame is the composition's, carried into those units.
/// </remarks>
internal sealed class MotionWipeLayer : MotionLayer
{
    #region Constructors
    public MotionWipeLayer(MotionOptions o, MotionLayerList stack) : base(o)
    {
        this.stack = stack;
        type = o.Raw("type") as string ?? throw new ArgumentException("wipe's type is 'linear', 'radial', 'iris', 'blinds' or 'matte'.");
        string[] allowed = type switch
        {
            "linear" => ["angle", "over"],
            "radial" => ["startAngle", "center", "sweep"],
            "iris" => ["center", "points", "innerRadius", "rotation", "invert"],
            "blinds" => ["angle", "width"],
            "matte" => ["image", "tl", "br", "invert"],
            _ => throw new ArgumentException($"wipe has no type '{type}'. It takes 'linear', 'radial', 'iris', 'blinds' or 'matte'."),
        };
        string[] all = ["angle", "over", "startAngle", "center", "sweep", "points", "innerRadius", "rotation", "invert", "width", "image", "tl", "br"];
        foreach (var key in all.Where(k => o.Has(k) && !allowed.Contains(k)))
            throw new ArgumentException($"A {type} wipe has no '{key}'. It takes completion, feather, {string.Join(", ", allowed)}, desc.");

        completion = o.Node("completion", MotionType.Real, 0d);
        feather = o.Node("feather", MotionType.Real, 0d);
        angle = o.Node("angle", MotionType.Angle, 90d);
        startAngle = o.Node("startAngle", MotionType.Angle, 0d);
        center = o.Has("center") ? o.Node("center", MotionType.Vector, new[] { 0d, 0d }) : null;
        width = o.Node("width", MotionType.Real, 30d);
        rotation = o.Node("rotation", MotionType.Angle, 0d);
        sweep = o.Raw("sweep") as string ?? "clockwise";
        if (sweep is not ("clockwise" or "counterclockwise" or "both"))
            throw new ArgumentException("wipe's sweep is 'clockwise', 'counterclockwise' or 'both'.");
        points = o.Has("points") ? (int)o.Number("points", 0) : 0;
        if (o.Has("points") && points < 3) throw new ArgumentException("wipe's points makes a polygon or star: 3 or more.");
        inner = o.Number("innerRadius", 1);
        if (inner is <= 0 or > 1) throw new ArgumentException("wipe's innerRadius is a fraction of the outer radius, more than 0 and at most 1: it makes the polygon a star.");
        invert = o.Bool("invert", false);
        over = o.Has("over") ? Box(o.Raw("over")) : null;
        if (type == "matte")
        {
            picture = MotionPicture.From(o.Raw("image"), "a matte wipe");
            luma = Luma(picture);
            tl = o.Has("tl") ? o.Node("tl", MotionType.Vector, new[] { 0d, 0d }) : null;
            br = o.Has("br") ? o.Node("br", MotionType.Vector, new[] { 0d, 0d }) : null;
        }
    }
    #endregion

    #region Fields
    private const double Far = 1e5;
    private readonly MotionLayerList stack;
    private readonly string type, sweep;
    private readonly MotionNode completion, feather, angle, startAngle, width, rotation;
    private readonly MotionNode? center;
    private readonly int points;
    private readonly double inner;
    private readonly bool invert;
    private readonly SKRect? over;
    private readonly MotionPicture? picture;
    private readonly float[] luma = [];
    private readonly MotionNode? tl, br;
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() =>
        new[] { Amount, completion, feather, angle, startAngle, width, rotation }.Concat(new[] { center, tl, br }.OfType<MotionNode>());

    /// <summary>The composition's frame in this stack's units, from the space the stack is drawn in.</summary>
    private SKPoint[] Frame(MotionSpace space)
    {
        var v = stack.Root?.View ?? [0, 0, 100, 100];
        SKPoint[] corners = [new((float)v[0], (float)v[1]), new((float)v[2], (float)v[1]), new((float)v[2], (float)v[3]), new((float)v[0], (float)v[3])];
        return space.Plane.TryInvert(out var back) ? [.. corners.Select(back.MapPoint)] : corners;
    }

    private SKPoint Centre(double time, SKPoint[] frame)
    {
        if (center is not null) { var c = center.Evaluate(time); return new((float)c[0], (float)c[1]); }
        return new(frame.Average(p => p.X), frame.Average(p => p.Y));
    }

    private double Done(double time) => Math.Clamp(completion.Evaluate(time)[0], 0, 1);

    private double Feather(double time) => Math.Max(0, feather.Evaluate(time)[0]);

    /// <summary>A direction from an angle measured clockwise from up, y down: 90 points right.</summary>
    private static (double X, double Y) Dir(double degrees)
    {
        var (s, c) = Math.SinCos(degrees * Math.PI / 180);
        return (s, -c);
    }

    // ------------------------------------------------------------------ the geometry, shared by the render and the exports

    /// <summary><c>over</c>: <c>[x0, y0, x1, y1]</c>, or <c>{ x, y, width, height }</c> as <c>text.boundsAt</c> gives it.</summary>
    private static SKRect Box(object? value)
    {
        const string what = "wipe's over is a box in the stack's units: [x0, y0, x1, y1], or { x, y, width, height } as text.boundsAt(t) gives it";
        double N(object? x, string name) => MotionTypes.Read(x, MotionType.Real, $"wipe's over {name}")[0];
        double[] b = value switch
        {
            IList { Count: 4 } l => [.. l.Cast<object?>().Select((x, i) => N(x, $"[{i}]"))],
            _ when JsInterop.AsDict(value) is { } d && new[] { "x", "y", "width", "height" }.All(d.Contains) =>
                [N(d["x"], "x"), N(d["y"], "y"), N(d["x"], "x") + N(d["width"], "width"), N(d["y"], "y") + N(d["height"], "height")],
            _ => throw new ArgumentException(what + "."),
        };
        var box = new SKRect((float)Math.Min(b[0], b[2]), (float)Math.Min(b[1], b[3]), (float)Math.Max(b[0], b[2]), (float)Math.Max(b[1], b[3]));
        return box.Width > 0 && box.Height > 0 ? box : throw new ArgumentException(what + ", and this one has no width or no height.");
    }

    private static SKPoint[] Corners(SKRect r) => [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];

    /// <summary>Linear: the direction, the edge along it, and the extent across it of what it crosses — the frame, or <c>over</c>.</summary>
    private ((double X, double Y) D, double Edge, double F, double Vmin, double Vmax, double Umin) Linear(double time, SKPoint[] frame)
    {
        var d = Dir(angle.Evaluate(time)[0]);
        var f = Feather(time);
        var crossed = over is { } box ? Corners(box) : frame;
        var u = crossed.Select(p => p.X * d.X + p.Y * d.Y).ToArray();
        var v = crossed.Select(p => -p.X * d.Y + p.Y * d.X).ToArray();
        var edge = u.Min() - f + (u.Max() - u.Min() + 2 * f) * Done(time);
        return (d, edge, f, v.Min(), v.Max(), u.Min());
    }

    /// <summary>
    /// A hard linear wipe's erased region: the band behind the edge, four points; or with <c>over</c> the box's part behind
    /// it, always five (a box cut by a line has at most five corners; fewer are padded by repeating the last, none by one
    /// corner five times), so an animated <c>d</c> or <c>bline</c> keeps its length from frame to frame.
    /// </summary>
    private SKPoint[] Erased(((double X, double Y) D, double Edge, double F, double Vmin, double Vmax, double Umin) l)
    {
        if (over is not { } box) return BandOf(l.D, l.Umin - Far, l.Edge, l.Vmin - Far, l.Vmax + Far);
        double U(SKPoint p) => p.X * l.D.X + p.Y * l.D.Y;
        var corners = Corners(box);
        var kept = new List<SKPoint>();
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (corners[i], corners[(i + 1) % 4]);
            if (U(a) <= l.Edge) kept.Add(a);
            if ((U(a) <= l.Edge) != (U(b) <= l.Edge))
            {
                var s = (float)((l.Edge - U(a)) / (U(b) - U(a)));
                kept.Add(new(a.X + s * (b.X - a.X), a.Y + s * (b.Y - a.Y)));
            }
        }
        if (kept.Count == 0) kept.Add(corners[0]);
        while (kept.Count < 5) kept.Add(kept[^1]);
        return [.. kept];
    }

    private static SKPoint P((double X, double Y) d, double u, double v) => new((float)(u * d.X - v * d.Y), (float)(u * d.Y + v * d.X));

    /// <summary>A rectangle in the (along, across) frame of direction <paramref name="d"/>, as four points.</summary>
    private static SKPoint[] BandOf((double X, double Y) d, double u0, double u1, double v0, double v1) =>
        [P(d, u0, v0), P(d, u1, v0), P(d, u1, v1), P(d, u0, v1)];

    /// <summary>The erased share along one blind, 0 to <c>width</c>, as gradient stops: erase = 1 − clamp((d − done·w)/f + ½).</summary>
    private static (float Pos, float Erase)[] BlindStops(double done, double w, double f)
    {
        double K(double d) => f <= 0 ? (d >= done * w ? 1 : 0) : Math.Clamp((d - done * w) / f + 0.5, 0, 1);
        var at = new[] { 0, done * w - f / 2, done * w + f / 2, w }.Select(x => Math.Clamp(x, 0, w)).Distinct().Order().ToList();
        if (f <= 0) at = [0, done * w, done * w, w];
        var stops = at.Select((d, i) => (Pos: (float)(d / w), Erase: (float)(1 - (f <= 0 && i == 1 ? 0 : K(d))))).ToList();
        if (f <= 0) stops[2] = ((float)done, 0);
        return [.. stops];
    }

    /// <summary>The erased share round a radial wipe, by angle from the start in degrees (0–360), as gradient stops.</summary>
    private (float Pos, float Erase)[] SweepStops(double done, double f)
    {
        var both = sweep == "both";
        var span = both ? 180 : 360;
        var edge = -f / 2 + (span + f) * done;
        double E(double rho)
        {
            var a = sweep switch { "counterclockwise" => 360 - rho, "both" => Math.Min(rho, 360 - rho), _ => rho };
            return f <= 0 ? (a < edge ? 1 : 0) : 1 - Math.Clamp((a - edge) / f + 0.5, 0, 1);
        }
        var breaks = new List<double> { 0, 360, edge - f / 2, edge + f / 2, 360 - edge - f / 2, 360 - edge + f / 2 }
            .Where(x => x is >= 0 and <= 360).Distinct().Order().ToList();
        var stops = new List<(float, float)>();
        foreach (var x in breaks)
        {
            if (f <= 0)
            {
                // A hard edge as two stops at one angle: the value just before and just after.
                stops.Add(((float)(x / 360), (float)E(Math.Max(0, x - 1e-6))));
                stops.Add(((float)(x / 360), (float)E(Math.Min(360, x + 1e-6))));
            }
            else stops.Add(((float)(x / 360), (float)E(x)));
        }
        return [.. stops];
    }

    /// <summary>Iris: the shape's radius now, and its vertices (none for a circle).</summary>
    private (SKPoint C, double R, SKPoint[] Vertices, double F) Iris(double time, SKPoint[] frame)
    {
        var c = Centre(time, frame);
        var f = Feather(time);
        var reach = frame.Max(p => SKPoint.Distance(p, c));
        var cover = points >= 3 ? reach / (inner * Math.Cos(Math.PI / points)) : reach;
        var full = cover + 2 * f;
        var r = invert ? (1 - Done(time)) * full : Done(time) * full;
        if (points < 3) return (c, r, [], f);
        var turn = rotation.Evaluate(time)[0];
        var n = inner < 1 ? points * 2 : points;
        var vertices = Enumerable.Range(0, n).Select(i =>
        {
            var rr = inner < 1 && i % 2 == 1 ? r * inner : r;
            var d = Dir(turn + 360.0 * i / n);
            return new SKPoint((float)(c.X + rr * d.X), (float)(c.Y + rr * d.Y));
        }).ToArray();
        return (c, r, vertices, f);
    }

    /// <summary>Radial with a hard edge: the centre and five points far out along the erased arc, a wedge.</summary>
    private SKPoint[] Wedge(double time, SKPoint c)
    {
        var start = startAngle.Evaluate(time)[0];
        var done = Done(time);
        var (from, to) = sweep switch
        {
            "counterclockwise" => (start - 360 * done, start),
            "both" => (start - 180 * done, start + 180 * done),
            _ => (start, start + 360 * done),
        };
        return [c, .. Enumerable.Range(0, 5).Select(i => { var d = Dir(from + (to - from) * i / 4); return new SKPoint((float)(c.X + Far * d.X), (float)(c.Y + Far * d.Y)); })];
    }

    /// <summary>Each pixel's grey, Rec. 601, over black: a transparent pixel reads as black and goes first.</summary>
    private static float[] Luma(MotionPicture picture)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(picture.Width, picture.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bitmap)) { c.Clear(SKColors.Black); c.DrawImage(picture.Image, 0, 0); }
        var px = bitmap.GetPixelSpan();
        var luma = new float[picture.Width * picture.Height];
        for (var i = 0; i < luma.Length; i++)
            luma[i] = (0.299f * px[i * 4] + 0.587f * px[i * 4 + 1] + 0.114f * px[i * 4 + 2]) / 255f;
        return luma;
    }

    /// <summary>Matte: where the picture lies, its top-left and bottom-right corners — the frame's when not given.</summary>
    private (SKPoint Tl, SKPoint Br) Placed(double time, SKPoint[] frame)
    {
        static SKPoint At(MotionNode n, double t) { var v = n.Evaluate(t); return new((float)v[0], (float)v[1]); }
        return (tl is null ? frame[0] : At(tl, time), br is null ? frame[2] : At(br, time));
    }

    /// <summary>
    /// Matte: the erased share of each pixel at <paramref name="done"/>, black with that alpha, at the picture's size. A grey g
    /// (1 − g with <c>invert</c>) goes once completion passes it; a feather f spreads that over a band of greys,
    /// erase = clamp((done · (1 + f) − g) / f), so nothing goes at 0 and everything at 1.
    /// </summary>
    private SKBitmap Mask(double done, double f)
    {
        var bitmap = new SKBitmap(new SKImageInfo(picture!.Width, picture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var px = bitmap.GetPixelSpan();
        for (var i = 0; i < luma.Length; i++)
        {
            var g = invert ? 1 - luma[i] : luma[i];
            var erase = done >= 1 ? 1 : f <= 0 ? (g < done ? 1 : 0) : Math.Clamp((done * (1 + f) - g) / f, 0, 1);
            px[i * 4 + 3] = (byte)Math.Round(erase * 255);
        }
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    // ------------------------------------------------------------------ the render

    /// <summary>
    /// Nothing in the frame is erased: at amount 0, or at completion 0 — except feathered blinds, whose bands begin half
    /// erased. An inverted iris at 0 is a circle reaching the frame's farthest corner plus twice its feather, wider than the
    /// feather softens, so it erases nothing either (trailer4 probed it: nothing visible).
    /// </summary>
    internal override bool IsVoid(double time) =>
        AmountAt(time) <= 0 || (Done(time) <= 0 && (type != "blinds" || Feather(time) <= 0));

    protected override void Draw(SkiaCanvas surface, double time, double amount)
    {
        var canvas = surface.SkCanvas;
        var frame = Frame(stack.Root?.Space ?? MotionSpace.Root);
        var done = Done(time);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        switch (type)
        {
            case "linear":
            {
                var l = Linear(time, frame);
                var (d, edge, f) = (l.D, l.Edge, l.F);
                if (f <= 0)
                {
                    using var band = Poly(Erased(l));
                    canvas.DrawPath(band, ink);
                    return;
                }
                using var shader = SKShader.CreateLinearGradient(P(d, edge - f / 2, 0), P(d, edge + f / 2, 0),
                    [SKColors.Black, SKColors.Transparent], [0, 1], SKShaderTileMode.Clamp);
                ink.Shader = shader;
                if (over is { } box) canvas.DrawRect(box, ink);
                else canvas.DrawPaint(ink);
                return;
            }
            case "blinds":
            {
                var d = Dir(angle.Evaluate(time)[0] + 90);    // the bands run across the blinds' direction
                var w = Math.Max(1e-6, width.Evaluate(time)[0]);
                var f = Feather(time);
                if (f <= 0)
                {
                    if (done <= 0) return;
                    var u = frame.Select(p => p.X * d.X + p.Y * d.Y).ToArray();
                    var v = frame.Select(p => -p.X * d.Y + p.Y * d.X).ToArray();
                    using var bands = new SKPath();
                    for (var k = Math.Floor(u.Min() / w) - 1; k <= Math.Ceiling(u.Max() / w); k++)
                        bands.AddPoly(BandOf(d, k * w, k * w + done * w, v.Min() - 1, v.Max() + 1));
                    canvas.DrawPath(bands, ink);
                    return;
                }
                var stops = BlindStops(done, w, f);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), P(d, w, 0),
                    [.. stops.Select(s => SKColors.Black.WithAlpha((byte)Math.Round(s.Erase * 255)))], [.. stops.Select(s => s.Pos)], SKShaderTileMode.Repeat);
                ink.Shader = shader;
                canvas.DrawPaint(ink);
                return;
            }
            case "matte":
            {
                var (a, b) = Placed(time, frame);
                if (done <= 0 || a.X == b.X || a.Y == b.Y) return;
                using var mask = Mask(done, Feather(time));
                using var image = SKImage.FromBitmap(mask);
                canvas.DrawImage(image, SKRect.Create(0, 0, mask.Width, mask.Height), new SKRect(a.X, a.Y, b.X, b.Y),
                    new SKSamplingOptions(SKFilterMode.Linear), ink);
                return;
            }
            case "radial":
            {
                var c = Centre(time, frame);
                var f = Feather(time);
                if (f <= 0)
                {
                    if (done <= 0) return;
                    if (done >= 1) { canvas.DrawPaint(ink); return; }
                    using var wedge = Poly(Wedge(time, c));
                    canvas.DrawPath(wedge, ink);
                    return;
                }
                var stops = SweepStops(done, f);
                // Skia's sweep starts at +x and turns clockwise in y-down units: turned so 0 lies along the start angle.
                var local = SKMatrix.CreateRotationDegrees((float)(startAngle.Evaluate(time)[0] - 90), c.X, c.Y);
                using var shader = SKShader.CreateSweepGradient(c, [.. stops.Select(s => SKColors.Black.WithAlpha((byte)Math.Round(s.Erase * 255)))],
                    [.. stops.Select(s => s.Pos)], local);
                ink.Shader = shader;
                canvas.DrawPaint(ink);
                return;
            }
            default:
            {
                var (c, r, vertices, f) = Iris(time, frame);
                using var shape = new SKPath { FillType = invert ? SKPathFillType.InverseWinding : SKPathFillType.Winding };
                if (vertices.Length == 0) shape.AddCircle(c.X, c.Y, (float)Math.Max(0, r));
                else shape.AddPoly(vertices);
                if (f <= 0)
                {
                    if (!invert && r <= 0) return;
                    canvas.DrawPath(shape, ink);
                    return;
                }
                DrawFeathered(canvas, surface, shape, f);
                return;
            }
        }
    }

    /// <summary>
    /// The shape blurred as Synfig feathers a shape — its coverage blurred by the fast Gaussian of size <c>feather</c> — and
    /// drawn back in black. Device-sized, so the blur is in pixels at the stack's scale.
    /// </summary>
    private static void DrawFeathered(SKCanvas canvas, SkiaCanvas surface, SKPath shape, double feather)
    {
        var matrix = MotionLayerList.Tangent(canvas.TotalMatrix, surface.Width / 2f, surface.Height / 2f);
        var (w, h) = (surface.Width, surface.Height);
        using var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var mc = new SKCanvas(mask))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            mc.Clear(SKColors.Transparent);
            mc.SetMatrix(canvas.TotalMatrix);
            mc.DrawPath(shape, paint);
        }
        var stride = mask.RowBytes;
        var bytes = mask.GetPixelSpan().ToArray();
        var alpha = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) alpha[y * w + x] = bytes[y * stride + x] / 255f;
        var size = MotionBlurLayer.Pixels(new MotionConstant(MotionType.Vector, [feather, feather]), matrix, 0);
        var soft = MotionBlurLayer.BlurredAlpha(alpha, w, h, MotionBlurLayer.BlurType.FastGaussian, size.X, size.Y,
            MotionBlurLayer.MarginFor(MotionBlurLayer.BlurType.FastGaussian, size.X, size.Y));
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < soft.Length; i++) rgba[i * 4 + 3] = (byte)Math.Round(Math.Clamp(soft[i], 0, 1) * 255);
        using var out_ = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        rgba.CopyTo(out_.GetPixelSpan());
        out_.NotifyPixelsChanged();
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawBitmap(out_, 0, 0);
        canvas.Restore();
    }

    private static SKPath Poly(SKPoint[] points)
    {
        var path = new SKPath();
        path.AddPoly(points);
        return path;
    }

    // ------------------------------------------------------------------ .sif: a Synfig layer that erases

    public override XElement ToSif(SifWriter sif)
    {
        var fps = sif.Fps;
        var count = (int)Math.Floor(sif.Duration * fps + 1e-9) + 1;
        var times = Enumerable.Range(0, count).Select(f => f / fps).ToArray();
        var frames = times.Select(t => Frame(stack.SpaceAt(t).Space)).ToArray();
        MotionNode Sampled(MotionType kind, Func<int, double[]> at) => MotionTextLayer.Keys(kind, times, [.. Enumerable.Range(0, count).Select(at)]);
        MotionNode Point(Func<int, SKPoint> at) => Sampled(MotionType.Vector, i => { var p = at(i); return [p.X, p.Y]; });
        var black = SifWriter.Param("color", SifWriter.Value(MotionType.Color, [0, 0, 0, 1]));
        bool Hard() => times.All(t => Feather(t) <= 0);

        switch (type)
        {
            case "linear" when Hard():
            {
                var corners = Enumerable.Range(0, over is null ? 4 : 5).Select(k => Point(i => Erased(Linear(times[i], frames[i]))[k])).ToArray();
                return Region(sif, corners, false, null);
            }
            case "linear":
            {
                object[] gradient =
                [
                    sif.Param("p1", Point(i => { var (d, edge, f, _, _, _) = Linear(times[i], frames[i]); return P(d, edge - f / 2, 0); })),
                    sif.Param("p2", Point(i => { var (d, edge, f, _, _, _) = Linear(times[i], frames[i]); return P(d, edge + f / 2, 0); })),
                    GradientParam(times, _ => [(0f, 1f), (1f, 0f)]),
                    SifWriter.Param("loop", SifWriter.Bool(false)), SifWriter.Param("zigzag", SifWriter.Bool(false)),
                ];
                if (over is not { } box) return Element("linear_gradient", "0.1", sif, gradient);
                // Confined to the box: a group that erases, holding the gradient (composite, 0) and an inverted box erasing it outside.
                var outside = RegionParams(sif, [.. Corners(box).Select(p => (MotionNode)new MotionConstant(MotionType.Vector, [p.X, p.Y]))], true, null);
                return ErasingGroup(sif, Inner("linear_gradient", 0, gradient), Inner("region", SifBlendMethod, outside));
            }
            case "matte":
            {
                // Synfig has no threshold wipe, so each distinct mask is a picture beside the .sif, on an import layer held
                // for the frames it covers, all in a group that erases.
                var names = times.Select(t => Done(t) <= 0 ? null : Baked(sif, Done(t), Feather(t))).ToArray();
                XElement Corner(Func<(SKPoint Tl, SKPoint Br), SKPoint> pick, string name)
                {
                    var at = Enumerable.Range(0, count).Select(i => pick(Placed(times[i], frames[i]))).ToArray();
                    return at.All(p => p == at[0]) ? SifWriter.Param(name, SifWriter.Value(MotionType.Vector, [at[0].X, at[0].Y]))
                        : sif.Param(name, Point(i => at[i]));
                }
                var (cornerTl, cornerBr) = (Corner(p => p.Tl, "tl"), Corner(p => p.Br, "br"));
                var held = new List<XElement>();
                for (var i = 0; i < count;)
                {
                    var j = i;
                    while (j + 1 < count && names[j + 1] == names[i]) j++;
                    if (names[i] is { } file)
                        held.Add(Inner("import", 0, [new XElement(cornerTl), new XElement(cornerBr),
                            SifWriter.Param("c", new XElement("integer", new XAttribute("value", "1"))),
                            SifWriter.Param("gamma_adjust", new XElement("real", new XAttribute("value", "1"))),
                            SifWriter.Param("filename", new XElement("string", file)),
                            SifWriter.Param("time_offset", new XElement("time", new XAttribute("value", "0s")))],
                            Held(times[i], j + 1 < count ? times[j + 1] : null)));
                    i = j + 1;
                }
                return ErasingGroup(sif, [.. held]);
            }
            case "blinds":
                return Element("linear_gradient", "0.1", sif,
                    SifWriter.Param("p1", SifWriter.Value(MotionType.Vector, [0, 0])),
                    sif.Param("p2", Point(i => P(Dir(angle.Evaluate(times[i])[0] + 90), Math.Max(1e-6, width.Evaluate(times[i])[0]), 0))),
                    GradientParam(times, t => BlindStops(Done(t), Math.Max(1e-6, width.Evaluate(t)[0]), Feather(t))),
                    SifWriter.Param("loop", SifWriter.Bool(true)), SifWriter.Param("zigzag", SifWriter.Bool(false)));
            case "radial" when Hard():
            {
                var wedge = Enumerable.Range(0, 6).Select(k => Point(i => Done(times[i]) <= 0 ? Centre(times[i], frames[i]) : Wedge(times[i], Centre(times[i], frames[i]))[k])).ToArray();
                return Region(sif, wedge, false, null);
            }
            case "radial":
                // Synfig's conical gradient runs counterclockwise from +x as a fraction of a turn; the stops are laid out
                // reversed and the gradient turned so its 0 lies on the start line and it runs clockwise.
                return Element("conical_gradient", "0.1", sif,
                    GradientParam(times, t => [.. SweepStops(Done(t), Feather(t)).Select(s => (1 - s.Pos, s.Erase)).Reverse()]),
                    sif.Param("center", Point(i => Centre(times[i], frames[i]))),
                    sif.Param("angle", Sampled(MotionType.Angle, i => [startAngle.Evaluate(times[i])[0] - 90])),
                    SifWriter.Param("symmetric", SifWriter.Bool(false)));
            default:
            {
                var feathered = sif.Param("feather", Sampled(MotionType.Real, i => [Feather(times[i])]));
                if (points < 3)
                    return Element("circle", "0.2", sif, black,
                        sif.Param("radius", Sampled(MotionType.Real, i => [Math.Max(0, Iris(times[i], frames[i]).R)])),
                        feathered,
                        sif.Param("origin", Point(i => Iris(times[i], frames[i]).C)),
                        SifWriter.Param("invert", SifWriter.Bool(invert)),
                        SifWriter.Param("blurtype", new XElement("integer", new XAttribute("value", "1"))));
                var n = inner < 1 ? points * 2 : points;
                var vertices = Enumerable.Range(0, n).Select(k => Point(i => Iris(times[i], frames[i]).Vertices[k])).ToArray();
                return Region(sif, vertices, invert, feathered);
            }
        }
    }

    /// <summary>A group that erases what lies under it with what it holds, the wipe's amount on the group.</summary>
    private XElement ErasingGroup(SifWriter sif, params XElement[] inner) =>
        Element("group", "0.3", sif,
            SifWriter.Param("origin", SifWriter.Value(MotionType.Vector, [0, 0])),
            SifWriter.Param("transformation", new XElement("composite", new XAttribute("type", "transformation"),
                new XElement("offset", SifWriter.Value(MotionType.Vector, [0, 0])),
                new XElement("angle", SifWriter.Value(MotionType.Angle, [0])),
                new XElement("skew_angle", SifWriter.Value(MotionType.Angle, [0])),
                new XElement("scale", SifWriter.Value(MotionType.Vector, [1, 1])))),
            SifWriter.Param("canvas", new XElement("canvas", inner)),
            SifWriter.Param("time_dilation", new XElement("real", new XAttribute("value", "1"))),
            SifWriter.Param("time_offset", new XElement("time", new XAttribute("value", MotionTypes.Time(0)))),
            SifWriter.Param("children_lock", SifWriter.Bool(false)),
            SifWriter.Param("outline_grow", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("z_range", SifWriter.Bool(false)),
            SifWriter.Param("z_range_position", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("z_range_depth", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("z_range_blur", new XElement("real", new XAttribute("value", "0"))));

    /// <summary>A matte's mask at one completion, written beside the .sif; the same mask twice is one file.</summary>
    private string Baked(SifWriter sif, double done, double f)
    {
        using var mask = Mask(done, f);
        return sif.Picture(MotionPicture.From(mask, "a matte wipe"));
    }

    /// <summary>An amount of 1 from <paramref name="from"/> until <paramref name="until"/> (or the end), 0 otherwise, held.</summary>
    private static XElement Held(double from, double? until)
    {
        if (from <= 0 && until is null) return SifWriter.Param("amount", new XElement("real", new XAttribute("value", "1")));
        static XElement At(double t, double v) => new("waypoint", new XAttribute("time", MotionTypes.Time(t)),
            new XAttribute("before", "constant"), new XAttribute("after", "constant"), new XElement("real", new XAttribute("value", MotionTypes.Format(v))));
        return SifWriter.Param("amount", new XElement("animated", new XAttribute("type", "real"),
            from > 0 ? At(0, 0) : null, At(from, 1), until is { } u ? At(u, 0) : null));
    }

    /// <summary>A closed region of corner points, black, erasing.</summary>
    private XElement Region(SifWriter sif, MotionNode[] corners, bool inverted, XElement? featherParam) =>
        Element("region", "0.1", sif, RegionParams(sif, corners, inverted, featherParam));

    /// <summary>A layer inside the wipe's own group: at full amount unless given one, blended by <paramref name="blend"/>.</summary>
    private static XElement Inner(string type, int blend, object[] content, XElement? amount = null) =>
        new("layer", new XAttribute("type", type), new XAttribute("active", "true"), new XAttribute("version", "0.1"),
            SifWriter.Param("z_depth", new XElement("real", new XAttribute("value", "0"))),
            amount ?? SifWriter.Param("amount", new XElement("real", new XAttribute("value", "1"))),
            SifWriter.Param("blend_method", new XElement("integer", new XAttribute("value", blend))),
            content);

    private static object[] RegionParams(SifWriter sif, MotionNode[] corners, bool inverted, XElement? featherParam) =>
        [
            SifWriter.Param("color", SifWriter.Value(MotionType.Color, [0, 0, 0, 1])),
            SifWriter.Param("origin", SifWriter.Value(MotionType.Vector, [0, 0])),
            SifWriter.Param("invert", SifWriter.Bool(inverted)),
            SifWriter.Param("antialias", SifWriter.Bool(true)),
            featherParam ?? SifWriter.Param("feather", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("blurtype", new XElement("integer", new XAttribute("value", "1"))),
            SifWriter.Param("winding_style", new XElement("integer", new XAttribute("value", "0"))),
            SifWriter.Param("bline", new XElement("bline", new XAttribute("type", "bline_point"), new XAttribute("loop", "true"),
                corners.Select(c => new XElement("entry", new XElement("composite", new XAttribute("type", "bline_point"),
                    sif.Link("point", c),
                    new XElement("width", SifWriter.Value(MotionType.Real, [1])),
                    new XElement("origin", SifWriter.Value(MotionType.Real, [0.5])),
                    new XElement("split", SifWriter.Bool(true)),
                    new XElement("t1", SifWriter.Value(MotionType.Vector, [0, 0])),
                    new XElement("t2", SifWriter.Value(MotionType.Vector, [0, 0])),
                    new XElement("split_radius", SifWriter.Bool(true)),
                    new XElement("split_angle", SifWriter.Bool(true)))))))
        ];

    /// <summary>A black gradient of erase stops: one value when it never changes, else a held key at every frame.</summary>
    private static XElement GradientParam(double[] times, Func<double, (float Pos, float Erase)[]> at)
    {
        static XElement Gradient((float Pos, float Erase)[] stops) => new("gradient", stops.Select(s =>
        {
            var c = SifWriter.Value(MotionType.Color, [0, 0, 0, s.Erase]);
            c.Add(new XAttribute("pos", MotionTypes.Format(s.Pos)));
            return c;
        }));
        var all = times.Select(at).ToArray();
        if (all.All(s => s.SequenceEqual(all[0]))) return SifWriter.Param("gradient", Gradient(all[0]));
        return SifWriter.Param("gradient", new XElement("animated", new XAttribute("type", "gradient"),
            times.Select((t, i) => new XElement("waypoint", new XAttribute("time", MotionTypes.Time(t)),
                new XAttribute("before", "constant"), new XAttribute("after", "constant"), Gradient(all[i])))));
    }

    // ------------------------------------------------------------------ SVG: the eraser shape, which the stack masks with

    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
    {
        if (type == "matte")
            throw new InvalidOperationException($"A matte wipe{(Desc is null ? "" : $" '{Desc}'")} cannot be written as animated SVG: its mask is a picture "
                + "that changes every frame. Write a .sif, or capture the composition as frames.");
        if (times(svg).Any(t => Feather(map.Local(t)) > 0))
            throw new InvalidOperationException($"The {type} wipe{(Desc is null ? "" : $" '{Desc}'")} is feathered, which this SVG export does not draw yet. "
                + "Give it no feather, write a .sif, or capture the composition as frames.");
        var path = SvgElement("path", new XAttribute("fill", "#000000"));
        if (type == "iris" && invert) path.SetAttributeValue("fill-rule", "evenodd");
        svg.Attribute(path, "d", t =>
        {
            var local = map.Local(t);
            var frame = Frame(stack.SpaceAt(t).Space);
            var pts = type switch
            {
                "linear" => Erased(Linear(local, frame)),
                "radial" => Done(local) <= 0 ? [] : Wedge(local, Centre(local, frame)),
                "iris" => Iris(local, frame) is var i && i.Vertices.Length > 0 ? i.Vertices : Circle(i.C, i.R),
                _ => throw new InvalidOperationException($"A blinds wipe{(Desc is null ? "" : $" '{Desc}'")} cannot be written as animated SVG: its bands change in number. Write a .sif, or capture the composition as frames."),
            };
            var d = pts.Length == 0 ? "M0 0Z" : "M" + string.Join("L", pts.Select(p => $"{SvgAnimationWriter.N(p.X)} {SvgAnimationWriter.N(p.Y)}")) + "Z";
            return type == "iris" && invert ? SvgAnimationWriter.Everything + d : d;
        });
        Opacity(path, svg, map);
        return path;

        static double[] times(SvgAnimationWriter w) => w.Times;

        // A circle as a 64-sided polygon, so its d keeps the same commands at every frame.
        static SKPoint[] Circle(SKPoint c, double r) =>
            [.. Enumerable.Range(0, 64).Select(i => new SKPoint((float)(c.X + r * Math.Cos(i * Math.PI / 32)), (float)(c.Y + r * Math.Sin(i * Math.PI / 32))))];
    }
    #endregion
}
