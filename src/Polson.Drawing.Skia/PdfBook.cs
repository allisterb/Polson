namespace Polson.Drawing.Skia;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

/// <summary>
/// A short presentation as a multi-page PDF: a header on every page, then a title, an image, its caption and text, in
/// that order — the process book a studio hands its client with the finished work (<c>Stage.book</c>).
/// </summary>
/// <remarks>
/// <para>
/// Lupton's concept presentations are sequential, simple and concise, with a header on each page naming the project
/// (<i>Graphic Design Thinking</i>, pp. 108–109). Pages are 16:9 at 960 × 540 points, the shape of a projected slide.
/// </para>
/// <para>
/// Text is a light Markdown: <c>#</c> headings, <c>-</c> bullets, paragraphs split on blank lines, and <c>**</c>, <c>`</c>
/// and link syntax dropped. A page whose text runs past its bottom continues on the next under the same title. Images
/// arrive as bytes, decoded here, never from a path: a Skia-opened path is a handle a child process inherits.
/// </para>
/// </remarks>
public static class PdfBook
{
    #region Types
    /// <summary>One page: any of a title, an image (encoded bytes), a caption and text.</summary>
    public sealed record Page(string? Title, byte[]? Image, string? Caption, string? Text);
    #endregion

    #region Constants
    public const float Width = 960, Height = 540;
    private const float Margin = 40, HeaderY = 26, TitleY = 74;
    #endregion

    #region Methods
    /// <summary>
    /// The book as PDF bytes, how many pages it came to, and for each of <paramref name="pages"/> how many continuation pages
    /// its text overflowed onto. A page a top-level heading opens is not a continuation: a concept to a page is the format.
    /// </summary>
    public static (byte[] Pdf, int Pages, int[] Overflows) Write(string title, IReadOnlyList<Page> pages)
    {
        using var stream = new MemoryStream();
        var count = 0;
        var parts = pages.Select(p => Paginate(p).ToList()).ToArray();
        using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title, Creator = "Polson Studio" }))
        {
            var layouts = parts.SelectMany(p => p).ToList();
            foreach (var (page, lines, continued, overflow) in layouts)
            {
                count++;
                using var canvas = document.BeginPage(Width, Height);
                Render(canvas, title, page, lines, continued, overflow, count, layouts.Count);
                document.EndPage();
            }
            document.Close();
        }
        return (stream.ToArray(), count, [.. parts.Select(p => p.Count(part => part.Overflow))]);
    }
    #endregion

    #region Methods (private)
    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;

    /// <summary>A line of laid-out text: what it says, its size, whether bold, its indent, and the space before it.</summary>
    private sealed record Line(string Text, float Size, bool Strong, float Indent, float Before, bool Opens = false);

    /// <summary>
    /// The page, split where its text overflows and before each top-level heading after the first, so a concepts file
    /// gives each concept its page, as a concept presentation does; the first part keeps the image.
    /// </summary>
    private static IEnumerable<(Page Page, List<Line> Lines, bool Continued, bool Overflow)> Paginate(Page page)
    {
        var textLeft = page.Image is null ? Margin : Width * 0.58f;
        var width = Width - Margin - textLeft;
        var lines = Lay(page.Text ?? "", width);
        var top = page.Title is null ? TitleY - 24 : TitleY + 18;
        var room = Height - Margin - top;
        var part = new List<Line>();
        float used = 0;
        var continued = false;
        var overflow = false;
        foreach (var line in lines)
        {
            var need = line.Before + line.Size * 1.35f;
            var opens = line.Opens && part.Any(l => l.Text.Length > 0);
            if ((used + need > room || opens) && part.Count > 0)
            {
                yield return (continued ? page with { Image = null, Caption = null } : page, part, continued, overflow);
                (part, used, continued, overflow) = ([], 0, true, !opens);
                need = line.Size * 1.35f;
                part.Add(line with { Before = 0 });
                used += need;
                continue;
            }
            part.Add(line);
            used += need;
        }
        yield return (continued ? page with { Image = null, Caption = null } : page, part, continued, overflow);
    }

    /// <summary>Light Markdown, wrapped to <paramref name="width"/> points.</summary>
    private static List<Line> Lay(string text, float width)
    {
        var lines = new List<Line>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n").Trim(), @"\n\s*\n"))
        {
            foreach (var raw in block.Split('\n'))
            {
                var line = Plain(raw.TrimEnd());
                if (line.Trim().Length == 0 || Regex.IsMatch(line, @"^\s*\|?\s*:?-{3,}")) continue;
                var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                var bullet = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
                if (heading.Success)
                {
                    var size = heading.Groups[1].Length <= 2 ? 17f : 14f;
                    var opens = heading.Groups[1].Length == 1;
                    lines.AddRange(Wrap(heading.Groups[2].Value, size, true, 0, width).Select((l, i) => i == 0 ? l with { Before = 10, Opens = opens } : l));
                }
                else if (bullet.Success)
                    lines.AddRange(Wrap("•  " + bullet.Groups[2].Value, 12.5f, false, 14, width).Select((l, i) => i == 0 ? l with { Before = 3 } : l with { Indent = 26 }));
                else
                    lines.AddRange(Wrap(line.Replace("|", "  ").Trim(), 12.5f, false, 0, width));
            }
            lines.Add(new Line("", 6, false, 0, 0));
        }
        return lines;
    }

    private static string Plain(string s) =>
        Regex.Replace(Regex.Replace(s.Replace("**", "").Replace("`", ""), @"\[([^\]]*)\]\([^)]*\)", "$1"), @"(?<!\w)\*(\S[^*]*)\*(?!\w)", "$1");

    private static IEnumerable<Line> Wrap(string text, float size, bool strong, float indent, float width)
    {
        using var font = new SKFont(strong ? Bold : Regular, size);
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var trial = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureText(trial) > width - indent)
            {
                yield return new Line(line, size, strong, indent, 0);
                line = word;
            }
            else line = trial;
        }
        if (line.Length > 0) yield return new Line(line, size, strong, indent, 0);
    }

    private static void Render(SKCanvas canvas, string book, Page page, List<Line> lines, bool continued, bool overflow, int number, int total)
    {
        canvas.Clear(new SKColor(0xfa, 0xf8, 0xf4));
        using var ink = new SKPaint { Color = new SKColor(0x1d, 0x1f, 0x24), IsAntialias = true };
        using var muted = new SKPaint { Color = new SKColor(0x7a, 0x7f, 0x88), IsAntialias = true };
        using var rule = new SKPaint { Color = new SKColor(0xd8, 0xd3, 0xc9), StrokeWidth = 1, IsStroke = true };
        using var small = new SKFont(Regular, 10);
        using var titleFont = new SKFont(Bold, 24);
        using var captionFont = new SKFont(Regular, 11);

        // The header Lupton asks for on every page: the project, and where this page sits.
        canvas.DrawText(book, Margin, HeaderY, SKTextAlign.Left, small, muted);
        canvas.DrawText($"{number} of {total}", Width - Margin, HeaderY, SKTextAlign.Right, small, muted);
        canvas.DrawLine(Margin, HeaderY + 8, Width - Margin, HeaderY + 8, rule);

        if (page.Title is { } title)
            canvas.DrawText(overflow ? $"{title} (continued)" : title, Margin, TitleY, SKTextAlign.Left, titleFont, ink);

        var top = page.Title is null ? TitleY - 24 : TitleY + 18;
        if (!continued && page.Image is { } bytes)
        {
            using var image = SKImage.FromEncodedData(bytes) ?? throw new ArgumentException("Stage.book: an image could not be decoded.");
            var boxW = (lines.Any(l => l.Text.Length > 0) ? Width * 0.58f - Margin * 1.5f : Width - Margin * 2);
            var boxH = Height - top - Margin - (page.Caption is null ? 0 : 22);
            var scale = Math.Min(boxW / image.Width, boxH / image.Height);
            var rect = SKRect.Create(Margin, top, image.Width * scale, image.Height * scale);
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.DrawRect(rect, rule);
            if (page.Caption is { } caption)
                foreach (var (l, i) in Wrap(Plain(caption), 11, false, 0, Math.Max(rect.Width, 260)).Take(2).Select((l, i) => (l, i)))
                    canvas.DrawText(l.Text, Margin, rect.Bottom + 15 + i * 13, SKTextAlign.Left, captionFont, muted);
        }

        var x = page.Image is null || continued ? Margin : Width * 0.58f;
        var y = top;
        foreach (var line in lines)
        {
            y += line.Before + line.Size * 1.35f;
            if (line.Text.Length == 0) continue;
            using var font = new SKFont(line.Strong ? Bold : Regular, line.Size);
            canvas.DrawText(line.Text, x + line.Indent, y - line.Size * 0.3f, SKTextAlign.Left, font, ink);
        }
    }
    #endregion
}
