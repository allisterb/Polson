namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polson.Drawing.Skia;
using SkiaSharp;

/// <summary>
/// Several finished films side by side in one video, one per audience: each fitted into a cell of a grid, labelled under
/// it, and held on its last frame, dimmed, until the longest ends. The file the studio offers beside its page's grid,
/// where the same films play together at full size; this one is for sending to someone without the page.
/// </summary>
/// <remarks>
/// Made by <c>polson compare</c>, never by an agent: it is a mechanical view of finished work, and building it would
/// cost a run tokens and could take longer than a host's limit on one tool call.
/// </remarks>
public static class Comparison
{
    #region Types
    /// <summary>One film: its file, and the two lines under it — who it is for, and what else to say of them.</summary>
    public sealed record Film(string Path, string Title, string Detail);
    #endregion

    #region Fields
    /// <summary>The studio's gallery ground and its two inks, so the file looks like the page it came from.</summary>
    static readonly SKColor Ground = new(0x12, 0x13, 0x17), Ink = new(0xEC, 0xEC, 0xEE), Muted = new(0x9A, 0x9C, 0xA4);
    #endregion

    #region Methods
    /// <summary>
    /// Writes <paramref name="films"/> as one MP4 at <paramref name="output"/>, <paramref name="width"/> pixels wide, with
    /// the sound of the film at <paramref name="sound"/> (an index into <paramref name="films"/>) or none. Returns its bytes.
    /// </summary>
    /// <remarks>
    /// Two films sit side by side, three or four in two rows of two, five or six in two rows of three. Every cell takes
    /// the first film's shape; a film of another is letterboxed in it. The frame rate is the fastest film's and the
    /// length the longest's. Each label ends with that film's own length, read from the file, so a short one is plain.
    /// </remarks>
    public static long Write(IReadOnlyList<Film> films, string output, int width = 1920, int? sound = null)
    {
        const string who = "compare";
        if (films.Count is < 2 or > 6) throw new ArgumentException($"{who} sets 2 to 6 films side by side, not {films.Count}.");
        if (width is < 640 or > 3840) throw new ArgumentException($"{who}: width {width} is not between 640 and 3840.");
        if (!string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{who} writes an .mp4, not '{Path.GetFileName(output)}'.");
        if (sound is { } s && (s < 0 || s >= films.Count))
            throw new ArgumentException($"{who}: there is no film {s + 1} to take the sound from; there are {films.Count}.");

        var probed = films.Select(f =>
        {
            var full = Path.GetFullPath(f.Path);
            if (!File.Exists(full)) throw new FileNotFoundException($"{who}: there is no film at '{f.Path}'.", f.Path);
            var format = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".mp4" => FFmpeg.VideoFormat.Mp4,
                ".webm" => FFmpeg.VideoFormat.WebM,
                var ext => throw new ArgumentException($"{who} reads .mp4 or .webm films, not '{ext}' ('{f.Path}').")
            };
            return (Film: f, Full: full, Format: format, Info: FFmpeg.Probe(full, f.Path, format, who));
        }).ToArray();
        if (sound is { } k && !probed[k].Info.HasAudio)
            throw new ArgumentException($"{who}: film {k + 1} ('{films[k].Path}') has no sound to keep.");

        // The grid: cells of the first film's shape, a label band under each, gutters round all, every measure even for 4:2:0.
        static int Even(double x) => Math.Max(2, (int)Math.Round(x / 2) * 2);
        var cols = films.Count <= 4 ? 2 : 3;
        var rows = (films.Count + cols - 1) / cols;
        var gutter = Even(width * 0.0125);
        var band = Even(width * 0.034);
        var cellW = Even((width - gutter * (cols + 1)) / (double)cols);
        var first = probed[0].Info;
        var cellH = Even(cellW * first.Height / (double)first.Width);
        var (w, h) = (gutter + cols * (cellW + gutter), gutter + rows * (cellH + band + gutter));
        var fps = Math.Min(60, probed.Max(p => p.Info.Fps));
        var seconds = probed.Max(p => p.Info.Seconds);

        var cells = probed.Select((p, i) => new FFmpeg.GridCell(p.Full, p.Format, p.Info.Seconds,
            gutter + i % cols * (cellW + gutter), gutter + i / cols * (cellH + band + gutter), cellW, cellH)).ToArray();

        using var background = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(background))
        {
            canvas.Clear(Ground);
            using var face = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            using var plain = SKTypeface.FromFamilyName("Arial");
            using var title = new SKFont(face, band * 0.36f) { Edging = SKFontEdging.Antialias };
            using var detail = new SKFont(plain, band * 0.29f) { Edging = SKFontEdging.Antialias };
            using var ink = new SKPaint { Color = Ink, IsAntialias = true };
            using var muted = new SKPaint { Color = Muted, IsAntialias = true };
            using var black = new SKPaint { Color = SKColors.Black };
            for (var i = 0; i < cells.Length; i++)
            {
                var c = cells[i];
                canvas.DrawRect(c.X, c.Y, c.Width, c.Height, black);
                var under = c.Y + c.Height;
                var said = string.Join(" · ", new[] { films[i].Detail.Trim(), Clock(c.Seconds) }.Where(t => t.Length > 0));
                TextShaper.DrawText(canvas, Fit(films[i].Title.Trim(), title, c.Width), c.X, under + band * 0.48f, SKTextAlign.Left, title, ink);
                TextShaper.DrawText(canvas, Fit(said, detail, c.Width), c.X, under + band * 0.88f, SKTextAlign.Left, detail, muted);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        return FFmpeg.Grid(background, cells, fps, seconds, sound, Path.GetFullPath(output), who);
    }

    /// <summary>m:ss, as the page writes a film's length.</summary>
    static string Clock(double seconds)
    {
        var s = (int)Math.Round(seconds);
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary><paramref name="text"/> cut with an ellipsis to fit <paramref name="width"/> in <paramref name="font"/>.</summary>
    static string Fit(string text, SKFont font, float width)
    {
        if (TextShaper.Measure(text, font) <= width) return text;
        var cut = text.Length;
        while (cut > 1 && TextShaper.Measure(text[..cut].TrimEnd() + "…", font) > width) cut--;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;                 // never half a character
        return text[..cut].TrimEnd() + "…";
    }
    #endregion
}
