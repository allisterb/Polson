namespace Polson.Tests.Drawing;

using System;
using System.Linq;
using Polson.Drawing.Skia;
using SkiaSharp;
using Xunit;

/// <summary>
/// The words a book sets itself: a face for every character, and the Markdown a treatment or a brief is written in.
/// </summary>
/// <remarks>
/// becool2 (2026-10-25) set its pitch book in a Latin face and every Japanese character came out as a box, with no
/// warning, while <c>comp.text</c> set the same lines in the same run; a blockquote's <c>&gt;</c> markers were printed
/// mid-sentence where its lines were joined.
/// </remarks>
public class PdfBookTextTests : TestsRuntime
{
    #region Methods
    [Fact]
    public void TestJapaneseInALatinBookIsSetFromAFaceThatHasIt()
    {
        var fallback = SKFontManager.Default.MatchCharacter("Arial", SKFontStyle.Normal, null, char.ConvertToUtf32("映", 0));
        if (fallback is null || !PdfBook.Installed("Arial"))
        {
            Info("Skipped: needs Arial and a face with Japanese.");
            return;
        }

        var (pdf, _, _, previews) = PdfBook.Write("Be Cool", [new PdfBook.Page("Treatment", null, null, "映画の世界から")],
            new PdfBook.BookStyle(Font: "Arial", Cover: null), previewScale: 1);

        // Drawn as outlines, so the Japanese face is not embedded: Skia embeds a face whole, and Yu Gothic is 13 MB.
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain(fallback.PostScriptName, text, StringComparison.Ordinal);
        Assert.True(pdf.Length < 3 * 1024 * 1024, $"the book is {pdf.Length:N0} bytes");

        // And the line is still drawn: ink where the text sits, under the title. (That the glyphs are Japanese and not
        // boxes is the shaper's test, TestACharacterTheFaceLacksIsDrawnFromOneThatHasIt.)
        using var page = SKBitmap.Decode(previews[0]);
        var ink = 0;
        for (var y = 100; y < 135; y++)
            for (var x = 56; x < 260; x++)
                if (page.GetPixel(x, y).Red < 128) ink++;
        Assert.True(ink > 150, $"{ink} ink pixels where the Japanese line is");
    }

    [Fact]
    public void TestAQuoteIsIndentedAndItsMarkersAreNotText()
    {
        using var look = new PdfBook.Look(new PdfBook.BookStyle());

        var lines = PdfBook.Lay("Before.\n\n> Fifteen seconds is a handful\n> of beats.", 800, look).Where(l => l.Text.Length > 0).ToArray();

        Assert.Equal(["Before.", "Fifteen seconds is a handful of beats."], lines.Select(l => l.Text));
        Assert.Equal([0f, 18f], lines.Select(l => l.Indent));
    }

    [Fact]
    public void TestATableRowIsItsCellsAndItsHeaderIsBold()
    {
        using var look = new PdfBook.Look(new PdfBook.BookStyle());

        var lines = PdfBook.Lay("| Parameter | Value |\n| :--- | :--- |\n| Length in seconds | 15 |\n| Sound | |", 800, look)
            .Where(l => l.Text.Length > 0).ToArray();

        Assert.Equal(["Parameter · Value", "Length in seconds · 15", "Sound"], lines.Select(l => l.Text));
        Assert.Equal([true, false, false], lines.Select(l => l.Strong));
    }
    #endregion
}
