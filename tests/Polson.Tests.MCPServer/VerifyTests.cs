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
        Assert.Equal(("unsourced figure", "no source for 183.4"), sources.Classify("$183.4B profit", computed));
    }

    /// <summary>A sentence is sourced figure by figure: a document's figure and a computed one in one line, from two pages.</summary>
    [Fact]
    public void TestASentenceIsSourcedFigureByFigure()
    {
        var sources = new FilmSources(root, null);
        var computed = new Dictionary<string, string> { ["Top 4.4%"] = "22 / 500" };

        Assert.Equal(("computed", "documents/letter.pdf p. 2; 4.4 = 22 / 500"),
            sources.Classify("Returned $974 million, the top 4.4%", computed));
        Assert.Equal(("unsourced figure", "no source for 7"), sources.Classify("$974 million in 7 days", computed));
        Assert.Equal(("scale", null), sources.Classify("1,000%", computed, new HashSet<string> { "1,000%" }));
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
        var session = new SessionContext();
        var verify = new VerifyToolkit(video, root, session, (type, _) => recorded.Add(type));

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

        Assert.Throws<InvalidOperationException>(() => verify.Stray(1, "misread"));   // not in the script that ran it
        var later = new VerifyToolkit(video, root, session, (type, _) => recorded.Add(type));
        later.Stray(1, "misread", "the frame shows $974 million");
        var settled = later.Stray(2, "real");
        Assert.False((bool)settled["passed"]!);                            // a real fault in the film still fails it
        Assert.Contains("1 confirmed fault", (string)settled["summary"]!);
        Assert.Equal(["film.verify", "check", "film.stray", "check", "film.stray", "check"], recorded);
        Assert.Throws<ArgumentException>(() => later.Stray(9, "misread"));
        Assert.Throws<ArgumentException>(() => later.Stray(1, "maybe"));
    }
    /// <summary>
    /// A counting number is checked by the values it holds. A reading in its format and range while it counts is no stray,
    /// even a value no frame drew exactly; one outside its range, or after it stopped, is.
    /// </summary>
    [Fact]
    public async Task TestACountingNumberIsReadByWhatItHolds()
    {
        if (!FFmpeg.Available || !SkiaSharp.SKFontManager.Default.GetFontFamilies().Contains("Arial"))
        {
            Info("Skipped: needs ffmpeg and Arial.");
            return;
        }
        var motion = new MotionToolkit(root);
        var comp = motion.Composition(new Hashtable { ["width"] = 320d, ["height"] = 180d, ["fps"] = 12d, ["duration"] = 4d });
        comp.Fill(new Hashtable { ["color"] = "#ffffff" });
        var count = motion.Nodes.Animated("real", new object[]
        {
            new Hashtable { ["time"] = 0d, ["value"] = 0d }, new Hashtable { ["time"] = 1d, ["value"] = 0d }, new Hashtable { ["time"] = 2.5, ["value"] = 974d },
        });
        var number = comp.Number(new Hashtable { ["value"] = count, ["family"] = "Arial", ["size"] = 40d, ["origin"] = new[] { 160d, 90d },
            ["format"] = new Hashtable { ["prefix"] = "$", ["suffix"] = "M" } });
        comp.SaveVideo("counting.mp4");

        var watched = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["text"] = "$0M", ["from"] = 0, ["to"] = 1 },
                new JsonObject { ["text"] = number.TextAt(1.75), ["from"] = 1.75, ["to"] = 1.8 },
                new JsonObject { ["text"] = "$380M", ["from"] = 1.5, ["to"] = 1.5 },
                new JsonObject { ["text"] = "$974M", ["from"] = 2.5, ["to"] = 4 },
                new JsonObject { ["text"] = "$999M", ["from"] = 3, ["to"] = 3.5 }),
        }.ToJsonString();
        using var video = new VideoReader([new NemotronEndpoint(NemotronEndpoint.Nebius, "https://tf.example/v1", "k", NemotronEndpoint.NebiusModel)],
            new DocumentBudget(5), root, http: new HttpClient(new Answer(watched)));
        var result = await new VerifyToolkit(video, root, null).Film(comp, "counting.mp4", new Dictionary<string, object?>
        {
            ["computed"] = new Dictionary<string, object?> { ["$0M"] = "the start", ["$974M"] = "974" },
        });

        Assert.True((bool)result["ok"]!, result.GetValueOrDefault("error") as string);
        var items = ((object?[])result["items"]!).Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(["$0M", "$974M"], items.Select(i => (string)i["text"]!));
        Assert.All(items, i => Assert.Equal("seen", i["status"]));
        var stray = Assert.Single(((object?[])result["strays"]!).Cast<Dictionary<string, object?>>());
        Assert.Equal("$999M", stray["read"]);
    }

    [Fact]
    public void TestAStillsTextsAreReadOnceEachAndAWrappedReadingCoversItsLines()
    {
        string[] texts = ["Qloo", "2,205.8%", "Growth 2016-19", "Fastest-growing companies", "in the Americas"];
        var (seen, _, strays) = ImageReadBack.Match(texts,
            ["QLOO", "2,205.8%", "Growth 2016–19", "Fastest-growing companies in the Americas"]);

        Assert.Equal(["QLOO", "2,205.8%", "Growth 2016–19", "Fastest-growing companies in the Americas", "Fastest-growing companies in the Americas"], seen.AsEnumerable());
        Assert.Empty(strays);
    }

    /// <summary>A figure read differently is a stray beside the line it resembles, alone or inside a run-together reading.</summary>
    [Fact]
    public void TestAStillsFigureReadDifferentlyIsAStrayBesideItsLine()
    {
        var alone = ImageReadBack.Match(["Grew 2,205.8%", "Rank 22"], ["Grew 2,206.8%", "Rank 22", "Statista"]);
        Assert.Equal([null, "Rank 22"], alone.Seen.AsEnumerable());
        Assert.Equal([("different figure", "Grew 2,205.8%"), ("never set", null)], alone.Strays.Select(s => (s.Kind, s.Near)));

        var together = ImageReadBack.Match(["Qloo", "Technology", "2,205.8%"], ["Qloo Technology 2,205.6%"]);
        Assert.Equal(["Qloo Technology 2,205.6%", "Qloo Technology 2,205.6%", null], together.Seen.AsEnumerable());
        Assert.Equal([("different figure", "2,205.8%")], together.Strays.Select(s => (s.Kind, s.Near)));
    }

    /// <summary>
    /// qloo_among_the_fastest_growing: the reader listed each bar's figure on its own and again inside a table row read
    /// across its columns. Every line in the row was drawn, so the row is explained, not a stray; a name read short is
    /// missed, with the reading closest to it.
    /// </summary>
    [Fact]
    public void TestATableRowReadAcrossItsColumnsIsNotAStray()
    {
        string[] texts = ["#18", "Fair Price Properties", "Property · US", "+2,587.2%", "#26", "ORORO Heated Apparel", "Fashion · US"];
        var (seen, closest, strays) = ImageReadBack.Match(texts,
            ["+2,587.2%", "Fair Price Properties", "#18 Fair Price Properties Property • US +2,587.2%",
             "#26 OROO Heated Apparel Fashion • US"]);

        Assert.Empty(strays);
        Assert.Null(seen[5]);
        Assert.Equal("#26 OROO Heated Apparel Fashion • US", closest[5]);
    }

    /// <summary>The equation probe's defect: a split-off fragment, <c>2</c>, is not a reading of a long line that contains it.</summary>
    [Fact]
    public void TestAShortFragmentIsNotAReadingOfTheLongLineItSitsIn()
    {
        var (seen, _, strays) = ImageReadBack.Match(["aₙ = a₁ + (n − 2)d"], ["2"]);
        Assert.Null(seen[0]);
        Assert.Single(strays);
    }

    /// <summary>A script written <c>^</c> is the same text as one set as a superscript character, and a changed exponent is a changed figure.</summary>
    [Fact]
    public void TestScriptsCompareAlikeAndAChangedExponentIsADifferentFigure()
    {
        Assert.Equal("E = mc^2 - a_n", FilmReadBack.Canon("E = mc² − aₙ"));
        var same = ImageReadBack.Match(["E = mc²"], ["E = mc^2"]);
        Assert.Equal("E = mc^2", same.Seen[0]);
        var changed = ImageReadBack.Match(["E = mc²"], ["E = mc^3"]);
        Assert.Null(changed.Seen[0]);
        Assert.Equal([("different figure", "E = mc²")], changed.Strays.Select(s => (s.Kind, s.Near)));
    }

    /// <summary>Readings in LaTeX become linear forms through the same parser; a built-up equation split into rows is joined.</summary>
    [Fact]
    public void TestLatexReadingsAreLinearisedAndSplitEquationsJoined()
    {
        var prepared = MathReading.Prepare([
            @"x = \frac{-b \pm \sqrt{b^2 - 5ac}", "2a",
            @"\int_{0}^{1} x^2 \, dx = \frac{1}", "4",
            "Revenue grew 22%",
            @"a^2 + b^2 = c^2"]);

        Assert.Equal(["x = (−b ± √(b² − 5ac))/(2a)", "∫₀¹ x² dx = 1/4", "Revenue grew 22%", "a² + b² = c²"], prepared.Select(p => p.Compare));
        Assert.Equal([(0, 1), (2, 3), (4, 4), (5, 5)], prepared.Select(p => (p.First, p.Last)));
        Assert.Equal("x = \\frac{-b \\pm \\sqrt{b^2 - 5ac}\n2a", prepared[0].Raw);
    }

    /// <summary>
    /// Readings as the reader sent them on 2026-10-23: wrapped in <c>$…$</c>, and with <c>\begin</c> turned into a backspace by
    /// a JSON string that did not double its backslashes. A price is not math and keeps its dollar sign.
    /// </summary>
    [Theory]
    [InlineData("$E = mc^3$", "E = mc^3")]
    [InlineData("\begin{pmatrix} 2 & 7 \\\\ 0 & 3 \\end{pmatrix}", @"\begin{pmatrix} 2 & 7 \\ 0 & 3 \end{pmatrix}")]
    [InlineData("a \times b", @"a \times b")]
    [InlineData(@"\[ x^2 \]", "x^2")]
    [InlineData("$974M returned", "$974M returned")]
    public void TestAReadingIsUnwrappedAndItsEscapesRepaired(string read, string meant) =>
        Assert.Equal(meant, MathReading.Unwrap(read));

    /// <summary>
    /// The fault probe (2026-10-23): a denominator painted over was read as <c>\frac{1}{}</c>. The stray belongs beside its
    /// equation, so its crop shows where to look.
    /// </summary>
    [Fact]
    public void TestAnEquationWithAPartMissingIsAStrayBesideIt()
    {
        var prepared = MathReading.Prepare([@"\int_0^1 x^2 dx = \frac{1}{}"]);
        var (seen, _, strays) = ImageReadBack.Match(["∫₀¹ x² dx = 1/3", "a² + b² = c²"], [prepared[0].Compare]);
        Assert.Null(seen[0]);
        Assert.Equal([("different figure", "∫₀¹ x² dx = 1/3")], strays.Select(s => (s.Kind, s.Near)));
    }

    /// <summary>
    /// A sign is part of a figure (the axes film read −1 as 1, 2026-10-23); a binary minus and a year range's hyphen are not.
    /// </summary>
    [Fact]
    public void TestASignIsPartOfAFigure()
    {
        Assert.Equal(["-1"], FilmReadBack.MatchFigures("−1"));
        Assert.Equal(["2", "4"], FilmReadBack.MatchFigures("b² − 4ac"));
        Assert.Equal(["2016", "19"], FilmReadBack.MatchFigures("2016-19"));
        Assert.Equal(["-3"], FilmReadBack.MatchFigures("(−3%)"));

        var (seen, _, strays) = ImageReadBack.Match(["−1", "Lost −$974M"], ["1", "Lost $974M"]);
        Assert.Equal([null, null], seen.AsEnumerable());
        Assert.Equal(2, strays.Length);
    }

    /// <summary>The same characters spaced differently are the same reading: <c>x² dx</c> and <c>x^2dx</c>; different figures are not.</summary>
    [Fact]
    public void TestSpacingAloneDoesNotMakeAReadingDifferent()
    {
        Assert.Equal("∫_0^1 x^2dx = 1/3", ImageReadBack.Match(["∫₀¹ x² dx = 1/3"], ["∫_0^1 x^2dx = 1/3"]).Seen[0]);
        Assert.Null(ImageReadBack.Match(["x = 1 2"], ["x = 12"]).Seen[0]);
    }

    /// <summary>
    /// End to end on a real canvas: an equation read back in LaTeX is seen, shown as read; the same equation drawn with
    /// 4ac and read with 5ac is a different figure.
    /// </summary>
    [Fact]
    public async Task TestAnEquationReadBackInLatexIsSeenAndAChangedTermIsAStray()
    {
        var canvas = new global::Polson.Drawing.Skia.SkiaCanvas(900, 400);
        var ctx = canvas.GetContext();
        ctx.FillStyle = "#ffffff";
        ctx.FillRect(0, 0, 900, 400);
        ctx.FillStyle = "#111111";
        ctx.FillEquation(@"E = mc^2", 40, 100, new Hashtable { ["size"] = 48f });
        ctx.FillEquation(@"x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}", 40, 280, new Hashtable { ["size"] = 48f });

        var read = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["text"] = "E = mc^2" },
                new JsonObject { ["text"] = @"x = \frac{-b \pm \sqrt{b^2 - 5ac}" },
                new JsonObject { ["text"] = "2a" }),
        }.ToJsonString();
        using var reader = new VideoReader([new NemotronEndpoint(NemotronEndpoint.Nebius, "https://tf.example/v1", "k", NemotronEndpoint.NebiusModel)],
            new DocumentBudget(5), root, http: new HttpClient(new Answer(read)));
        var result = await new VerifyToolkit(reader, root, null).Image(canvas, new Dictionary<string, object?> { ["name"] = "eq" });

        var items = ((object?[])result["items"]!).Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(["seen", "missed"], items.Select(i => (string)i["status"]!));
        Assert.Equal("E = mc^2", items[0]["readAs"]);
        var stray = Assert.Single(((object?[])result["strays"]!).Cast<Dictionary<string, object?>>());
        Assert.Equal(("different figure", "x = (−b ± √(b² − 4ac))/(2a)"), ((string)stray["kind"]!, (string)stray["near"]!));
        Assert.Equal("x = \\frac{-b \\pm \\sqrt{b^2 - 5ac}\n2a", stray["read"]);
    }

    /// <summary>
    /// End to end on a real canvas: one line read as set, one read with another figure, one painted over and so missed. The
    /// still goes to the reader as an image; the stray's crop is saved and it stays pending until settled.
    /// </summary>
    [Fact]
    public async Task TestAStillIsCheckedAndItsStraysWaitForTheirCrops()
    {
        var canvas = new global::Polson.Drawing.Skia.SkiaCanvas(400, 240);
        var ctx = canvas.GetContext();
        ctx.FillStyle = "#ffffff";
        ctx.FillRect(0, 0, 400, 240);
        ctx.FillStyle = "#111111";
        ctx.Font = "28px Arial";
        ctx.FillText("BE COOL", 20, 50);
        ctx.FillText("Returned $974 million", 20, 110);
        ctx.FillText("+46%", 20, 170);
        ctx.FillText("Hidden note", 20, 220);
        ctx.FillRect(0, 195, 400, 45);                // painted over: the reader cannot see it
        ctx.FillText("Off the page", 900, 50);

        var read = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["text"] = "BE COOL" },
                new JsonObject { ["text"] = "Returned $744 million" },
                new JsonObject { ["text"] = "+46%" }),
        }.ToJsonString();
        var answer = new Answer(read);
        using var reader = new VideoReader([new NemotronEndpoint(NemotronEndpoint.Nebius, "https://tf.example/v1", "k", NemotronEndpoint.NebiusModel)],
            new DocumentBudget(5), root, http: new HttpClient(answer));
        var recorded = new List<string>();
        var session = new SessionContext();
        var verify = new VerifyToolkit(reader, root, session, (type, _) => recorded.Add(type));

        var result = await verify.Image(ctx, new Dictionary<string, object?> { ["computed"] = new Dictionary<string, object?> { ["+46%"] = "582.3 / 399.0 − 1" } });

        Assert.True((bool)result["ok"]!, result.GetValueOrDefault("error") as string);
        Assert.Contains("\"image_url\"", answer.LastRequest);
        Assert.True(File.Exists(Path.Combine(root, "artifacts", "verify", "image.png")));
        var items = ((object?[])result["items"]!).Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(["BE COOL", "Returned $974 million", "+46%", "Hidden note", "Off the page"], items.Select(i => (string)i["text"]!));
        Assert.Equal(["seen", "missed", "seen", "missed", "off the canvas"], items.Select(i => (string)i["status"]!));
        Assert.Equal(["brief", "document", "computed", "own words", "own words"], items.Select(i => (string)i["source"]!));
        var stray = Assert.Single(((object?[])result["strays"]!).Cast<Dictionary<string, object?>>());
        Assert.Equal(("different figure", "Returned $974 million", "artifacts/verify/image-stray-1.png"), ((string)stray["kind"]!, (string)stray["near"]!, (string)stray["frame"]!));
        Assert.True(File.Exists(Path.Combine(root, "artifacts", "verify", "image-stray-1.png")));
        Assert.False((bool)result["passed"]!);
        Assert.Contains("look at its crop", (string)result["summary"]!);

        // A citation passed as computed is set aside: its working shows no arithmetic.
        var cited = await new VerifyToolkit(reader, root, null).Image(canvas, new Dictionary<string, object?> { ["computed"] = new Dictionary<string, object?> { ["+46%"] = "from the letter" } });
        Assert.Contains("1 computed entry was not used", (string)cited["summary"]!);
        Assert.Equal("unsourced figure", ((object?[])cited["items"]!).Cast<Dictionary<string, object?>>().Single(i => (string)i["text"]! == "+46%")["source"]);

        var refused = Assert.Throws<InvalidOperationException>(() => verify.Stray(1, "misread"));
        Assert.Contains("artifacts/verify/image-stray-1.png", refused.Message);
        var later = new VerifyToolkit(reader, root, session, (type, _) => recorded.Add(type));
        var settled = later.Stray(1, "misread", "the crop shows $974 million");
        Assert.True((bool)settled["passed"]!);
        Assert.Equal(["image.verify", "check", "image.stray", "check"], recorded);

        // The same pixels checked again keep the verdict already given; the check passes without settling again.
        var again = await new VerifyToolkit(reader, root, session).Image(canvas, new Dictionary<string, object?> { ["computed"] = new Dictionary<string, object?> { ["+46%"] = "582.3 / 399.0 − 1" } });
        Assert.True((bool)again["passed"]!, (string)again["summary"]!);

        Assert.Throws<ArgumentException>(() => verify.Image(canvas, new Dictionary<string, object?> { ["name"] = "../x" }).GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => verify.Image("artifacts/final.png").GetAwaiter().GetResult());
    }
    #endregion

    #region Types
    /// <summary>The watcher: answers every request with one transcription, as a chat completion.</summary>
    private sealed class Answer(string content) : HttpMessageHandler
    {
        /// <summary>The last request's body, to see how the piece was sent.</summary>
        public string LastRequest { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop" }),
                    ["usage"] = new JsonObject { ["total_tokens"] = 100 },
                }.ToJsonString()),
            };
        }
    }
    #endregion
}
