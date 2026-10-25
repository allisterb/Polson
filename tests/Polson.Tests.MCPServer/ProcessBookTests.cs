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
        Assert.Contains(result.Logs, l => l.Contains("pages 6"));      // the cover, the brief, a page per concept and the recommendation, the film
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

    /// <summary>
    /// Shaw's frame for a book (Design for Motion, ch. 4): a cover by default and none when refused, contents once it runs
    /// past eight pages, a closing page for a pitch, and the piece's typeface and accent — a typeface not installed said so.
    /// </summary>
    [Fact]
    public void TestABookHasACoverContentsWhenLongAndAPitchClosesWithThanks()
    {
        var many = string.Join(", ", Enumerable.Range(1, 10).Select(i => $"{{ title: 'Scene {i}', text: 'Scene {i}.' }}"));

        var plain = Run("log('plain ' + Stage.book('a.pdf', { title: 'Harbour', cover: false, pages: [{ title: 'One', text: 'x' }] }).pages);");
        var pitch = Run("log('pitch ' + Stage.book('b.pdf', { title: 'Harbour', kind: 'pitch', accent: '#c8553d', font: 'Georgia', " +
            "cover: { client: 'Harbour Coffee' }, pages: [{ title: 'One', text: 'x' }] }).pages);");
        var longBook = Run($"log('long ' + Stage.book('c.pdf', {{ title: 'Harbour', pages: [{many}] }}).pages);");
        var missing = Run("Stage.book('d.pdf', { title: 'Harbour', font: 'No Such Face 123', pages: [{ title: 'One', text: 'x' }] });");

        Assert.Contains(plain.Logs, l => l.Contains("plain 1"));                // no cover, no contents, no close
        Assert.Contains(pitch.Logs, l => l.Contains("pitch 3"));                // cover, the page, "Thank you."
        Assert.Contains(longBook.Logs, l => l.Contains("long 12"));             // cover, contents, ten scenes
        Assert.Contains(missing.Logs, l => l.Contains("'No Such Face 123' is not installed"));
        Assert.Contains("is a colour", Run("Stage.book('e.pdf', { title: 'H', accent: 'rust', pages: [{ title: 'One' }] });").Error);
    }

    /// <summary>
    /// The book is the client's, and a sheet of Qloo's pictures is other people's posters and logos. Known by the script
    /// that drew it, never by its name: an infographic's own <c>research_spend.png</c> goes in.
    /// </summary>
    [Fact]
    public void TestAResearchSheetIsRefusedAndAnythingElseIsNot()
    {
        foreach (var name in new[] { "look.png", "01_field_a.png", "research_spend.png" })
            File.Copy(Path.Combine(root, "artifacts", "frame.png"), Path.Combine(root, "artifacts", name));
        Record();
        DrawingExecutionResult Book(string image) => new JsDrawingEngine { ProjectRoot = root, Events = new RunEventLog(root) }
            .Execute($"Stage.book('process.pdf', {{ title: 'X', pages: [{{ image: '{image}' }}] }});\nconst c = createCanvas(8, 8); c;", 8, 8, null, "png", 90);

        var refused = Book("artifacts/look.png");

        Assert.False(refused.Success);
        Assert.Contains("research sheet", refused.Error);
        Assert.Contains("01_field_a.png", refused.Error);
        Assert.True(Book("artifacts/01_field_a.png").Success);
        Assert.True(Book("artifacts/research_spend.png").Success);
    }

    /// <summary>Any render of a script that took a Qloo picture's pixels is research, whatever it is called, and nothing else is.</summary>
    [Fact]
    public void TestTheRecordMarksEveryRenderOfAScriptThatTookQloosPictures()
    {
        Record();
        var log = new RunEventLog(root);

        Assert.True(log.IsResearch("artifacts/look.png"));
        Assert.True(log.IsResearch(@"artifacts\look.png"));
        Assert.False(log.IsResearch("artifacts/01_field_a.png"));
        Assert.False(log.IsResearch("artifacts/01_research_b.webp"));      // a name is not evidence
        Assert.False(log.IsResearch("artifacts/research_spend.png"));
    }

    /// <summary>A run that looked at Qloo's posters on a sheet (x1), then drew the board, which has none (x2).</summary>
    private void Record()
    {
        Directory.CreateDirectory(Path.Combine(root, "events"));
        File.WriteAllLines(Path.Combine(root, "events", "server.jsonl"),
        [
            """{"type":"taste.pictures","execution":"x1","first":"Suspiria"}""",
            """{"type":"render","execution":"x1","artifact":"artifacts/look.png","format":"png"}""",
            """{"type":"render","execution":"x2","artifact":"artifacts/01_field_a.png","format":"png"}""",
        ]);
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
