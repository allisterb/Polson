namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;

/// <summary>
/// The animator's two graphs of keyed values — value over time with a dot at every frame, and speed over time — drawn,
/// and measured: <c>Motion.graph</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An agent judges a move from frames, and a move is a curve.</b> Animators read the speed graph's shape (Colette &amp;
/// Shaw, <i>Motion Design Toolkit</i>, ch. 2) and the spacing of the frames on a timing chart (Lasseter 1987, §2.7); an
/// agent keyed values and could only render stills. This draws both and returns what they show as numbers, so a check can
/// test the motion: where it peaks, where it **stops at a key it should pass through** (a middle key left on
/// <c>halt</c>, the stutter Colette and Shaw single out), where a segment **overshoots** its keys (Lasseter's Luxo base
/// through the floor), and where it holds.
/// </para>
/// <para>
/// Measured on the node's own evaluation, exactly as the film is drawn: speed by central differences, a key's stop by the
/// speed at the key when the motion carries on in the same direction through it. Each curve is drawn to its own range,
/// so shapes compare across units; the numbers are in the value's own units.
/// </para>
/// </remarks>
internal static class MotionCurves
{
    #region Methods
    /// <summary>The graph as PNG bytes, and one report per curve.</summary>
    internal static (byte[] Png, List<Dictionary<string, object?>> Reports) Plot(
        IReadOnlyList<(string Label, MotionNode Node)> curves, double from, double to, double fps, int width, int height)
    {
        var count = Math.Clamp((int)Math.Ceiling((to - from) * fps * 6), 300, 4000);
        var times = Enumerable.Range(0, count + 1).Select(i => from + (to - from) * i / count).ToArray();
        var h = Math.Max(1e-5, Math.Min(1e-3, (to - from) / 1e4));

        var plotted = curves.Select(c =>
        {
            var keyed = Keyed(c.Node).ToArray();
            var jumps = keyed.SelectMany(Holds).Select(s => s.To).ToArray();
            var values = times.Select(t => c.Node.Evaluate(t)).ToArray();
            // A hold's jump is a cut, not a speed: the samples straddling one are left out of the speed curve.
            var speeds = times.Select(t => jumps.Any(j => Math.Abs(t - j) <= 2 * h + 1e-9) ? double.NaN : Speed(c.Node, t, h)).ToArray();
            return (c.Label, c.Node, Keyed: keyed, Values: values, Speeds: speeds);
        }).ToArray();

        var reports = plotted.Select(p => Report(p.Label, p.Node, p.Keyed, times, p.Speeds, h)).ToList();
        return (Draw(plotted.Select(p => (p.Label, p.Node, p.Keyed, p.Values, p.Speeds)).ToArray(), times, from, to, fps, width, height), reports);
    }

    /// <summary>The time range the keyed values inside <paramref name="nodes"/> span, or null when none is keyed.</summary>
    internal static (double From, double To)? Span(IEnumerable<MotionNode> nodes)
    {
        var keyed = nodes.SelectMany(Keyed).ToArray();
        return keyed.Length == 0 ? null : (keyed.Min(a => a.Start), keyed.Max(a => a.End));
    }

    internal static string EaseName(MotionEase ease) => ease switch
    {
        MotionEase.Auto => "auto",
        MotionEase.Constant => "constant",
        MotionEase.Linear => "linear",
        MotionEase.Halt => "halt",
        _ => "clamped"
    };

    /// <summary>The keyed values a node is made of: itself, or those inside a composite, a sum, a scale.</summary>
    private static IEnumerable<MotionAnimated> Keyed(MotionNode node) =>
        node is MotionAnimated a ? [a] : node.Children.SelectMany(Keyed);

    private static IEnumerable<(double From, double To)> Holds(MotionAnimated a) =>
        a.Waypoints.Zip(a.Waypoints.Skip(1)).Where(p => p.First.After == MotionEase.Constant || p.Second.Before == MotionEase.Constant)
            .Select(p => (p.First.Time, p.Second.Time));

    /// <summary>How fast the value changes at <paramref name="t"/>, in its units a second: a vector's or colour's by its length.</summary>
    private static double Speed(MotionNode node, double t, double h)
    {
        var (a, b) = (node.Evaluate(t - h), node.Evaluate(t + h));
        return Math.Sqrt(a.Zip(b).Sum(p => (p.Second - p.First) * (p.Second - p.First))) / (2 * h);
    }

    private static Dictionary<string, object?> Report(string label, MotionNode node, MotionAnimated[] keyed, double[] times,
        double[] speeds, double h)
    {
        var finite = Enumerable.Range(0, times.Length).Where(i => double.IsFinite(speeds[i])).ToArray();
        var peak = finite.Length == 0 ? 0 : finite.Max(i => speeds[i]);
        var peakAt = finite.Length == 0 ? (double?)null : times[finite.First(i => speeds[i] == peak)];

        var stops = new List<object?>();
        var overshoots = new List<object?>();
        var holds = new List<object?>();
        var keys = new List<object?>();
        // Measured by the node's own keys when it is one keyed value; a composite's parts are plotted, and its keys marked.
        foreach (var a in keyed)
        {
            var w = a.Waypoints;
            keys.AddRange(w.Select(k => (object?)new Dictionary<string, object?>
            {
                ["time"] = k.Time,
                ["value"] = MotionTypes.ToJs(a.Kind, k.Value),
                ["ease"] = k.Before == k.After ? EaseName(k.After) : $"{EaseName(k.Before)}/{EaseName(k.After)}",
            }));
            if (!ReferenceEquals(a, node)) continue;

            for (var i = 1; i + 1 < w.Count; i++)
            {
                var onward = w[i].Value.Select((v, c) => (v - w[i - 1].Value[c]) * (w[i + 1].Value[c] - v)).Sum();
                var held = w[i].Before == MotionEase.Constant || w[i].After == MotionEase.Constant
                    || w[i - 1].After == MotionEase.Constant || w[i + 1].Before == MotionEase.Constant;
                if (onward > 0 && !held && Speed(a, w[i].Time, h) <= 0.02 * peak) stops.Add(w[i].Time);
            }

            var range = Enumerable.Range(0, w[0].Value.Length).Max(c => w.Max(k => k.Value[c]) - w.Min(k => k.Value[c]));
            for (var i = 0; i + 1 < w.Count; i++)
            {
                if (w[i].After == MotionEase.Constant || w[i + 1].Before == MotionEase.Constant)
                {
                    holds.Add(new Dictionary<string, object?> { ["from"] = w[i].Time, ["to"] = w[i + 1].Time });
                    continue;
                }
                var (t0, t1) = (w[i].Time, w[i + 1].Time);
                var worst = Enumerable.Range(1, 99).Select(s => t0 + (t1 - t0) * s / 100).Select(t => a.Evaluate(t))
                    .Max(v => v.Select((x, c) => Math.Max(Math.Min(w[i].Value[c], w[i + 1].Value[c]) - x,
                        x - Math.Max(w[i].Value[c], w[i + 1].Value[c]))).Max());
                if (worst > 1e-6 + 0.005 * range)
                    overshoots.Add(new Dictionary<string, object?> { ["from"] = t0, ["to"] = t1, ["by"] = Math.Round(worst, 4) });
            }
        }

        return new()
        {
            ["label"] = label,
            ["type"] = node.Type,
            ["keys"] = keys,
            ["peakSpeed"] = Math.Round(peak, 4),
            ["peakAt"] = peakAt,
            ["stops"] = stops,
            ["overshoots"] = overshoots,
            ["holds"] = holds,
        };
    }

    private static byte[] Draw((string Label, MotionNode Node, MotionAnimated[] Keyed, double[][] Values, double[] Speeds)[] curves,
        double[] times, double from, double to, double fps, int width, int height)
    {
        const float left = 64, right = 20, top = 44, gap = 46, bottom = 40;
        var plotW = width - left - right;
        var valueH = (height - top - gap - bottom) * 0.55f;
        var speedH = height - top - gap - bottom - valueH;
        var valueTop = top;
        var speedTop = top + valueH + gap;
        float X(double t) => left + (float)((t - from) / (to - from)) * plotW;

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0xfa, 0xf8, 0xf4));
        using var font = new SKFont(SKTypeface.FromFamilyName("Arial"), 13);
        using var small = new SKFont(SKTypeface.FromFamilyName("Arial"), 11);
        using var ink = new SKPaint { Color = new SKColor(0x22, 0x24, 0x2a), IsAntialias = true };
        using var frameLine = new SKPaint { Color = new SKColor(0xcf, 0xcb, 0xc2), IsStroke = true, StrokeWidth = 1 };
        using var keyLine = new SKPaint
        {
            Color = new SKColor(0xb9, 0xb3, 0xa7), IsStroke = true, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([4, 4], 0)
        };

        canvas.DrawText("Value — each curve drawn to its own range, a dot at every frame", left, valueTop - 12, SKTextAlign.Left, font, ink);
        canvas.DrawText("Speed — each curve drawn to its own peak", left, speedTop - 12, SKTextAlign.Left, font, ink);
        canvas.DrawRect(left, valueTop, plotW, valueH, frameLine);
        canvas.DrawRect(left, speedTop, plotW, speedH, frameLine);

        // Seconds along the bottom, at a step that leaves room for the labels.
        var step = new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60 }.First(s => (to - from) / s <= 12);
        for (var t = Math.Ceiling(from / step) * step; t <= to + 1e-9; t += step)
        {
            canvas.DrawLine(X(t), speedTop + speedH, X(t), speedTop + speedH + 4, ink);
            canvas.DrawText($"{t.ToString("0.##", CultureInfo.InvariantCulture)}s", X(t), speedTop + speedH + 18, SKTextAlign.Center, small, ink);
        }

        // Every key time, dashed through both panels; each curve names its own keys' eases below, in its colour.
        foreach (var t in curves.SelectMany(c => c.Keyed).SelectMany(a => a.Waypoints).Select(k => Math.Round(k.Time, 4)).Distinct())
            canvas.DrawLine(X(t), valueTop, X(t), speedTop + speedH, keyLine);

        SKColor[] palette = [new(0x1f, 0x6f, 0x8b), new(0xc8, 0x55, 0x2b), new(0x5b, 0x8c, 0x3a), new(0x7a, 0x4e, 0xa6), new(0xb8, 0x8a, 0x10)];
        for (var ci = 0; ci < curves.Length; ci++)
        {
            using var label = new SKPaint { Color = palette[ci % palette.Length], IsAntialias = true };
            foreach (var w in curves[ci].Keyed.SelectMany(a => a.Waypoints).GroupBy(k => Math.Round(k.Time, 4)).Select(g => g.First()))
            {
                // Near the right edge a label reads leftward from its line, so it is not cut off.
                var atEnd = X(w.Time) > width - right - 70;
                canvas.DrawText(w.Before == w.After ? EaseName(w.After) : $"{EaseName(w.Before)}/{EaseName(w.After)}",
                    X(w.Time) + (atEnd ? -3 : 3), valueTop + 12 + ci * 12, atEnd ? SKTextAlign.Right : SKTextAlign.Left, small, label);
            }
        }
        var legendX = left;
        for (var ci = 0; ci < curves.Length; ci++)
        {
            var c = curves[ci];
            var colour = palette[ci % palette.Length];
            var components = c.Values[0].Length;
            var lo = c.Values.Min(v => v.Min());
            var hi = c.Values.Max(v => v.Max());
            var span = hi - lo < 1e-12 ? 1 : hi - lo;
            float VY(double v) => valueTop + valueH - 6 - (float)((v - lo) / span) * (valueH - 12);

            for (var comp = 0; comp < components; comp++)
            {
                using var line = new SKPaint
                {
                    Color = colour, IsStroke = true, StrokeWidth = 2, IsAntialias = true,
                    PathEffect = comp == 0 ? null : SKPathEffect.CreateDash([6, 4], 0)
                };
                using var path = new SKPath();
                for (var i = 0; i < times.Length; i++)
                {
                    var p = new SKPoint(X(times[i]), VY(c.Values[i][comp]));
                    if (i == 0) path.MoveTo(p); else path.LineTo(p);
                }
                canvas.DrawPath(path, line);

                // The timing chart: one dot per frame. Bunched dots are an ease, spread dots a fast move.
                using var dot = new SKPaint { Color = colour, IsAntialias = true };
                for (var f = Math.Ceiling(from * fps - 1e-9); f / fps <= to + 1e-9; f++)
                    canvas.DrawCircle(X(f / fps), VY(c.Node.Evaluate(f / fps)[comp]), 2.4f, dot);
            }

            var peak = c.Speeds.Where(double.IsFinite).DefaultIfEmpty(0).Max();
            using (var speedLine = new SKPaint { Color = colour, IsStroke = true, StrokeWidth = 2, IsAntialias = true })
            using (var speedPath = new SKPath())
            {
                var drawing = false;
                for (var i = 0; i < times.Length; i++)
                {
                    if (!double.IsFinite(c.Speeds[i])) { drawing = false; continue; }
                    var p = new SKPoint(X(times[i]), speedTop + speedH - 4 - (float)(peak < 1e-12 ? 0 : c.Speeds[i] / peak) * (speedH - 10));
                    if (!drawing) speedPath.MoveTo(p); else speedPath.LineTo(p);
                    drawing = true;
                }
                canvas.DrawPath(speedPath, speedLine);
            }

            // Legend: the curve's name, its range and its peak, in its own units.
            using var swatch = new SKPaint { Color = colour, IsAntialias = true };
            var parts = components == 2 ? " (x solid, y dashed)" : components == 4 ? " (r, g, b, a)" : "";
            var text = $"{c.Label}{parts}: {Num(lo)} to {Num(hi)}, peak {Num(peak)}/s";
            canvas.DrawRect(legendX, height - 14, 10, 10, swatch);
            canvas.DrawText(text, legendX + 14, height - 5, SKTextAlign.Left, small, ink);
            legendX += 24 + small.MeasureText(text);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string Num(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture);
    #endregion
}
