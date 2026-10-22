namespace Polson.Tests.MCPServer;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using global::Polson.Animation;
using global::Polson.ExtendedMind.DocumentProcessing;
using global::Polson.MCPServer;
using global::Polson.Tests;
using Xunit;

/// <summary>
/// <c>Verify.film</c>: a watcher's transcription held against what the composition set, each text's source looked up, and
/// strays kept pending until their frames have been looked at. The watcher is a fake service; the film a real encode.
/// </summary>
public class VerifyTests : TestsRuntime, IDisposable
{
    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-verify-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public VerifyTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        File.WriteAllText(Path.Combine(root, "brief.md"),
            "# Brief\n<!-- BRIEF-BEGIN -->\nFilm: Be Cool. Tagline: Everyone wants a piece of the action.\n<!-- BRIEF-END -->\n");
        // A document as Documents.ask leaves it: the file, and its page text cached under the file's hash.
        var pdf = Path.Combine(root, "documents", "letter.pdf");
        File.WriteAllBytes(pdf, [37, 80, 68, 70, 1, 2, 3]);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdf)))[..32];
        var pages = Directory.CreateDirectory(Path.Combine(root, ".polson", "documents", "pages", hash));
        File.WriteAllText(Path.Combine(pages.FullName, "pages.json"),
            new JsonObject { ["Pages"] = new JsonArray("Cover", "Revenues 105.1 582.3 454% in USD millions; returned $974 million") }.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static FilmReadBack.Shown Shown(string text, double from, double to) => new(text, from, to, to - from, [[from, to]]);

    private static FilmReadBack.Reading Read(string text, double at) => new(text, at, at + 1);

    [Fact]
    public void TestATextReadAsSetAtItsTimeIsSeenAndOneNotReadIsMissed()
    {
        var shown = new[] { Shown("BE COOL", 0, 3), Shown("$582.3M", 4, 6), Shown("Q2 2026", 7, 7.4) };
        var outcome = FilmReadBack.Match(shown, [Read("Be Cool", 0.5), Read("$582.3M", 4.2)]);

        Assert.Equal(["Be Cool", "$582.3M", null], outcome.Seen.Select(r => r?.Text));
        Assert.Empty(outcome.Strays);
    }

    /// <summary>The report film's case: the line was set with $974M and read with $744M. Not a match — a stray to look at.</summary>
    [Fact]
    public void TestTheSameLineWithADifferentFigureIsAStrayNotAMatch()
    {
        var shown = new[] { Shown("Returned $974M to shareholders", 10, 14) };
        var outcome = FilmReadBack.Match(shown, [Read("Returned $744M to shareholders", 11), Read("GHOST", 12)]);

        Assert.Null(outcome.Seen[0]);
        Assert.Equal(2, outcome.Strays.Length);
        Assert.Equal(("different figure", "Returned $974M to shareholders"), (outcome.Strays[0].Kind, outcome.Strays[0].Near));
        Assert.Equal(("never set", null), (outcome.Strays[1].Kind, outcome.Strays[1].Near));
    }

    [Fact]
    public void TestALineReadFarFromItsTimeIsNotItsReadingAndLinesReadApartMakeTheWhole()
    {
        var outcome = FilmReadBack.Match([Shown("Fiscal 2026\nRevenue $215.9B", 20, 25)],
            [Read("Fiscal 2026", 20.5), Read("Revenue $215.9B", 21.5)]);
        Assert.Equal("Fiscal 2026\nRevenue $215.9B", outcome.Seen[0]?.Text);

        var late = FilmReadBack.Match([Shown("TITLE", 0, 2)], [Read("TITLE", 9)]);
        Assert.Null(late.Seen[0]);
        Assert.Single(late.Strays);
    }

    [Theory]
    [InlineData("$40.4 billion (282 million)", new[] { "40.4", "282" })]
    [InlineData("102,718 units, up 4.5%.", new[] { "102718", "4.5" })]
    public void TestFiguresAreReadWithoutTheirCommas(string text, string[] figures) =>
        Assert.Equal(figures, FilmReadBack.Figures(text));

    [Fact]
    public void TestEachTextsSourceIsLookedUpNotAsked()
    {
        var session = new SessionContext();
        session.Taste.Add(id => new global::Polson.ExtendedMind.Taste.TasteResult
        {
            Id = id, Op = "profile", Label = "audience A", Ok = true,
            Entities = [new Dictionary<string, object?> { ["id"] = "e1", ["name"] = "Riverside Mukolitta" }],
        });
        var sources = new FilmSources(root, session);
        var computed = new Dictionary<string, string> { ["+46%"] = "582.3 / 399.0 − 1" };

        Assert.Equal(("brief", null), sources.Classify("Everyone wants a piece of the action", computed));
        Assert.Equal(("document", "documents/letter.pdf p. 2"), sources.Classify("$582.3M", computed));
        Assert.Equal(("document", "documents/letter.pdf p. 2"), sources.Classify("$974 million returned", computed));
        Assert.Equal(("computed", "582.3 / 399.0 − 1"), sources.Classify("+46%", computed));
        Assert.Equal(("qloo", "t1"), sources.Classify("Riverside Mukolitta", computed));
        Assert.Equal(("own words", null), sources.Classify("Coming soon", computed));
        Assert.Equal(("unsourced figure", null), sources.Classify("$183.4B profit", computed));
    }

    /// <summary>
    /// End to end on a real encode: one text read as set, one read with another figure, one reading of nothing set. The
    /// strays' frames are saved and they stay pending — the check does not pass — until settled after looking.
    /// </summary>
    [Fact]
    public async Task TestAFilmIsCheckedAndItsStraysWaitForTheirFrames()
    {
        if (!FFmpeg.Available || !SkiaSharp.SKFontManager.Default.GetFontFamilies().Contains("Arial"))
        {
            Info("Skipped: needs ffmpeg and Arial.");
            return;
        }
        Info("ffmpeg: {0}", FFmpeg.Executable!);
        var motion = new MotionToolkit(root);
        var comp = motion.Composition(new Hashtable { ["width"] = 320d, ["height"] = 180d, ["fps"] = 12d, ["duration"] = 3d });
        comp.Fill(new Hashtable { ["color"] = "#ffffff" });
        comp.Text(new Hashtable { ["text"] = "BE COOL", ["family"] = "Arial", ["size"] = 40d, ["origin"] = new[] { 160d, 50d } });
        comp.Text(new Hashtable { ["text"] = "Returned $974 million", ["family"] = "Arial", ["size"] = 20d, ["origin"] = new[] { 160d, 120d } });
        comp.SaveVideo("output.mp4");

        var watched = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["text"] = "BE COOL", ["from"] = 0, ["to"] = 3 },
                new JsonObject { ["text"] = "Returned $744 million", ["from"] = 1, ["to"] = 3 },
                new JsonObject { ["text"] = "GHOST", ["from"] = 2, ["to"] = 3 }),
        }.ToJsonString();
        using var video = new VideoReader([new NemotronEndpoint(NemotronEndpoint.Nebius, "https://tf.example/v1", "k", NemotronEndpoint.NebiusModel)],
            new DocumentBudget(5), root, http: new HttpClient(new Answer(watched)));
        var recorded = new List<string>();
        var verify = new VerifyToolkit(video, root, null, (type, _) => recorded.Add(type));

        var result = await verify.Film(comp, "output.mp4");

        Assert.True((bool)result["ok"]!, result.GetValueOrDefault("error") as string);
        var items = ((object?[])result["items"]!).Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(["seen", "missed"], items.Select(i => (string)i["status"]!));
        Assert.Equal(["brief", "document"], items.Select(i => (string)i["source"]!));
        var strays = ((object?[])result["strays"]!).Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(["different figure", "never set"], strays.Select(s => (string)s["kind"]!));
        Assert.All(strays, s => Assert.True(File.Exists(Path.Combine(root, (string)s["frame"]!)), $"no frame: {s["frameError"]}"));
        Assert.False((bool)result["passed"]!);
        Assert.Contains("Verify.stray(n, 'misread' | 'real')", (string)result["summary"]!);

        verify.Stray(1, "misread", "the frame shows $974 million");
        var settled = verify.Stray(2, "real");
        Assert.False((bool)settled["passed"]!);                            // a real fault in the film still fails it
        Assert.Contains("1 confirmed fault", (string)settled["summary"]!);
        Assert.Equal(["film.verify", "film.stray", "film.stray"], recorded);
        Assert.Throws<ArgumentException>(() => verify.Stray(9, "misread"));
        Assert.Throws<ArgumentException>(() => verify.Stray(1, "maybe"));
    }
    #endregion

    #region Types
    /// <summary>The watcher: answers every request with one transcription, as a chat completion.</summary>
    private sealed class Answer(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop" }),
                    ["usage"] = new JsonObject { ["total_tokens"] = 100 },
                }.ToJsonString()),
            });
    }
    #endregion
}
