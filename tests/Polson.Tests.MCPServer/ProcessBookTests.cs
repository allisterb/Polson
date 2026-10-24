namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>Stage.book</c>: the run's process book from what it already made — a concept to a page from <c>concepts.md</c>, a
/// still beside its caption — and refused when it names a file the run did not make.
/// </summary>
public class ProcessBookTests : TestsRuntime, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-book-" + Guid.NewGuid().ToString("N"));

    public ProcessBookTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        using var bitmap = new SKBitmap(64, 36);
        bitmap.Erase(SKColors.SteelBlue);
        File.WriteAllBytes(Path.Combine(root, "artifacts", "frame.png"), bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        File.WriteAllText(Path.Combine(root, "concepts.md"),
            "# A — The Fleet\n**Overview.** One cab becomes a city.\n**Drawbacks.**\n- Busy frames fight the figures.\n\n"
            + "# B — The Empty Seat\n**Overview.** Idle hours as an empty seat.\n\n# Recommendation\nA, because it reads muted.");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private DrawingExecutionResult Run(string body) =>
        new JsDrawingEngine { ProjectRoot = root }.Execute($"{body}\nconst c = createCanvas(8, 8); c;", 8, 8, null, "png", 90);

    [Fact]
    public void TestABookGivesEachConceptItsPageAndAStillItsCaption()
    {
        var result = Run("""
            const b = Stage.book('process.pdf', { title: 'Harbour — process', pages: [
                { title: 'The brief', text: 'A film for two cities.' },
                { title: 'Concepts', file: 'concepts.md' },
                { title: 'The film', image: 'artifacts/frame.png', caption: 'The title moment, as delivered.' }] });
            log('pages ' + b.pages);
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("pages 5"));      // brief, a page per concept and the recommendation, the film
        var pdf = File.ReadAllBytes(Path.Combine(root, "process.pdf"));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    /// <summary>
    /// nightshift1's book was 46 pages, 27 of them direction.md and findings.md pasted whole. A page that runs on, and the
    /// findings, are named; a concepts file a concept to a page is the format, and is not.
    /// </summary>
    [Fact]
    public void TestAPastedFileAndTheFindingsAreWarnedButAConceptToAPageIsNot()
    {
        File.WriteAllText(Path.Combine(root, "direction.md"), string.Join("\n\n", Enumerable.Range(1, 80).Select(i =>
            $"Decision {i}: the amber stays under the claim because the profile's local-only brands lean to the evening register.")));
        File.WriteAllText(Path.Combine(root, "findings.md"), "# Findings\nThe cutout cache took 8 s on a repeat.");

        var warned = Run("""
            const b = Stage.book('process.pdf', { title: 'Harbour', pages: [
                { title: 'Concepts', file: 'concepts.md' }, { title: 'Direction', file: 'direction.md' }, { title: 'Findings', file: 'findings.md' }] });
            log('warnings ' + b.warnings.length);
            """);

        Assert.True(warned.Success, warned.Error);
        Assert.Contains(warned.Logs, l => l.Contains("warnings 2"));
        Assert.Contains(warned.Logs, l => l.StartsWith("[WARN] Stage.book: 'Direction' runs on for", StringComparison.Ordinal));
        Assert.Contains(warned.Logs, l => l.Contains("findings.md is the run's report to the studio"));
        Assert.DoesNotContain(warned.Logs, l => l.Contains("'Concepts' runs on"));
    }

    [Theory]
    [InlineData("{ title: 'X', pages: [{ image: 'artifacts/missing.png' }] }", "is not in the project")]
    [InlineData("{ title: 'X', pages: [{ title: 'Y', picture: 'artifacts/frame.png' }] }", "not 'picture'")]
    [InlineData("{ pages: [{ title: 'Y' }] }", "needs a title")]
    [InlineData("{ title: 'X', pages: [{ image: 'output.mp4' }] }", "is not in the project")]
    public void TestABookThatNamesWhatTheRunDidNotMakeIsRefused(string options, string words)
    {
        var result = Run($"Stage.book('process.pdf', {options});");

        Assert.False(result.Success);
        Assert.Contains(words, result.Error);
        Assert.False(File.Exists(Path.Combine(root, "process.pdf")));
    }
}
