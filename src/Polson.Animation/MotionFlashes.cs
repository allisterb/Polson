namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// WCAG 2.3.1, <i>Three Flashes or Below Threshold</i>, measured on a composition: every frame drawn small, each pixel's
/// luminance and redness followed through time, and the flashes that cover enough of the eye's view counted per second.
/// </summary>
/// <remarks>
/// WCAG's definitions, as the Success Criterion's glossary states them:
/// <list type="bullet">
/// <item>A <b>general flash</b> is a pair of opposing changes in relative luminance of 10% or more of the maximum, where
/// the darker state is below 0.80.</item>
/// <item>A <b>red flash</b> is a pair of opposing transitions involving a saturated red: to or from a state with
/// R / (R + G + B) ≥ 0.8, the two states more than 0.2 apart in CIE 1976 u′v′.</item>
/// <item>Content fails when there are <b>more than three</b> of either in any one second, and the flashes occurring
/// together cover <b>more than 25% of any 10° field of view</b> — a 341 × 256 rectangle on a screen 1024 wide.</item>
/// </list>
/// A transition is counted where a pixel turns from its last peak or valley by the threshold, so a slow swing counts as
/// surely as a cut. Drawn small, a pattern finer than the eye resolves at viewing distance — WCAG's exemption for a fine
/// balanced pattern — averages out before it is measured. Relative luminance is WCAG's: linearised sRGB, weighted
/// 0.2126 / 0.7152 / 0.0722. Frames are read over <c>background</c>, as the video is.
/// <para>
/// A measurement for a maker, not a certification: a broadcaster's check is the Harding test, which this follows in
/// outline but has not been compared against.
/// </para>
/// </remarks>
internal static class MotionFlashes
{
    #region Constants
    private const double LuminanceStep = 0.1, DarkerBelow = 0.8, RedShare = 0.8, ChromaStep = 0.2, AreaShare = 0.25;

    /// <summary>A 10° field at typical viewing distance, as a share of a screen 1024 wide: 341 × 256.</summary>
    private const double FieldWidth = 341d / 1024, FieldHeight = 256d / 1024;

    // D65 in u′v′, for a pixel too dark to have a chromaticity.
    private const float WhiteU = 0.1978f, WhiteV = 0.4683f;
    #endregion

    #region Methods
    public static Dictionary<string, object?> Measure(MotionComposition comp, object? options)
    {
        var o = new MotionOptions(options, "composition.flashes", "from", "to", "width", "background");
        var from = o.Number("from", 0);
        var to = o.Number("to", comp.Duration);
        var width = o.Number("width", 160);
        if (width is < 32 or > 640) throw new ArgumentException("composition.flashes: width is the size drawn to measure, from 32 to 640 pixels.");
        if (to < from) throw new ArgumentException("composition.flashes: to comes after from.");
        var background = SkiaColorParser.Parse(o.Raw("background")?.ToString() ?? "#ffffff").WithAlpha(255);

        var scale = Math.Min(1, width / comp.Width);
        var first = (int)Math.Ceiling(from * comp.Fps - 1e-9);
        var last = (int)Math.Floor(to * comp.Fps + 1e-9);
        Pixels? state = null;
        var events = new List<Transition>();

        for (var f = first; f <= last; f++)
        {
            var time = f / comp.Fps;
            using var frame = comp.Render(time, scale);
            using var flat = new SKBitmap(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(flat))
            {
                c.Clear(background);
                c.DrawBitmap(frame.SkBitmap, 0, 0);
            }

            if (state is null)
            {
                state = new Pixels(flat);
                continue;
            }

            var (up, down, toRed, fromRed) = state.Step(flat);
            var (fw, fh) = (Math.Max(1, (int)Math.Round(FieldWidth * flat.Width)), Math.Max(1, (int)Math.Round(FieldHeight * flat.Width)));
            Add("general", up, down);
            Add("red", toRed, fromRed);

            void Add(string kind, bool[] rise, bool[] fall)
            {
                var a = Share(rise, flat.Width, flat.Height, fw, fh);
                var b = Share(fall, flat.Width, flat.Height, fw, fh);
                if (Math.Max(a, b) > AreaShare) events.Add(new(time, kind, a >= b ? 1 : -1, Math.Max(a, b)));
            }
        }

        var general = Worst(events.Where(e => e.Kind == "general"));
        var red = Worst(events.Where(e => e.Kind == "red"));
        var worst = general.Flashes >= red.Flashes ? general : red;
        return new()
        {
            ["passed"] = general.Flashes <= 3 && red.Flashes <= 3,
            ["generalFlashes"] = general.Flashes,
            ["redFlashes"] = red.Flashes,
            ["worstAt"] = worst.Flashes >= 1 ? worst.At : null,
            ["transitions"] = events.Select(e => new Dictionary<string, object?>
            {
                ["time"] = Math.Round(e.Time, 4),
                ["kind"] = e.Kind,
                ["direction"] = e.Kind == "red" ? (e.Sign > 0 ? "toRed" : "fromRed") : (e.Sign > 0 ? "brighter" : "darker"),
                ["area"] = Math.Round(e.Area, 3),
            }).ToList(),
            ["frames"] = Math.Max(0, last - first + 1),
            ["width"] = state?.Width ?? 0,
        };
    }

    /// <summary>The largest share of any field-sized window that <paramref name="map"/> covers, by a summed-area table.</summary>
    private static double Share(bool[] map, int w, int h, int fw, int fh)
    {
        (fw, fh) = (Math.Min(fw, w), Math.Min(fh, h));
        var sum = new int[(w + 1) * (h + 1)];
        var any = false;
        for (var y = 0; y < h; y++)
        {
            var row = 0;
            for (var x = 0; x < w; x++)
            {
                if (map[y * w + x]) { row++; any = true; }
                sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + row;
            }
        }
        if (!any) return 0;

        var best = 0;
        for (var y = 0; y + fh <= h; y++)
            for (var x = 0; x + fw <= w; x++)
                best = Math.Max(best, sum[(y + fh) * (w + 1) + x + fw] - sum[y * (w + 1) + x + fw] - sum[(y + fh) * (w + 1) + x] + sum[y * (w + 1) + x]);
        return best / (double)(fw * fh);
    }

    /// <summary>
    /// The most flashes in any one second, and where that second starts. A flash is two opposing transitions, so a
    /// second holding seven is more than three flashes; transitions in one direction in a row are one.
    /// </summary>
    private static (double Flashes, double At) Worst(IEnumerable<Transition> all)
    {
        var turns = new List<Transition>();
        foreach (var t in all.OrderBy(t => t.Time))
            if (turns.Count == 0 || turns[^1].Sign != t.Sign) turns.Add(t);

        var (best, at) = (0, 0d);
        for (int i = 0, j = 0; i < turns.Count; i++)
        {
            while (j < turns.Count && turns[j].Time < turns[i].Time + 1 - 1e-9) j++;
            if (j - i > best) (best, at) = (j - i, turns[i].Time);
        }
        return (best / 2d, at);
    }
    #endregion

    #region Types
    private readonly record struct Transition(double Time, string Kind, int Sign, double Area);

    /// <summary>Each pixel's last peak or valley of luminance and the way it was going, and its last colour state for red.</summary>
    private sealed class Pixels
    {
        public Pixels(SKBitmap first)
        {
            (Width, height) = (first.Width, first.Height);
            var n = Width * height;
            (peak, dir, anchorU, anchorV, anchorRed) = (new float[n], new sbyte[n], new float[n], new float[n], new bool[n]);
            Read(first, (i, l, u, v, red) => (peak[i], anchorU[i], anchorV[i], anchorRed[i]) = (l, u, v, red));
        }

        private readonly int height;
        private readonly float[] peak, anchorU, anchorV;
        private readonly sbyte[] dir;
        private readonly bool[] anchorRed;

        public int Width { get; }

        /// <summary>Moves every pixel on to the next frame, returning where each kind of transition happened.</summary>
        public (bool[] Up, bool[] Down, bool[] ToRed, bool[] FromRed) Step(SKBitmap next)
        {
            var n = Width * height;
            var (up, down, toRed, fromRed) = (new bool[n], new bool[n], new bool[n], new bool[n]);
            Read(next, (i, l, u, v, red) =>
            {
                // Still going the same way, the peak or valley moves with it; turned back by the step, it is a transition.
                var (p, d) = (peak[i], dir[i]);
                if (d > 0 && l > p || d < 0 && l < p) peak[i] = l;
                else if (d <= 0 && l - p >= LuminanceStep && p < DarkerBelow) (up[i], dir[i], peak[i]) = (true, 1, l);
                else if (d >= 0 && p - l >= LuminanceStep && l < DarkerBelow) (down[i], dir[i], peak[i]) = (true, -1, l);

                if (red == anchorRed[i]) (anchorU[i], anchorV[i]) = (u, v);
                else if (Math.Sqrt((u - anchorU[i]) * (u - anchorU[i]) + (v - anchorV[i]) * (v - anchorV[i])) > ChromaStep)
                {
                    if (red) toRed[i] = true;
                    else fromRed[i] = true;
                    (anchorU[i], anchorV[i], anchorRed[i]) = (u, v, red);
                }
            });
            return (up, down, toRed, fromRed);
        }

        private void Read(SKBitmap bitmap, Action<int, float, float, float, bool> each)
        {
            var bytes = bitmap.GetPixelSpan();
            for (var i = 0; i < Width * height; i++)
            {
                var (r, g, b) = (Linear(bytes[4 * i]), Linear(bytes[4 * i + 1]), Linear(bytes[4 * i + 2]));
                var l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                var x = 0.4124f * r + 0.3576f * g + 0.1805f * b;
                var z = 0.0193f * r + 0.1192f * g + 0.9505f * b;
                var d = x + 15 * l + 3 * z;
                var (u, v) = d < 1e-6f ? (WhiteU, WhiteV) : (4 * x / d, 9 * l / d);
                var sum = r + g + b;
                each(i, l, u, v, sum > 1e-6f && r / sum >= RedShare);
            }
        }

        private static readonly float[] Lut = [.. Enumerable.Range(0, 256).Select(i =>
        {
            var c = i / 255d;
            return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        })];

        private static float Linear(byte b) => Lut[b];
    }
    #endregion
}
