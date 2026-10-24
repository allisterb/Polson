namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SkiaSharp;

/// <summary>
/// Axes in a composition, as <c>comp.axes(...)</c> returns them: data coordinates mapped onto the composition, and
/// function plots, areas, Riemann rectangles and tangents drawn in them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Made of what the composition already has.</b> Axis lines, ticks, grid and curves are outlines from path data, areas
/// and rectangles are regions, and tick labels are equations: so everything keys, fades, exports to <c>.sif</c>, and a
/// curve writes on by keying its <c>trimEnd</c>, as Manim's <c>Create</c> does. The labels are listed by
/// <c>comp.texts()</c> and read back with the rest of the film.
/// </para>
/// <para>
/// <b>A function is sampled once, when it is plotted,</b> never per frame: per-frame callbacks would break <c>.sif</c> export
/// and the script's time limit. Values outside the y range, or not finite, break the curve, which is clipped at the range.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase form is the one documented in
/// <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public sealed class MotionAxes
{
    #region Constructors
    internal MotionAxes(MotionGroup group, double[] x, double[] y, double cx, double cy, double width, double height, bool yDown, double stroke)
    {
        (this.group, this.x, this.y, this.cx, this.cy, this.width, this.height, this.yDown, this.stroke) =
            (group, x, y, cx, cy, width, height, yDown, stroke);
    }
    #endregion

    #region Properties
    /// <summary>The x range and tick step, <c>[min, max, step]</c>.</summary>
    public double[] X => [.. x];

    /// <summary>The y range and tick step, <c>[min, max, step]</c>.</summary>
    public double[] Y => [.. y];
    #endregion

    #region Methods
    /// <summary>Where the data point (<paramref name="px"/>, <paramref name="py"/>) sits in the composition, <c>[x, y]</c>.</summary>
    public double[] Point(double px, double py) => [MapX(px), MapY(py)];

    /// <summary>
    /// The graph of <paramref name="f"/>, sampled once now: <c>{ range?, samples?, color?, width?, trimEnd?, amount?, desc? }</c>.
    /// <c>range</c> defaults to the x range; <c>samples</c> to 200 (at most 2,000). Values outside the y range, or not
    /// finite, break the curve. Key <c>trimEnd</c> from 0 to 1 to draw it on.
    /// </summary>
    public MotionAxes Plot(Func<double, double> f, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(f);
        var o = new MotionOptions(options, "axes.plot", "range", "samples", "color", "width", "trimEnd", "amount", "desc");
        var (a, b) = Range(o, "axes.plot");
        var n = Samples(o, 200);
        var d = new StringBuilder();
        double? px = null, py = null;
        var open = false;
        for (var i = 0; i <= n; i++)
        {
            var xv = a + (b - a) * i / n;
            var yv = f(xv);
            var inside = double.IsFinite(yv) && yv >= y[0] && yv <= y[1];
            if (inside)
            {
                if (!open && px is { } x0 && py is { } y0 && double.IsFinite(y0))
                {
                    // Re-entering the range: start on its edge, where the segment from the last sample crosses it.
                    var edge = y0 > y[1] ? y[1] : y[0];
                    d.Append(Move(x0 + (xv - x0) * (edge - y0) / (yv - y0), edge));
                }
                else if (!open) d.Append(Move(xv, yv));
                d.Append(Line(xv, yv));
                open = true;
            }
            else if (open && double.IsFinite(yv) && px is { } x1 && py is { } y1)
            {
                var edge = yv > y[1] ? y[1] : y[0];
                d.Append(Line(x1 + (xv - x1) * (edge - y1) / (yv - y1), edge));
                open = false;
            }
            else open = false;
            (px, py) = (xv, yv);
        }
        if (d.Length == 0) throw new ArgumentException("axes.plot drew nothing: the function has no finite value inside the axes' y range over the x range given.");

        var line = Stroke(o, "#1d3557", stroke * 2.5);
        line["path"] = d.ToString();
        if (o.Has("trimEnd")) line["trimEnd"] = o.Raw("trimEnd");
        group.Outline(line);
        return this;
    }

    /// <summary>
    /// The area between <paramref name="f"/> and the x axis from <paramref name="a"/> to <paramref name="b"/>, filled:
    /// <c>{ samples?, color?, amount?, desc? }</c>, the colour translucent by default so the curve shows through.
    /// </summary>
    public MotionAxes Area(Func<double, double> f, double a, double b, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(f);
        var o = new MotionOptions(options, "axes.area", "samples", "color", "amount", "desc");
        if (!(b > a)) throw new ArgumentException($"axes.area needs a below b; it was given {a} and {b}.");
        var n = Samples(o, 120);
        var baseline = Clamp(0);
        var d = new StringBuilder(Move(a, baseline));
        for (var i = 0; i <= n; i++)
        {
            var xv = a + (b - a) * i / n;
            d.Append(Line(xv, Clamp(f(xv))));
        }
        d.Append(Line(b, baseline)).Append('Z');
        group.Region(Fill(o, d.ToString(), "#457b9d66"));
        return this;
    }

    /// <summary>
    /// <paramref name="n"/> Riemann rectangles of <paramref name="f"/> from <paramref name="a"/> to <paramref name="b"/>:
    /// <c>{ rule?, color?, amount?, desc? }</c>, <c>rule</c> <c>'left'</c> (default), <c>'right'</c> or <c>'mid'</c>. Returns
    /// their total area, the approximation itself, for a label.
    /// </summary>
    public double Riemann(Func<double, double> f, double a, double b, int n, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(f);
        var o = new MotionOptions(options, "axes.riemann", "rule", "color", "amount", "desc");
        if (!(b > a)) throw new ArgumentException($"axes.riemann needs a below b; it was given {a} and {b}.");
        if (n is < 1 or > 500) throw new ArgumentException($"axes.riemann takes 1 to 500 rectangles; it was given {n}.");
        var rule = o.Raw("rule") as string ?? "left";
        var at = rule switch
        {
            "left" => 0d,
            "right" => 1d,
            "mid" => 0.5,
            _ => throw new ArgumentException($"axes.riemann's rule is 'left', 'right' or 'mid'; '{rule}' is not one."),
        };
        var step = (b - a) / n;
        var total = 0d;
        var d = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var x0 = a + i * step;
            var h = f(x0 + at * step);
            if (!double.IsFinite(h)) throw new ArgumentException($"axes.riemann: f({x0 + at * step}) is not a finite number.");
            total += h * step;
            var (lo, hi) = (Clamp(Math.Min(0, h)), Clamp(Math.Max(0, h)));
            d.Append(Move(x0, lo)).Append(Line(x0 + step, lo)).Append(Line(x0 + step, hi)).Append(Line(x0, hi)).Append('Z');
        }
        group.Region(Fill(o, d.ToString(), "#a8dadc"));
        return total;
    }

    /// <summary>
    /// The tangent to <paramref name="f"/> at <paramref name="x0"/>, from a central difference: <c>{ length?, color?, width?,
    /// amount?, desc? }</c>, <c>length</c> in x units (default a quarter of the x range). Returns <c>{ x, y, slope }</c>.
    /// </summary>
    public Dictionary<string, object?> Tangent(Func<double, double> f, double x0, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(f);
        var o = new MotionOptions(options, "axes.tangent", "length", "color", "width", "amount", "desc");
        var h = (x[1] - x[0]) * 1e-4;
        var (y0, slope) = (f(x0), (f(x0 + h) - f(x0 - h)) / (2 * h));
        if (!double.IsFinite(y0) || !double.IsFinite(slope)) throw new ArgumentException($"axes.tangent: f is not finite and smooth at {x0}.");
        var half = o.Number("length", (x[1] - x[0]) / 4) / 2;
        var line = Stroke(o, "#e63946", stroke * 2);
        line["path"] = Move(x0 - half, y0 - slope * half) + Line(x0 + half, y0 + slope * half);
        group.Outline(line);
        return new() { ["x"] = x0, ["y"] = y0, ["slope"] = slope };
    }
    #endregion

    #region Private methods
    private double MapX(double v) => cx - width / 2 + (v - x[0]) / (x[1] - x[0]) * width;

    private double MapY(double v)
    {
        var t = (v - y[0]) / (y[1] - y[0]) * height;
        return yDown ? cy + height / 2 - t : cy - height / 2 + t;
    }

    private double Clamp(double v) => Math.Clamp(double.IsFinite(v) ? v : 0, y[0], y[1]);

    private string Move(double px, double py) => FormattableString.Invariant($"M{MapX(px):0.###} {MapY(py):0.###} ");

    private string Line(double px, double py) => FormattableString.Invariant($"L{MapX(px):0.###} {MapY(py):0.###} ");

    private (double A, double B) Range(MotionOptions o, string who)
    {
        if (!o.Has("range")) return (x[0], x[1]);
        if (o.Raw("range") is not IList { Count: 2 } r) throw new ArgumentException($"{who}'s range is [from, to] in x units.");
        var (a, b) = (Convert.ToDouble(r[0], CultureInfo.InvariantCulture), Convert.ToDouble(r[1], CultureInfo.InvariantCulture));
        if (!(b > a)) throw new ArgumentException($"{who}'s range runs from low to high; it was [{a}, {b}].");
        return (a, b);
    }

    private static int Samples(MotionOptions o, int fallback)
    {
        var n = (int)o.Number("samples", fallback);
        return n is >= 2 and <= 2000 ? n : throw new ArgumentException($"samples is 2 to 2,000; it was {n}.");
    }

    private Hashtable Stroke(MotionOptions o, string fallbackColor, double fallbackWidth)
    {
        var line = o.Subset("amount", "desc");
        line["color"] = o.Has("color") ? o.Raw("color") : fallbackColor;
        line["width"] = o.Has("width") ? o.Raw("width") : fallbackWidth;
        return line;
    }

    private static Hashtable Fill(MotionOptions o, string path, string fallbackColor)
    {
        var region = o.Subset("amount", "desc");
        region["color"] = o.Has("color") ? o.Raw("color") : fallbackColor;
        region["path"] = path;
        return region;
    }

    /// <summary>A tick value as a label: whole numbers plain, others to at most three places, a minus sign as LaTeX writes it.</summary>
    internal static string Label(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    #endregion

    #region Fields
    private readonly MotionGroup group;
    private readonly double[] x, y;
    private readonly double cx, cy, width, height, stroke;
    private readonly bool yDown;
    #endregion
}

public abstract partial class MotionLayerList
{
    /// <summary>The smallest tick label, as a share of the frame's height: 16 px at 720 lines.</summary>
    private const double MinLabelShare = 16d / 720;

    /// <summary>
    /// Refuses a box that is never wholly in the frame, through the transforms of every group above, at any time of the film.
    /// </summary>
    /// <remarks>
    /// ftcexplainer1 (2026-10-23) read <c>origin</c> as the box's lower-left corner, as a chart's origin usually is: every
    /// graph ran 160 px off the left edge, and the scene titled "Exact Area from 0 to 2" never showed 0. Axes that slide in
    /// pass, being in the frame at some time. Not checked under a camera or a warp, whose mapping is not a plane's.
    /// </remarks>
    private void OffFrame(double[] origin, double[] size)
    {
        if (Root is not { } root || root.HasCamera) return;
        var frame = root.ViewRect;
        var (w, h) = (size[0] / 2, size[1] / 2);
        var slack = 0.005 * Math.Max(frame.Width, frame.Height);
        var steps = (int)Math.Clamp(Math.Ceiling(root.Duration * root.Fps), 1, 200);
        SKRect? first = null;
        for (var i = 0; i <= steps; i++)
        {
            var (space, _, _, _) = SpaceAt(root.Duration * i / steps);
            if (space.Warped) return;
            var corners = new[] { (-w, -h), (w, -h), (w, h), (-w, h) }
                .Select(c => space.Plane.MapPoint((float)(origin[0] + c.Item1), (float)(origin[1] + c.Item2))).ToArray();
            var box = new SKRect(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
            first ??= box;
            if (box.Left >= frame.Left - slack && box.Top >= frame.Top - slack && box.Right <= frame.Right + slack && box.Bottom <= frame.Bottom + slack)
                return;
        }

        var b = first!.Value;
        var sides = new[] { (b.Left < frame.Left - slack, "left"), (b.Right > frame.Right + slack, "right"), (b.Top < frame.Top - slack, "top"), (b.Bottom > frame.Bottom + slack, "bottom") }
            .Where(s => s.Item1).Select(s => s.Item2);
        // What a corner reading would have meant: the corner on the x axis's side (bottom, with y down), and the box from there.
        var down = root.YDown ? 1 : -1;
        var meant = new[] { origin[0] + w, origin[1] - down * h };
        var meantFits = meant[0] - w >= frame.Left && meant[0] + w <= frame.Right && meant[1] - h >= Math.Min(frame.Top, frame.Bottom) && meant[1] + h <= Math.Max(frame.Top, frame.Bottom);
        throw new ArgumentException($"axes' box runs off the {string.Join(" and ", sides)} of the frame at every time: it spans "
            + $"({F(b.Left)}, {F(b.Top)}) to ({F(b.Right)}, {F(b.Bottom)}), and the frame is ({F(frame.Left)}, {F(frame.Top)}) to "
            + $"({F(frame.Right)}, {F(frame.Bottom)}). origin is the centre of the axes' box, not its corner or where the data's 0 falls."
            + (meantFits ? $" If [{F(origin[0])}, {F(origin[1])}] was meant as the lower-left corner, origin is [{F(meant[0])}, {F(meant[1])}]." : ""));

        static string F(double v) => MotionAxes.Label(Math.Round(v, 1));
    }

    /// <summary>
    /// Axes in data coordinates: <c>{ x: [min, max, step], y: [min, max, step], origin, size: [width, height], color?, width?,
    /// labels?, labelSize?, grid?, amount?, desc? }</c>. <c>origin</c> is the centre of the axes' box, which must be wholly
    /// in the frame at some time. Each axis crosses the other at 0 when 0 is in range, else at its minimum; ticks fall on
    /// multiples of <c>step</c>, labelled (as equations, read back with the film) unless <c>labels</c> is false. Returns the
    /// axes, to map points and to plot in.
    /// </summary>
    public MotionAxes Axes(object? options)
    {
        var o = new MotionOptions(options, "axes", "x", "y", "origin", "size", "color", "labelColor", "width", "labels", "labelSize",
            "xLabel", "yLabel", "arrows", "grid", "amount", "desc");
        var xr = AxisRange(o, "x");
        var yr = AxisRange(o, "y");
        var origin = Pair(o, "origin") ?? throw new ArgumentException("axes needs origin, the centre of its box: [x, y].");
        var size = Pair(o, "size") ?? throw new ArgumentException("axes needs size, its box: [width, height].");
        if (!(size[0] > 0 && size[1] > 0)) throw new ArgumentException("axes' size is a positive [width, height].");

        OffFrame(origin, size);

        var color = o.Raw("color") as string ?? "#444444";
        var stroke = o.Number("width", Math.Min(size[0], size[1]) / 300);
        var group = Group(o.Subset("amount", "desc"));
        var axes = new MotionAxes(group, xr, yr, origin[0], origin[1], size[0], size[1], Root?.YDown ?? true, stroke);

        var crossX = xr[0] <= 0 && 0 <= xr[1] ? 0 : xr[0];
        var crossY = yr[0] <= 0 && 0 <= yr[1] ? 0 : yr[0];
        string Seg(double x0, double y0, double x1, double y1)
        {
            var (a, b) = (axes.Point(x0, y0), axes.Point(x1, y1));
            return FormattableString.Invariant($"M{a[0]:0.###} {a[1]:0.###} L{b[0]:0.###} {b[1]:0.###} ");
        }

        var xTicks = Ticks(xr);
        var yTicks = Ticks(yr);
        if (o.Bool("grid", false))
        {
            var grid = new StringBuilder();
            foreach (var t in xTicks) grid.Append(Seg(t, yr[0], t, yr[1]));
            foreach (var t in yTicks) grid.Append(Seg(xr[0], t, xr[1], t));
            group.Outline(new Hashtable { ["path"] = grid.ToString(), ["color"] = "#dddddd", ["width"] = stroke * 0.6 });
        }

        var tick = Math.Min(size[0], size[1]) / 60;
        var tickX = (xr[1] - xr[0]) / size[0] * tick;
        var tickY = (yr[1] - yr[0]) / size[1] * tick;
        var lines = new StringBuilder(Seg(xr[0], crossY, xr[1], crossY)).Append(Seg(crossX, yr[0], crossX, yr[1]));
        foreach (var t in xTicks) lines.Append(Seg(t, crossY - tickY, t, crossY + tickY));
        foreach (var t in yTicks) lines.Append(Seg(crossX - tickX, t, crossX + tickX, t));
        group.Outline(new Hashtable { ["path"] = lines.ToString(), ["color"] = color, ["width"] = stroke });

        // A tip at each axis's far end, which says the axis goes on: Manim's default, and half of why its graphs read at a glance.
        if (o.Bool("arrows", false))
        {
            var (len, half) = (Math.Max(stroke * 6, tick * 1.8), Math.Max(stroke * 3, tick * 0.9));
            var xEnd = axes.Point(xr[1], crossY);
            var yEnd = axes.Point(crossX, yr[1]);
            var up = -(Root?.YDown ?? true ? 1 : -1);
            string Tip(double[] p, double dx, double dy) => FormattableString.Invariant(
                $"M{p[0] + dx * len:0.###} {p[1] + dy * len:0.###} L{p[0] - dy * half:0.###} {p[1] + dx * half:0.###} L{p[0] + dy * half:0.###} {p[1] - dx * half:0.###} Z ");
            group.Region(new Hashtable { ["path"] = Tip(xEnd, 1, 0) + Tip(yEnd, 0, up), ["color"] = color });
        }

        var (xLabel, yLabel) = (o.Raw("xLabel") as string, o.Raw("yLabel") as string);
        var labels = o.Bool("labels", true);
        if (!labels && xLabel is null && yLabel is null) return axes;

        // The smallest a viewer, and the read-back's watcher, reads: 16 px at 720 lines, measured (2026-10-22).
        var least = Root is { } root ? root.ViewRect.Height * MinLabelShare : 0;
        var labelSize = o.Number("labelSize", Math.Max(Math.Min(size[0], size[1]) / 18, least));
        if (labelSize < least - 1e-9)
            throw new ArgumentException($"axes' labelSize {MotionAxes.Label(labelSize)} is below {MotionAxes.Label(Math.Round(least, 1))}, "
                + "the smallest a viewer reads at this frame (16 px at 720 lines): leave it out, or give the axes a larger box.");
        // Labels are text and lines are not: ftcexplainer4 dimmed its axes to #475569 and its labels went with them, at
        // 2.4 : 1 on its ground, and the read-back missed every one.
        var labelColor = o.Raw("labelColor") as string ?? color;
        var down = Root?.YDown ?? true ? 1 : -1;

        // What each axis measures, as LaTeX (\text{Time} for a word set upright): the x axis's above its far end, ending
        // there; the y axis's beside its top. Listed by comp.texts() as kind 'axis', a ruler like the ticks.
        if (xLabel is not null)
        {
            var p = axes.Point(xr[1], crossY);
            group.Equation(new Hashtable { ["latex"] = xLabel, ["origin"] = new[] { p[0], p[1] - down * (tick + labelSize * 0.9) }, ["size"] = labelSize, ["orient"] = new[] { 1d, 0.5 }, ["color"] = labelColor }, "axis");
        }
        if (yLabel is not null)
        {
            var p = axes.Point(crossX, yr[1]);
            group.Equation(new Hashtable { ["latex"] = yLabel, ["origin"] = new[] { p[0] + tick + labelSize * 0.6, p[1] }, ["size"] = labelSize, ["orient"] = new[] { 0d, 0.5 }, ["color"] = labelColor }, "axis");
        }

        if (labels)
        {
            foreach (var t in xTicks.Where(t => Math.Abs(t - crossX) > 1e-9))
            {
                var p = axes.Point(t, crossY);
                group.Equation(new Hashtable { ["latex"] = MotionAxes.Label(t), ["origin"] = new[] { p[0], p[1] + down * (tick + labelSize * 0.6) }, ["size"] = labelSize, ["color"] = labelColor }, "tick");
            }
            foreach (var t in yTicks.Where(t => Math.Abs(t - crossY) > 1e-9))
            {
                var p = axes.Point(crossX, t);
                group.Equation(new Hashtable { ["latex"] = MotionAxes.Label(t), ["origin"] = new[] { p[0] - tick - labelSize * 0.4, p[1] }, ["size"] = labelSize, ["orient"] = new[] { 1d, 0.5 }, ["color"] = labelColor }, "tick");
            }
        }
        return axes;
    }

    private static double[] AxisRange(MotionOptions o, string key)
    {
        if (o.Raw(key) is not IList { Count: 3 } r)
            throw new ArgumentException($"axes' {key} is [min, max, step], such as [-3, 3, 1].");
        var v = r.Cast<object?>().Select(e => Convert.ToDouble(e, CultureInfo.InvariantCulture)).ToArray();
        if (!(v[1] > v[0]) || !(v[2] > 0) || (v[1] - v[0]) / v[2] > 100)
            throw new ArgumentException($"axes' {key} is [min, max, step] with min below max and at most 100 steps; it was [{string.Join(", ", v)}].");
        return v;
    }

    private static double[]? Pair(MotionOptions o, string key) =>
        o.Raw(key) is IList { Count: 2 } p ? [Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Convert.ToDouble(p[1], CultureInfo.InvariantCulture)] : null;

    /// <summary>The multiples of the step inside the range.</summary>
    private static List<double> Ticks(double[] r)
    {
        var ticks = new List<double>();
        for (var k = Math.Ceiling(r[0] / r[2] - 1e-9); k * r[2] <= r[1] + 1e-9; k++) ticks.Add(Math.Round(k * r[2], 10));
        return ticks;
    }
}
