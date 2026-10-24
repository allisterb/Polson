namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// Where a composition shows nothing: frames sampled small, and a frame blank when almost every pixel is its ground.
/// </summary>
/// <remarks>
/// A Manimator film of the Fourier transform (2026-10-23) was an empty screen for 9.8 of its 44 seconds: a fade out, a
/// wait, a fade in, between every scene. Nothing in a film's own record says so, since an empty frame sets no text.
/// <para>
/// A frame's ground is its most common colour, so a dark film and a light one are read alike and a fill is not content.
/// A frame is blank when no more than <see cref="InkShare"/> of its pixels differ from the ground by more than
/// <see cref="Tolerance"/> in any channel: drawn 320 wide, a line of 24 px type at 1280 is some hundreds of pixels, and
/// the grain of an encode is none.
/// </para>
/// </remarks>
internal static class MotionBlank
{
    #region Constants
    private const int Tolerance = 16;
    private const double InkShare = 0.0005;
    #endregion

    #region Methods
    public static Dictionary<string, object?> Measure(MotionComposition comp, object? options)
    {
        var o = new MotionOptions(options, "composition.blank", "from", "to", "longest", "width", "fps", "background");
        var from = o.Number("from", 0);
        var to = o.Number("to", comp.Duration);
        var longest = o.Number("longest", 1);
        var width = o.Number("width", 320);
        var fps = Math.Min(o.Number("fps", 6), comp.Fps);
        if (width is < 64 or > 960) throw new ArgumentException("composition.blank: width is the size drawn to look, from 64 to 960 pixels.");
        if (!(fps > 0)) throw new ArgumentException("composition.blank: fps is how many frames a second are looked at, more than 0.");
        if (to < from) throw new ArgumentException("composition.blank: to comes after from.");
        var background = SkiaColorParser.Parse(o.Raw("background")?.ToString() ?? "#ffffff").WithAlpha(255);

        var scale = Math.Min(1, width / comp.Width);
        var steps = Math.Max(1, (int)Math.Floor((to - from) * fps + 1e-9));
        var blank = new bool[steps + 1];
        var drawn = 0;
        for (var i = 0; i <= steps; i++)
        {
            using var frame = comp.Render(from + i / fps, scale);
            using var flat = new SKBitmap(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(flat))
            {
                c.Clear(background);
                c.DrawBitmap(frame.SkBitmap, 0, 0);
            }
            (blank[i], drawn) = (Blank(flat), flat.Width);
        }

        // Runs of blank samples, each spanning from its first sample to the sample after its last, clipped to the passage.
        var stretches = new List<(double From, double To)>();
        for (var i = 0; i <= steps; i++)
        {
            if (!blank[i]) continue;
            var j = i;
            while (j + 1 <= steps && blank[j + 1]) j++;
            stretches.Add((from + i / fps, Math.Min(to, from + (j + 1) / fps)));
            i = j;
        }
        var over = stretches.Where(s => s.To - s.From > longest + 1e-9).ToList();
        return new()
        {
            ["passed"] = over.Count == 0,
            ["stretches"] = stretches.Select(s => (object?)new Dictionary<string, object?>
            {
                ["from"] = Math.Round(s.From, 3), ["to"] = Math.Round(s.To, 3), ["seconds"] = Math.Round(s.To - s.From, 3),
            }).ToArray(),
            ["blankSeconds"] = Math.Round(stretches.Sum(s => s.To - s.From), 3),
            ["longestAllowed"] = longest,
            ["frames"] = steps + 1,
            ["width"] = drawn,
        };
    }

    /// <summary>Whether almost every pixel is the frame's most common colour.</summary>
    private static bool Blank(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var ground = pixels.GroupBy(p => (uint)p & 0xF0F0F0F0u).MaxBy(g => g.Count())!.First();
        var ink = pixels.Count(p => Math.Abs(p.Red - ground.Red) > Tolerance || Math.Abs(p.Green - ground.Green) > Tolerance
            || Math.Abs(p.Blue - ground.Blue) > Tolerance);
        return ink <= InkShare * pixels.Length;
    }
    #endregion
}
