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
/// Token Factory is a fake handler and Gemini a fake <c>GenerateInline</c>. The PDF tests build their PDFs with Skia
/// and need <c>mutool</c> (in <c>bin/</c> here, on the PATH in the container); the OCR test needs Tesseract too. Each
/// returns early, passing, where its tool is missing, and says so in the log.
/// </remarks>
public class NemotronDocumentProcessorTests : TestsRuntime, IDisposable
{
    #region Fields
    private const string Key = "test-nebius-key";
    private const string Base = "https://tokenfactory.example/v1";
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
        new(gemini, nebius, new DocumentBudget(10), root, new DocumentPages(Path.Combine(root, "pages")),
            new HttpClient(handler));

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
    public async Task TestWhenTokenFactoryFailsGeminiAnswersAndSaysWhy(HttpStatusCode status)
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

    [Fact]
    public async Task TestWithoutTokenFactoryGeminiIsTheFirstChoice()
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
    private sealed class Reader(string? gemini, string? nebius, DocumentBudget budget, string root, DocumentPages pages, HttpClient http)
        : NemotronDocumentProcessor(gemini, nebius, Base, budget, root, pages, http: http)
    {
        public int InlineCalls { get; private set; }

        protected override Task<Generation> GenerateInline(
            byte[] bytes, string mime, string query, string model, string? schema, CancellationToken ct)
        {
            InlineCalls += 1;
            return Task.FromResult(new Generation("gemini answer", 50, "STOP") { Model = model });
        }
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
