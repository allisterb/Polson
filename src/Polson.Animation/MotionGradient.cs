namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// Synfig's linear and radial gradient layers: a gradient over the whole plane, colour by distance along a line or from
/// a centre. To fill a shape with one, put both in a group and give the gradient <c>blend: 'onto'</c>.
/// </summary>
/// <remarks>
/// Ported from <c>mod_gradient/lineargradient.cpp</c>, <c>radialgradient.cpp</c> and <c>synfig/gradient.cpp</c>. Synfig's
/// <c>CompiledGradient</c> clamps stop positions to [0, 1], interpolates <b>premultiplied</b> colour, repeats with
/// <c>x − floor(x)</c> when looped, and for zigzag halves the stops and mirrors them into the second half — so a zigzag
/// gradient that does not loop runs out and back once between its two points. Skia's gradients interpolate straight
/// colour, so a span whose alpha changes is laid down as short premultiplied steps; the rest is Skia's own shader.
/// <para>
/// Synfig averages each pixel over a short span of the gradient. For the radial layer the span is ±0.6 pixel; for the
/// linear one it is ±0.1 of the gradient's own length in a file whose units are pixels — the window is never divided by
/// the length — so Synfig softens a hard stop over a fifth of a linear gradient. This draws the gradient exactly, as SVG
/// and a browser do; the <c>.sif</c> keeps the stops, and Synfig's render of it differs near hard stops.
/// </para>
/// </remarks>
internal sealed class MotionGradientLayer : MotionLayer
{
    #region Constructors
    public MotionGradientLayer(MotionOptions o, bool radial) : base(o)
    {
        this.radial = radial;
        if (radial)
        {
            a = o.Node("origin", MotionType.Vector, new[] { 0d, 0d });
            b = o.Node("radius", MotionType.Real, 100d);
        }
        else
        {
            a = o.Node("point1", MotionType.Vector, new[] { 0d, 0d });
            b = o.Node("point2", MotionType.Vector, new[] { 100d, 0d });
        }
        stops = ReadStops(o.Raw("stops"), o.Who);
        loop = o.Bool("loop", false);
        zigzag = o.Bool("zigzag", false);
        compiled = Compile(stops, zigzag);
    }
    #endregion

    #region Fields
    private readonly bool radial, loop, zigzag;
    private readonly MotionNode a, b;
    private readonly (double Pos, double[] Color)[] stops;

    /// <summary>The stops as Skia draws them: zigzag mirrored, and spans whose alpha changes laid down premultiplied.</summary>
    private readonly (float[] Pos, SKColor[] Colors) compiled;
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() => [Amount, a, b];

    protected override void Draw(SkiaCanvas surface, double time, double amount)
    {
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(amount * 255)), IsAntialias = true };
        var mode = loop ? SKShaderTileMode.Repeat : SKShaderTileMode.Clamp;
        var p = a.Evaluate(time);
        var start = new SKPoint((float)p[0], (float)p[1]);
        SKShader? shader = null;
        if (radial)
        {
            var r = Math.Abs(b.Evaluate(time)[0]);
            if (r > 1e-9) shader = SKShader.CreateRadialGradient(start, (float)r, compiled.Colors, compiled.Pos, mode);
            else paint.Color = compiled.Colors[^1].WithAlpha((byte)Math.Round(compiled.Colors[^1].Alpha * amount));
        }
        else
        {
            var q = b.Evaluate(time);
            var end = new SKPoint((float)q[0], (float)q[1]);
            // Synfig's distance along a line of no length is 0 everywhere: the first colour.
            if (SKPoint.Distance(start, end) > 1e-9) shader = SKShader.CreateLinearGradient(start, end, compiled.Colors, compiled.Pos, mode);
            else paint.Color = compiled.Colors[0].WithAlpha((byte)Math.Round(compiled.Colors[0].Alpha * amount));
        }

        using (shader)
        {
            paint.Shader = shader;
            surface.SkCanvas.DrawPaint(paint);
        }
    }

    public override XElement ToSif(SifWriter sif)
    {
        var gradient = SifGradient(stops);
        var repeat = new[] { SifWriter.Param("loop", SifWriter.Bool(loop)), SifWriter.Param("zigzag", SifWriter.Bool(zigzag)) };
        return radial
            ? Element("radial_gradient", "0.2", sif, gradient, sif.Param("center", a), sif.Param("radius", b), repeat)
            : Element("linear_gradient", "0.1", sif, sif.Param("p1", a), sif.Param("p2", b), gradient, repeat);
    }

    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
    {
        var gradient = new XElement(SvgAnimationWriter.Svg + (radial ? "radialGradient" : "linearGradient"),
            new XAttribute("gradientUnits", "userSpaceOnUse"),
            new XAttribute("spreadMethod", loop ? "repeat" : "pad"),
            compiled.Pos.Select((pos, i) =>
            {
                var c = compiled.Colors[i];
                return new XElement(SvgAnimationWriter.Svg + "stop", new XAttribute("offset", SvgAnimationWriter.N(pos, 6)),
                    new XAttribute("stop-color", $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}"),
                    c.Alpha == 255 ? null : new XAttribute("stop-opacity", SvgAnimationWriter.N(c.Alpha / 255d)));
            }));
        if (radial)
        {
            svg.Attribute(gradient, "cx", svg.Track(a, 0, map));
            svg.Attribute(gradient, "cy", svg.Track(a, 1, map));
            svg.Attribute(gradient, "r", svg.Track(b, 0, map, Math.Abs));
        }
        else
        {
            svg.Attribute(gradient, "x1", svg.Track(a, 0, map));
            svg.Attribute(gradient, "y1", svg.Track(a, 1, map));
            svg.Attribute(gradient, "x2", svg.Track(b, 0, map));
            svg.Attribute(gradient, "y2", svg.Track(b, 1, map));
        }
        var id = svg.Define("gradient", gradient);

        // At the root it covers the view; inside a group it must survive the group's transform.
        var v = svg.View;
        var (x, y, w, h) = svg.Depth == 0
            ? (Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Abs(v[2] - v[0]), Math.Abs(v[3] - v[1]))
            : (-100000d, -100000d, 200000d, 200000d);
        var rect = SvgElement("rect", new XAttribute("x", SvgAnimationWriter.N(x)), new XAttribute("y", SvgAnimationWriter.N(y)),
            new XAttribute("width", SvgAnimationWriter.N(w)), new XAttribute("height", SvgAnimationWriter.N(h)),
            new XAttribute("fill", $"url(#{id})"));
        Opacity(rect, svg, map);
        return rect;
    }

    /// <summary>The stops as Synfig's <c>gradient</c> parameter.</summary>
    internal static XElement SifGradient((double Pos, double[] Color)[] stops) =>
        SifWriter.Param("gradient", new XElement("gradient", stops.Select(s =>
        {
            var color = SifWriter.Value(MotionType.Color, s.Color);
            color.Add(new XAttribute("pos", MotionTypes.Format(s.Pos)));
            return color;
        })));

    /// <summary>
    /// <c>stops</c>: colours spread evenly, <c>['#000', '#fff']</c>; or <c>[[pos, color], ...]</c>; or
    /// <c>[{ pos, color }, ...]</c>; black to white when left out, as Synfig's default.
    /// </summary>
    internal static (double Pos, double[] Color)[] ReadStops(object? value, string who)
    {
        if (value is null) return [(0, [0, 0, 0, 1]), (1, [1, 1, 1, 1])];
        if (value is not IList list || list.Count == 0)
            throw new ArgumentException($"{who}'s stops are colours, ['#000000', '#ffffff'], or [[pos, color], ...] with pos from 0 to 1.");

        var read = list.Cast<object?>().Select((item, i) =>
        {
            var at = $"{who}'s stop {i}";
            if (item is string)
                return (Pos: list.Count == 1 ? 0 : i / (double)(list.Count - 1), Color: MotionTypes.Read(item, MotionType.Color, at));
            var (pos, color) = item switch
            {
                IList { Count: 2 } pair => (pair[0], pair[1]),
                _ when JsInterop.AsDict(item) is { } d && d.Count == 2 && d.Contains("pos") && d.Contains("color") => (d["pos"], d["color"]),
                _ => throw new ArgumentException($"{at} is a colour, [pos, color] or {{ pos, color }}.")
            };
            var p = MotionTypes.Read(pos, MotionType.Real, $"{at}'s pos")[0];
            if (p is < 0 or > 1) throw new ArgumentException($"{at}'s pos is {MotionTypes.Format(p)}: a stop sits from 0 to 1 along the gradient.");
            return (Pos: p, Color: MotionTypes.Read(color, MotionType.Color, $"{at}'s color"));
        }).ToArray();

        // Stable, as Synfig sorts them, so two stops at one place make a hard edge in the order given.
        return [.. read.Select((s, i) => (s, i)).OrderBy(x => x.s.Pos).ThenBy(x => x.i).Select(x => x.s)];
    }

    private static (float[] Pos, SKColor[] Colors) Compile((double Pos, double[] Color)[] stops, bool zigzag)
    {
        var list = stops.ToList();
        if (list.Count == 1) list.Add((1, list[0].Color));
        if (zigzag)
            list = [.. list.Select(s => (s.Pos * 0.5, s.Color)), .. Enumerable.Reverse(list).Select(s => (1 - s.Pos * 0.5, s.Color))];

        // Premultiplied between stops whose alpha differs, in steps fine enough that straight interpolation between them
        // is within a level of the curve.
        const int steps = 16;
        var pos = new List<float> { (float)list[0].Pos };
        var colors = new List<SKColor> { MotionTypes.ToSkColor(list[0].Color) };
        for (var i = 1; i < list.Count; i++)
        {
            var (p0, c0) = list[i - 1];
            var (p1, c1) = list[i];
            if (Math.Abs(c0[3] - c1[3]) > 1e-9 && p1 > p0)
            {
                for (var k = 1; k < steps; k++)
                {
                    var f = k / (double)steps;
                    var alpha = c0[3] + (c1[3] - c0[3]) * f;
                    double Channel(int c) => alpha <= 1e-9 ? 0 : (c0[c] * c0[3] * (1 - f) + c1[c] * c1[3] * f) / alpha;
                    pos.Add((float)(p0 + (p1 - p0) * f));
                    colors.Add(MotionTypes.ToSkColor([Channel(0), Channel(1), Channel(2), alpha]));
                }
            }
            pos.Add((float)p1);
            colors.Add(MotionTypes.ToSkColor(c1));
        }
        return ([.. pos], [.. colors]);
    }
    #endregion
}
