namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using global::Polson.Tests;
using Polson.ExtendedMind.DocumentProcessing;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>Documents.ask</c> on Nemotron: the page text it is sent, the evidence it is checked against, and the fall back
/// to Gemini.
/// </summary>
/// <remarks>
/// The Nemotron endpoints are fake handlers and Gemini a fake <c>GenerateInline</c>. The PDF tests build their PDFs with Skia
/// and need <c>mutool</c> (in <c>bin/</c> here, on the PATH in the container); the OCR test needs Tesseract too. Each
/// returns early, passing, where its tool is missing, and says so in the log.
/// </remarks>
public class NemotronDocumentProcessorTests : TestsRuntime, IDisposable
{
    #region Fields
    private const string Key = "test-nebius-key";
    private const string Base = "https://tokenfactory.example/v1";
    private const string NimBase = "https://nim.example/v1";
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-nemodoc-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public NemotronDocumentProcessorTests() => Directory.CreateDirectory(Path.Combine(root, "documents"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Reader Make(Handler handler, string? gemini = "gemini-key", string? nebius = Key) =>
        MakeWith(handler, nebius is null ? [] : [Nebius(nebius)], gemini);

    private Reader MakeWith(HttpMessageHandler handler, NemotronEndpoint[] nemotron, string? gemini = "gemini-key") =>
        new(gemini, nemotron, new DocumentBudget(10), root, new DocumentPages(Path.Combine(root, "pages")),
            new HttpClient(handler));

    private static NemotronEndpoint Nebius(string key = Key) => new(NemotronEndpoint.Nebius, Base, key, NemotronEndpoint.NebiusModel);

    private static NemotronEndpoint Nim(string key = Key) => new(NemotronEndpoint.Nim, NimBase, key, NemotronEndpoint.NimModel);

    private static string Reply(string answer, params (int Page, string Quote)[] evidence) =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["answer"] = answer,
                        ["evidence"] = new JsonArray([.. evidence.Select(e => (JsonNode)new JsonObject { ["page"] = e.Page, ["quote"] = e.Quote })]),
                    }.ToJsonString(),
                },
                ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject { ["total_tokens"] = 900 },
        }.ToJsonString();

    /// <summary>A PDF with one page per string; a null page is drawn as a picture of its text, as a scan is.</summary>
    private string Pdf(string name, params string?[] pages)
    {
        var path = Path.Combine(root, "documents", name);
        using (var stream = File.Create(path))
        using (var document = SKDocument.CreatePdf(stream))
        {
            foreach (var text in pages)
            {
                var canvas = document.BeginPage(612, 792);
                using var font = new SKFont(SKTypeface.Default, 28);
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                if (text is not null)
                {
                    canvas.DrawText(text, 72, 144, font, paint);
                }
                else
                {
                    // A scan: the words as pixels, so there is no text layer to find.
                    using var bitmap = new SKBitmap(1200, 300);
                    using (var c = new SKCanvas(bitmap))
                    {
                        c.Clear(SKColors.White);
                        using var big = new SKFont(SKTypeface.Default, 64);
                        c.DrawText("Total revenue 61,234", 40, 160, big, paint);
                    }
                    canvas.DrawBitmap(bitmap, SKRect.Create(36, 72, 540, 135));
                }
                document.EndPage();
            }
            document.Close();
        }
        return "documents/" + name;
    }
    #endregion

    #region Tests
    [Fact]
    public async Task TestATextDocumentIsSentAsMarkedPagesAndItsEvidenceIsChecked()
    {
        File.WriteAllText(Path.Combine(root, "documents", "notes.txt"), "Revenue was 215,938 in millions.");
        var handler = new Handler(HttpStatusCode.OK, Reply("215,938", (1, "Revenue was 215,938"), (1, "102,714")));
        using var reader = Make(handler);

        var answer = await reader.Ask("documents/notes.txt", "What was revenue?");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal("215,938", answer.Text);
        Assert.Equal(VideoReader.DefaultVideoModel, answer.Provenance!.Model);
        Assert.Equal("text", answer.Provenance.Route);
        Assert.Null(answer.Provenance.Fallback);
        Assert.Equal([true, false], answer.Evidence.Select(e => e.Found));
        Assert.Equal(["p.1: 102,714"], answer.Unverified);

        var body = JsonNode.Parse(handler.Bodies.Single())!;
        var text = string.Join("\n", body["messages"]![0]!["content"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()));
        Assert.Contains("[Page 1]\nRevenue was 215,938", text);
        Assert.Contains("QUESTION-BEGIN\nWhat was revenue?\nQUESTION-END", text);
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.NotNull(body["response_format"]!["json_schema"]!["schema"]!["properties"]!["evidence"]);
    }

    [Fact]
    public void TestAFigureStitchedFromATableRowIsFoundWhereItsCellsAreOnTheirOwnLines()
    {
        // The 10-K's text layer puts each cell on its own line; the model quotes the row.
        var paged = new PagedText { Pages = ["Graphics\n$\n22,459\n$\n14,304"] };

        Assert.True(NemotronDocumentProcessor.Holds(paged, 1, "Graphics $ 22,459"));
        Assert.False(NemotronDocumentProcessor.Holds(paged, 1, "22,495"));
        Assert.False(NemotronDocumentProcessor.Holds(paged, 2, "22,459"));      // no page 2
        Assert.True(NemotronDocumentProcessor.Holds(paged, 1, "graphics"));     // words, when there is no figure
    }

    [Fact]
    public async Task TestAPdfIsReadAsPageTextByMutool()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        var path = Pdf("report.pdf", "Contents", "Revenue 215,938");
        var handler = new Handler(HttpStatusCode.OK, Reply("215,938", (2, "Revenue 215,938")));
        using var reader = Make(handler);

        var answer = await reader.Ask(path, "What was revenue?");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal(2, answer.Provenance!.Pages);
        Assert.True(Assert.Single(answer.Evidence).Found);
        Assert.Contains("[Page 2]\nRevenue 215,938", handler.Bodies.Single().Replace("\\n", "\n"));
    }

    [Fact]
    public async Task TestAScannedPageIsReadByOcr()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable || pages.Tesseract is null) { Info("Skipped: no mutool or tesseract."); return; }

        var path = Pdf("scan.pdf", [null]);
        var read = await pages.Read(File.ReadAllBytes(Path.Combine(root, path)), "scan", NemotronDocumentProcessor.MaxOcrPages);

        Assert.Equal([1], read.OcrPages);
        Assert.Contains("61,234", read.Pages[0]);
    }

    [Fact]
    public async Task TestTooManyScannedPagesGoToGeminiWhole()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        // Blank pages read as scans; one more than OCR may do inside a script.
        var path = Pdf("scans.pdf", [.. Enumerable.Repeat("", NemotronDocumentProcessor.MaxOcrPages + 1)]);
        var handler = new Handler(HttpStatusCode.OK, Reply("unused"));
        using var reader = Make(handler);

        var answer = await reader.Ask(path, "What does it say?");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal(DocumentProcessor.DefaultModel, answer.Provenance!.Model);
        Assert.Contains("scans", answer.Provenance.Fallback);
        Assert.Empty(handler.Bodies);
        Assert.Equal(1, reader.InlineCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TestWhenNemotronFailsGeminiAnswersAndSaysWhy(HttpStatusCode status)
    {
        File.WriteAllText(Path.Combine(root, "documents", "notes.txt"), "Revenue was 215,938.");
        using var reader = Make(new Handler(status, """{"detail":"no"}"""));

        var answer = await reader.Ask("documents/notes.txt", "What was revenue?");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal("gemini answer", answer.Text);
        Assert.Equal(DocumentProcessor.DefaultModel, answer.Provenance!.Model);
        Assert.Equal("inline", answer.Provenance.Route);
        Assert.Contains(((int)status).ToString(), answer.Provenance.Fallback);
        Assert.Empty(answer.Evidence);
    }

    [Fact]
    public async Task TestWithNoGeminiToFallBackOnTheNemotronFailureIsTheAnswer()
    {
        File.WriteAllText(Path.Combine(root, "documents", "notes.txt"), "Revenue was 215,938.");
        using var reader = Make(new Handler(HttpStatusCode.TooManyRequests, """{"detail":"slow down"}"""), gemini: null);

        var answer = await reader.Ask("documents/notes.txt", "What was revenue?");

        Assert.False(answer.Success);
        Assert.Equal("RateLimited", answer.FailureName);
        Assert.Equal(0, reader.InlineCalls);
    }

    [Fact]
    public async Task TestWhenThePreferredEndpointFailsTheOtherAnswersWithItsEvidenceChecked()
    {
        File.WriteAllText(Path.Combine(root, "documents", "notes.txt"), "Revenue was 215,938 in millions.");
        var handler = new Routed
        {
            ["nim.example"] = (HttpStatusCode.InternalServerError, """{"detail":"down"}"""),
            ["tokenfactory.example"] = (HttpStatusCode.OK, Reply("215,938", (1, "Revenue was 215,938"))),
        };
        using var reader = MakeWith(handler, [Nim(), Nebius()]);

        var answer = await reader.Ask("documents/notes.txt", "What was revenue?");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal("215,938", answer.Text);
        Assert.Equal(VideoReader.DefaultVideoModel, answer.Provenance!.Model);   // the model's name, whoever served it
        Assert.Equal(NemotronEndpoint.Nebius, answer.Provenance.Endpoint);
        Assert.Null(answer.Provenance.Fallback);                                   // still Nemotron: nothing fell back
        Assert.True(Assert.Single(answer.Evidence).Found);
        Assert.Equal(0, reader.InlineCalls);
        Assert.Equal(["nim.example", "tokenfactory.example"], handler.Hosts);
        Assert.Equal([NemotronEndpoint.NimModel, NemotronEndpoint.NebiusModel], handler.Models);
    }

    [Fact]
    public async Task TestWhenEveryEndpointFailsGeminiAnswersAndTheFallbackNamesEach()
    {
        File.WriteAllText(Path.Combine(root, "documents", "notes.txt"), "Revenue was 215,938.");
        var handler = new Routed
        {
            ["nim.example"] = (HttpStatusCode.InternalServerError, """{"detail":"down"}"""),
            ["tokenfactory.example"] = (HttpStatusCode.TooManyRequests, """{"detail":"slow down"}"""),
        };
        using var reader = MakeWith(handler, [Nim(), Nebius()]);

        var answer = await reader.Ask("documents/notes.txt", "What was revenue?");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal("gemini answer", answer.Text);
        Assert.Null(answer.Provenance!.Endpoint);
        Assert.Contains("nim answered 500", answer.Provenance.Fallback);
        Assert.Contains("nebius answered 429", answer.Provenance.Fallback);
        Assert.Equal(1, reader.InlineCalls);
    }

    [Fact]
    public async Task TestAnImageGoesToGeminiWholeAndThatIsNotAFallback()
    {
        File.WriteAllBytes(Path.Combine(root, "documents", "logo.png"), [137, 80, 78, 71]);
        var handler = new Handler(HttpStatusCode.OK, Reply("unused"));
        using var reader = Make(handler);

        var answer = await reader.Ask("documents/logo.png", "What colour is it?");

        Assert.True(answer.Success, answer.Error);
        Assert.Null(answer.Provenance!.Fallback);
        Assert.Equal(DocumentProcessor.DefaultModel, answer.Provenance.Model);
        Assert.Empty(handler.Bodies);
    }

    /// <summary>
    /// Three pages: prose, a coloured bar chart, and a table with grey row shading. Only the chart page is visual.
    /// </summary>
    /// <remarks>The calibration it pins: a 10-K's statements score high on ink in 3–7 colours; charts and photos in 19 and up.</remarks>
    private string ChartPdf(string name)
    {
        var path = Path.Combine(root, "documents", name);
        using (var stream = File.Create(path))
        using (var document = SKDocument.CreatePdf(stream))
        {
            using var font = new SKFont(SKTypeface.Default, 14);
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            var prose = document.BeginPage(612, 792);
            for (var line = 0; line < 30; line++) prose.DrawText("Revenue grew in every quarter of the year.", 72, 90 + line * 20, font, ink);
            document.EndPage();

            var chart = document.BeginPage(612, 792);
            chart.DrawText("Adjusted EBITDA margin", 72, 90, font, ink);
            SKColor[] colours = [new(0x6e, 0x5b, 0xf6), new(0x9b, 0x8c, 0xff), new(0xd8, 0xff, 0x3c), new(0xf0, 0x6a, 0x3a),
                new(0x2a, 0x9d, 0x8f), new(0xe9, 0xc4, 0x6a)];
            for (var i = 0; i < colours.Length; i++)
            {
                using var bar = new SKPaint { Color = colours[i], IsAntialias = true };
                chart.DrawRoundRect(new SKRoundRect(SKRect.Create(80 + i * 85, 600 - (i + 2) * 55, 60, (i + 2) * 55), 6), bar);
            }
            document.EndPage();

            var table = document.BeginPage(612, 792);
            using var shade = new SKPaint { Color = new SKColor(0xee, 0xee, 0xee) };
            using var rule = new SKPaint { Color = new SKColor(0x99, 0x99, 0x99), StrokeWidth = 1 };
            for (var row = 0; row < 24; row++)
            {
                if (row % 2 == 0) table.DrawRect(SKRect.Create(60, 80 + row * 26, 490, 26), shade);
                table.DrawLine(60, 106 + row * 26, 550, 106 + row * 26, rule);
                table.DrawText($"Line item {row}   1,{row:000}   2,{row:000}", 70, 98 + row * 26, font, ink);
            }
            document.EndPage();
            document.Close();
        }
        return "documents/" + name;
    }

    private static string ReplyWith(string answer, params (int Page, string Quote, string From)[] evidence) =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["answer"] = answer,
                        ["evidence"] = new JsonArray([.. evidence.Select(e => (JsonNode)new JsonObject
                            { ["page"] = e.Page, ["quote"] = e.Quote, ["from"] = e.From })]),
                    }.ToJsonString(),
                },
                ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject { ["total_tokens"] = 900 },
        }.ToJsonString();

    [Fact]
    public async Task TestOnlyTheChartPageIsKeptAsAnImage()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        var path = ChartPdf("letter.pdf");
        var read = await pages.Read(File.ReadAllBytes(Path.Combine(root, path)), "letter", NemotronDocumentProcessor.MaxOcrPages);

        Assert.Equal([2], read.VisualPages!);
        Assert.NotNull(pages.ImageOf("letter", 2));
        Assert.Null(pages.ImageOf("letter", 3));
    }

    [Fact]
    public async Task TestTheChartPageGoesAsAnImageAndImageEvidenceIsKeptApart()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        var path = ChartPdf("letter.pdf");
        var handler = new Handler(HttpStatusCode.OK, ReplyWith("50% in the latest quarter",
            (2, "50%", "image"),                  // read off the chart: not in the page's text
            (1, "Revenue grew", "text"),          // in the text, and found
            (3, "9,999", "text"),                 // claimed from text, not there: a misreading
            (1, "Revenue grew", "image"),         // "image" for a page that was not sent as one: treated as text
            (2, "Adjusted EBITDA margin", "image"))); // read from the image, its words on the page: found, still unchecked
        using var reader = Make(handler);

        var answer = await reader.Ask(path, "What was the margin?");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal("text+images", answer.Provenance!.Route);
        Assert.Equal([2], answer.Provenance.ImagePages);
        Assert.Equal(["p.2: 50%", "p.2: Adjusted EBITDA margin"], answer.FromImages);
        Assert.True(answer.Evidence[4].Found);   // found on the page, and listed anyway: what it belongs to is not checked
        Assert.Equal(["p.3: 9,999"], answer.Unverified);
        Assert.Equal("text", answer.Evidence[3].From);

        var body = JsonNode.Parse(handler.Bodies.Single())!;
        var parts = body["messages"]![0]!["content"]!.AsArray();
        Assert.Single(parts, p => p!["type"]!.GetValue<string>() == "image_url");
        Assert.Contains(parts, p => p!["text"]?.GetValue<string>() == "[Page 2 image]");
        Assert.Contains("Some pages are also given as images", parts[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task TestImagesFalseSendsTextOnly()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        var path = ChartPdf("letter.pdf");
        var handler = new Handler(HttpStatusCode.OK, ReplyWith("text only", (1, "Revenue grew", "text")));
        using var reader = Make(handler);

        var answer = await reader.Ask(path, "What was the margin?", new DocumentOptions { Images = false });

        Assert.True(answer.Success, answer.Error);
        Assert.Equal("text", answer.Provenance!.Route);
        Assert.Empty(answer.Provenance.ImagePages);
        Assert.DoesNotContain("image_url", handler.Bodies.Single());
    }

    /// <summary>Only the chosen pages are sent, each under its own number, and evidence counts only on a page that was sent.</summary>
    [Fact]
    public async Task TestChosenPagesAloneAreSentUnderTheirOwnNumbers()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        // No 7 in the figures: this test font's text layer extracts it as "/".
        var path = Pdf("s1.pdf", "Contents", "Revenue 215,938", "Costs 102,614", "Risk factors");
        var handler = new Handler(HttpStatusCode.OK, Reply("215,938", (2, "Revenue 215,938"), (4, "Risk factors")));
        using var reader = Make(handler);

        var answer = await reader.Ask(path, "What was revenue?", new DocumentOptions { Pages = "2-3" });

        Assert.True(answer.Success, answer.Error);
        Assert.Equal(2, answer.Provenance!.Pages);
        // Page 4 does say "Risk factors", but it was never sent: a quote from it rests on nothing.
        Assert.Equal([true, false], answer.Evidence.Select(e => e.Found));
        var sent = handler.Bodies.Single().Replace("\\n", "\n");
        Assert.Contains("[Page 2]\nRevenue 215,938", sent);
        Assert.Contains("[Page 3]\nCosts 102,614", sent);
        Assert.DoesNotContain("[Page 1]", sent);
        Assert.DoesNotContain("[Page 4]", sent);
        Assert.Contains("Only some of the document", sent);   // the body's JSON escapes the apostrophe that follows
    }

    /// <summary>
    /// Scans outside the chosen pages are not OCR'd, so they cannot send the read to Gemini: the S-1's 19 picture pages
    /// once sent a question of its income statement there whole.
    /// </summary>
    [Fact]
    public async Task TestScansOutsideTheChosenPagesDoNotSendTheReadElsewhere()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        var last = NemotronDocumentProcessor.MaxOcrPages + 2;
        var path = Pdf("mixed.pdf", [.. Enumerable.Repeat("", last - 1), "Revenue 215,938"]);
        var handler = new Handler(HttpStatusCode.OK, Reply("215,938", (last, "Revenue 215,938")));
        using var reader = Make(handler);

        var chosen = await reader.Ask(path, "What was revenue?", new DocumentOptions { Pages = $"{last}" });
        var whole = await reader.Ask(path, "What was revenue?");

        Assert.True(chosen.Success, chosen.Error);
        Assert.Null(chosen.Provenance!.Fallback);
        Assert.True(Assert.Single(chosen.Evidence).Found);
        Assert.Single(handler.Bodies);
        Assert.Contains("scans", whole.Provenance!.Fallback);   // every page still needs every scan read
        Assert.Equal(1, reader.InlineCalls);
    }

    /// <summary>
    /// <c>find</c> names the pages a phrase is on, from the page text alone: nothing is sent, nothing spent, no scan OCR'd,
    /// and a scan not yet read is listed as unsearched rather than passed over in silence.
    /// </summary>
    [Fact]
    public async Task TestFindNamesThePagesAPhraseIsOnAndSpendsNothing()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        // Page 1 is long enough to count as text: under 16 characters a page is taken for a scan.
        var path = Pdf("s1.pdf", "Table of Contents, page one", "Consolidated Statements of Operations", null,
            "Revenue in the Statements of Operations");
        var handler = new Handler(HttpStatusCode.OK, Reply("unused"));
        var budget = new DocumentBudget(10);
        using var reader = new Reader(null, [Nebius()], budget, root, pages, new HttpClient(handler));

        var found = await reader.Find(path, "statements  of\noperations");   // any case, any break between the words
        var none = await reader.Find(path, "use of proceeds");
        var blank = await reader.Find(path, "  ");

        Assert.True(found.Success, found.Error);
        Assert.Equal([2, 4], found.Hits.Select(h => h.Page));
        Assert.Contains("Statements of Operations", found.Hits[0].Context);
        Assert.Equal(4, found.Pages);
        Assert.Equal([3], found.Unsearched);       // the scan, not yet OCR'd
        Assert.True(none.Success);
        Assert.Empty(none.Hits);
        Assert.Equal("NoQuery", blank.FailureName);
        Assert.Empty(handler.Bodies);
        Assert.Equal(0, reader.InlineCalls);
        Assert.Equal(0, budget.Spent);
    }

    /// <summary>A page past the last is refused once the document is open, before anything is sent, and costs nothing.</summary>
    [Fact]
    public async Task TestAPagePastTheLastIsRefusedForNothing()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        var path = Pdf("short.pdf", "One", "Two");
        var handler = new Handler(HttpStatusCode.OK, Reply("unused"));
        var budget = new DocumentBudget(10);
        using var reader = new Reader("gemini-key", [Nebius()], budget, root, pages, new HttpClient(handler));

        var answer = await reader.Ask(path, "What does it say?", new DocumentOptions { Pages = "2-5" });

        Assert.Equal("InvalidRequest", answer.FailureName);
        Assert.Contains("the document has 2", answer.Error);
        Assert.Empty(handler.Bodies);
        Assert.Equal(0, reader.InlineCalls);   // not a fallback: Gemini would read pages that do not exist either
        Assert.Equal(0, budget.Spent);
    }

    /// <summary>When Nemotron cannot answer, Gemini reads the same chosen pages, as marked text, never the whole file.</summary>
    [Fact]
    public async Task TestWhenNemotronFailsGeminiReadsOnlyTheChosenPages()
    {
        var pages = new DocumentPages(Path.Combine(root, "pages"));
        if (!pages.IsAvailable) { Info("Skipped: no mutool."); return; }

        var path = Pdf("s1.pdf", "Contents", "Revenue 215,938", "Costs 102,614");
        using var reader = Make(new Handler(HttpStatusCode.InternalServerError, """{"detail":"no"}"""));

        var answer = await reader.Ask(path, "What was revenue?", new DocumentOptions { Pages = "2" });

        Assert.True(answer.Success, answer.Error);
        Assert.NotNull(answer.Provenance!.Fallback);
        Assert.Equal(1, answer.Provenance.Pages);
        Assert.Equal("text/plain", reader.LastInline.Mime);
        Assert.Contains("[Page 2]\nRevenue 215,938", reader.LastInline.Text);
        Assert.DoesNotContain("Contents", reader.LastInline.Text);
    }

    [Fact]
    public async Task TestWithoutANemotronEndpointGeminiIsTheFirstChoice()
    {
        File.WriteAllText(Path.Combine(root, "documents", "notes.txt"), "Revenue was 215,938.");
        using var reader = Make(new Handler(HttpStatusCode.OK, Reply("unused")), nebius: null);

        Assert.True(reader.IsAvailable);
        Assert.Equal(DocumentProcessor.DefaultModel, reader.Model);
        var answer = await reader.Ask("documents/notes.txt", "What was revenue?");
        Assert.Null(answer.Provenance!.Fallback);
    }
    #endregion

    #region Types
    /// <summary>The reader with Gemini faked: it answers "gemini answer" and counts the calls.</summary>
    private sealed class Reader(string? gemini, NemotronEndpoint[] nemotron, DocumentBudget budget, string root, DocumentPages pages, HttpClient http)
        : NemotronDocumentProcessor(gemini, nemotron, budget, root, pages, http: http)
    {
        public int InlineCalls { get; private set; }

        /// <summary>What Gemini was last sent, as its type and its text.</summary>
        public (string Mime, string Text) LastInline { get; private set; }

        protected override Task<Generation> GenerateInline(
            byte[] bytes, string mime, string query, string model, string? schema, CancellationToken ct)
        {
            InlineCalls += 1;
            LastInline = (mime, System.Text.Encoding.UTF8.GetString(bytes));
            return Task.FromResult(new Generation("gemini answer", 50, "STOP") { Model = model });
        }
    }

    /// <summary>Answers each host with its own canned response, and keeps the hosts and models it was sent, in order.</summary>
    private sealed class Routed : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) this[string host]
        {
            get => routes[host];
            set => routes[host] = value;
        }

        public List<string> Hosts { get; } = [];

        public List<string> Models { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            Models.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["model"]!.GetValue<string>());
            var (status, body) = routes[host];
            return new HttpResponseMessage(status) { Content = new System.Net.Http.StringContent(body) };
        }

        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> routes = [];
    }

    /// <summary>Answers every request with one canned response, and keeps every body it was sent.</summary>
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new System.Net.Http.StringContent(body) };
        }
    }
    #endregion
}
