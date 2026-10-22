namespace Polson.MCPServer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Polson.Animation;
using Polson.ExtendedMind.DocumentProcessing;

/// <summary>
/// Checks a finished film against what it set and where that came from: <c>Verify.film(comp, video)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three things, and only one is a model's.</b> What the film shows is read from the composition itself
/// (<c>comp.texts()</c>): every text and the seconds it is on screen. Where each came from is looked up mechanically —
/// the brief, the pages of the project's documents, the session's Qloo answers and research, or a figure the script says
/// it computed. Only the watching is a model's: <c>Video.ask</c> transcribes the encoded film, and the transcription is
/// compared with the ground truth. A text set but not read is <b>missed</b> — usually too brief or too small; a reading
/// of something never set is a <b>stray</b>, either the watcher misreading or a real fault in the film (a half-drawn
/// figure, a frame of the wrong scene).
/// </para>
/// <para>
/// <b>A stray is not reported until it has been looked at.</b> The watcher misread $974M as $744M on a report film, and
/// the frame showed $974M. So each stray's frame is grabbed from the video, and the stray stays <i>pending</i> until the
/// agent has looked and called it with <c>Verify.stray(n, 'misread' | 'real')</c>. The studio shows a stray only then.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>Verify.film(...)</c> reaches <c>Film(...)</c>.
/// </para>
/// </remarks>
public sealed class VerifyToolkit
{
    #region Constructors
    /// <param name="video">The watcher; unconfigured, a film check answers why.</param>
    /// <param name="projectRoot">Where the film, the brief and the documents are; nothing outside it is read or written.</param>
    /// <param name="session">Holds the Qloo answers and research this session fetched, and the latest check for <see cref="Stray"/>.</param>
    /// <param name="record">Records <c>film.verify</c> and <c>film.stray</c> for the studio's Verification view.</param>
    public VerifyToolkit(VideoReader? video, string? projectRoot, SessionContext? session,
        Action<string, IReadOnlyDictionary<string, object?>>? record = null)
    {
        this.video = video;
        this.projectRoot = projectRoot;
        this.session = session;
        this.record = record;
    }
    #endregion

    #region Methods
    /// <summary>
    /// The film checked against what it set and where that came from: <c>{ ok, passed, items, strays, counts, summary }</c>,
    /// or <c>{ ok: false, failureName, error, remedy }</c> when the watcher could not watch (<c>Pending</c>: run it again
    /// unchanged). <paramref name="options"/>: <c>{ computed?: { 'text as set': 'its working' } }</c> for figures the script
    /// computed from sourced ones.
    /// </summary>
    public async Task<Dictionary<string, object?>> Film(MotionComposition comp, string videoPath, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(comp);
        if (string.IsNullOrWhiteSpace(videoPath)) throw new ArgumentException("Verify.film needs the encoded film: Verify.film(comp, 'output.mp4').");
        var computed = Computed(options);
        if (video is null || !video.IsAvailable)
            return Failed("NotConfigured", "No Nemotron endpoint is configured, so nothing can watch the film.", "Configure ApiKeys:NvidiaNIM or Token Factory.");

        var full = ProjectPath.Resolve(projectRoot, videoPath, "video", "Read");
        if (!File.Exists(full)) throw new FileNotFoundException($"Verify.film: '{videoPath}' is not in the project. Encode it first.", full);

        var answer = await video.Ask(videoPath, Question, new DocumentOptions { Schema = Schema });
        if (!answer.Success)
            return Failed(answer.FailureName, answer.Error, answer.FailureName == "Pending"
                ? "The watcher is still reading; it finishes into the cache. Run the same call again, unchanged."
                : answer.Remedy);

        var read = FilmReadBack.ParseReading(answer.Text);
        var expected = comp.Texts().OfType<Dictionary<string, object?>>()
            .Select(t => new FilmReadBack.Shown((string)t["text"]!, t["from"] as double?, t["to"] as double?, t["seconds"] as double? ?? 0,
                [.. ((object?[])t["intervals"]!).OfType<double[]>()]))
            .ToArray();
        var sources = new FilmSources(projectRoot, session);
        var match = FilmReadBack.Match(expected, read);

        var items = expected.Select((e, i) =>
        {
            var source = sources.Classify(e.Text, computed);
            var seen = match.Seen[i];
            return new Dictionary<string, object?>
            {
                ["text"] = e.Text,
                ["from"] = e.From,
                ["to"] = e.To,
                ["seconds"] = e.Seconds,
                ["source"] = source.Kind,
                ["detail"] = source.Detail,
                ["seen"] = seen is not null,
                ["readAs"] = seen?.Text,
                ["readAt"] = seen?.From,
                ["status"] = e.From is null ? "never shown" : seen is not null ? "seen" : "missed",
            };
        }).ToArray();

        var strays = match.Strays.Select((s, i) =>
        {
            var at = Math.Clamp((s.Reading.From ?? 0) + 0.25, 0, comp.Duration);
            var frame = $"artifacts/verify/stray-{i + 1}.png";
            var error = FFmpeg.Frame(full, at, ProjectPath.Resolve(projectRoot, frame, "frame", "Write"));
            return new FilmStray(i + 1, s.Reading.Text, s.Reading.From, error is null ? frame : null, error, s.Near, s.Kind);
        }).ToList();

        var check = new FilmCheck(videoPath, comp.Duration, items, strays, answer.Provenance?.Model, answer.Provenance?.Endpoint);
        if (session is not null) session.FilmCheck = check;
        else last = check;
        record?.Invoke("film.verify", check.Event());
        return check.Result();
    }

    /// <summary>
    /// A stray settled after looking at its frame: <paramref name="verdict"/> <c>'misread'</c> (the frame shows what the film
    /// set; the watcher misread it) or <c>'real'</c> (the frame shows what was read: a fault in the film). Returns the check
    /// as it now stands.
    /// </summary>
    public Dictionary<string, object?> Stray(int number, string verdict, string? note = null)
    {
        var check = (session?.FilmCheck ?? last) ?? throw new InvalidOperationException("Verify.stray: run Verify.film first; there is no check to settle.");
        var stray = check.Strays.FirstOrDefault(s => s.Number == number)
            ?? throw new ArgumentException($"Verify.stray: there is no stray {number}; the check has {check.Strays.Count}.");
        var v = verdict?.Trim().ToLowerInvariant();
        if (v is not ("misread" or "real"))
            throw new ArgumentException("Verify.stray's verdict is 'misread' (the frame shows what the film set) or 'real' (a fault in the film).");
        stray.Verdict = v;
        stray.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        record?.Invoke("film.stray", new Dictionary<string, object?>
        {
            ["video"] = check.Video, ["n"] = number, ["read"] = stray.Read, ["at"] = stray.At, ["frame"] = stray.Frame,
            ["near"] = stray.Near, ["verdict"] = v, ["note"] = stray.Note,
        });
        return check.Result();
    }
    #endregion

    #region Private methods
    private static Dictionary<string, string> Computed(object? options)
    {
        var o = JsInterop.AsDict(options);
        if (o is null) return [];
        foreach (var key in o.Keys)
            if (key?.ToString() != "computed") throw new ArgumentException($"Verify.film's options are {{ computed }}; '{key}' is not one.");
        return JsInterop.AsDict(o["computed"]) is { } c
            ? c.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => c[k]?.ToString() ?? "", StringComparer.Ordinal)
            : [];
    }

    private static Dictionary<string, object?> Failed(string? name, string? error, string? remedy) =>
        new() { ["ok"] = false, ["failureName"] = name, ["error"] = error, ["remedy"] = remedy };
    #endregion

    #region Fields
    /// <summary>What the watcher is asked. Every text, as written, and when: the transcription the ground truth is held against.</summary>
    internal const string Question =
        "List every piece of text that appears on screen in this video, each exactly as it is written — keep numbers, units, "
        + "currency signs and punctuation as shown — with the second it first appears and the second it disappears. Give "
        + "lines that appear at different times as separate items. Only text: do not describe pictures.";

    internal const string Schema =
        "{\"type\":\"object\",\"required\":[\"items\"],\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\","
        + "\"required\":[\"text\",\"from\",\"to\"],\"properties\":{\"text\":{\"type\":\"string\"},\"from\":{\"type\":\"number\"},"
        + "\"to\":{\"type\":\"number\"}}}}}}";

    private readonly VideoReader? video;
    private readonly string? projectRoot;
    private readonly SessionContext? session;
    private readonly Action<string, IReadOnlyDictionary<string, object?>>? record;
    private FilmCheck? last;
    #endregion
}

/// <summary>One reading of something the film never set, held until the agent has looked at its frame.</summary>
public sealed class FilmStray(int number, string read, double? at, string? frame, string? frameError, string? near, string kind)
{
    public int Number { get; } = number;
    public string Read { get; } = read;
    public double? At { get; } = at;
    public string? Frame { get; } = frame;
    public string? FrameError { get; } = frameError;

    /// <summary>The text set nearest to it, when the reading resembles one: a different figure in a line the film did set.</summary>
    public string? Near { get; } = near;

    /// <summary><c>'different figure'</c> or <c>'never set'</c>.</summary>
    public string Kind { get; } = kind;

    /// <summary>Null until looked at; then <c>'misread'</c> or <c>'real'</c>.</summary>
    public string? Verdict { get; internal set; }

    public string? Note { get; internal set; }

    internal Dictionary<string, object?> Dict() => new()
    {
        ["n"] = Number, ["read"] = Read, ["at"] = At, ["frame"] = Frame, ["frameError"] = FrameError, ["near"] = Near,
        ["kind"] = Kind, ["verdict"] = Verdict, ["note"] = Note,
    };
}

/// <summary>A film's latest check: what it set, where each came from, what the watcher read, and its strays.</summary>
public sealed class FilmCheck(string video, double duration, Dictionary<string, object?>[] items, List<FilmStray> strays, string? model, string? endpoint)
{
    public string Video { get; } = video;
    public List<FilmStray> Strays { get; } = strays;

    /// <summary>As recorded for the studio: every item, and every stray with its state.</summary>
    internal Dictionary<string, object?> Event() => new()
    {
        ["video"] = Video, ["duration"] = duration, ["model"] = model, ["endpoint"] = endpoint,
        ["items"] = items.Cast<object?>().ToArray(),
        ["strays"] = Strays.Select(s => (object?)s.Dict()).ToArray(),
        ["counts"] = Counts(),
    };

    /// <summary>As the script receives it, with a summary and whether it passed.</summary>
    internal Dictionary<string, object?> Result()
    {
        var counts = Counts();
        var unsourced = items.Count(i => (string?)i["source"] == "unsourced figure");
        var pending = Strays.Count(s => s.Verdict is null);
        var real = Strays.Count(s => s.Verdict == "real");
        var bySource = items.GroupBy(i => (string)i["source"]!).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
        var missed = items.Where(i => (string?)i["status"] == "missed").ToArray();
        var summary = $"{items.Length} texts on screen: {counts["seen"]} seen by the watcher"
            + (missed.Length == 0 ? "" : $", {missed.Length} missed ({string.Join("; ", missed.Take(4).Select(m => $"\"{Clip((string)m["text"]!)}\" on screen {m["seconds"]:0.#} s"))})")
            + $". Sources: {string.Join(", ", bySource)}."
            + (unsourced == 0 ? "" : $" {unsourced} figure{(unsourced == 1 ? " has" : "s have")} no source: find it in a document or the brief, or pass its working as computed.")
            + (pending == 0 ? "" : $" {pending} reading{(pending == 1 ? "" : "s")} of text the film never set: look at {(pending == 1 ? "its frame" : "their frames")} ({string.Join(", ", Strays.Where(s => s.Verdict is null).Select(s => s.Frame ?? "no frame"))}), then Verify.stray(n, 'misread' | 'real').")
            + (real == 0 ? "" : $" {real} confirmed fault{(real == 1 ? "" : "s")} in the film: fix and encode again.");
        return new()
        {
            ["ok"] = true,
            ["passed"] = unsourced == 0 && pending == 0 && real == 0,
            ["video"] = Video,
            ["items"] = items.Cast<object?>().ToArray(),
            ["strays"] = Strays.Select(s => (object?)s.Dict()).ToArray(),
            ["counts"] = counts,
            ["model"] = model,
            ["summary"] = summary,
        };
    }

    private Dictionary<string, object?> Counts() => new()
    {
        ["shown"] = items.Length,
        ["seen"] = items.Count(i => (string?)i["status"] == "seen"),
        ["missed"] = items.Count(i => (string?)i["status"] == "missed"),
        ["unsourced"] = items.Count(i => (string?)i["source"] == "unsourced figure"),
        ["strays"] = Strays.Count,
        ["pending"] = Strays.Count(s => s.Verdict is null),
        ["real"] = Strays.Count(s => s.Verdict == "real"),
    };

    private static string Clip(string s) => s.Length > 40 ? s[..39] + "…" : s.Replace('\n', ' ');
}

/// <summary>The read-back's comparison: what the watcher read against what the composition set.</summary>
internal static class FilmReadBack
{
    #region Types
    /// <summary>A text the film set, and when it is on screen.</summary>
    internal sealed record Shown(string Text, double? From, double? To, double Seconds, double[][] Intervals);

    /// <summary>One item of the watcher's transcription.</summary>
    internal sealed record Reading(string Text, double? From, double? To);

    /// <summary>A reading that matched nothing set: <c>Near</c> the set text it resembles, if any.</summary>
    internal sealed record Unmatched(Reading Reading, string? Near, string Kind);

    internal sealed record Outcome(Reading?[] Seen, Unmatched[] Strays);
    #endregion

    #region Methods
    /// <summary>The watcher's items, from the schema'd answer; none when it is not that shape.</summary>
    internal static Reading[] ParseReading(string? json)
    {
        try
        {
            return [.. (JsonNode.Parse(json ?? "")?["items"]?.AsArray() ?? [])
                .Select(n => new Reading(n?["text"]?.GetValue<string>() ?? "", Num(n?["from"]), Num(n?["to"])))
                .Where(r => Normal(r.Text).Length > 0)];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }

        static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    }

    /// <summary>
    /// Each set text's reading, or null when none matched; and each reading that matched nothing. A reading matches a text
    /// on screen within <see cref="Slack"/> seconds of its interval whose words it shares — and whose figures it has
    /// exactly: the same line with a different figure is a stray, kind <c>'different figure'</c>, not a match. A text of
    /// several lines is seen when every line is read.
    /// </summary>
    internal static Outcome Match(IReadOnlyList<Shown> shown, IReadOnlyList<Reading> read)
    {
        var used = new bool[read.Count];
        var seen = new Reading?[shown.Count];
        for (var i = 0; i < shown.Count; i++)
        {
            var s = shown[i];
            if (s.From is null) continue;
            var lines = s.Text.Split('\n').Select(l => l.Trim()).Where(l => Normal(l).Length > 0).ToArray();
            var whole = Best(s, s.Text, read, used);
            if (whole >= 0) { used[whole] = true; seen[i] = read[whole]; continue; }
            if (lines.Length < 2) continue;
            var parts = lines.Select(l => Best(s, l, read, used)).ToArray();
            if (parts.All(p => p >= 0) && parts.Distinct().Count() == parts.Length)
            {
                foreach (var p in parts) used[p] = true;
                seen[i] = new Reading(string.Join("\n", parts.Select(p => read[p].Text)), read[parts[0]].From, read[parts[^1]].To);
            }
        }

        var strays = read.Select((r, k) => (r, k)).Where(x => !used[x.k]).Select(x =>
        {
            var near = shown.Where(s => s.From is not null && InTime(s, x.r)).Select(s => (s, score: Words(s.Text, x.r.Text)))
                .Where(p => p.score >= 0.5).OrderByDescending(p => p.score).Select(p => p.s.Text).FirstOrDefault();
            return new Unmatched(x.r, near, near is not null && Figures(near).Length > 0 ? "different figure" : "never set");
        }).ToArray();
        return new Outcome(seen, strays);
    }

    /// <summary>The best unused reading of <paramref name="text"/> on screen with <paramref name="shown"/>, or -1.</summary>
    private static int Best(Shown shown, string text, IReadOnlyList<Reading> read, bool[] used)
    {
        var (best, bestScore) = (-1, 0.0);
        var figures = Figures(text);
        for (var k = 0; k < read.Count; k++)
        {
            if (used[k] || !InTime(shown, read[k])) continue;
            if (!figures.SequenceEqual(Figures(read[k].Text))) continue;
            var score = Similarity(text, read[k].Text);
            if (score > bestScore) (best, bestScore) = (k, score);
        }
        return bestScore >= 0.6 ? best : -1;
    }

    private static bool InTime(Shown s, Reading r) =>
        r.From is not { } at || s.Intervals.Length == 0 || s.Intervals.Any(iv => at >= iv[0] - Slack && at <= iv[1] + Slack);

    /// <summary>1 for the same words in order; containment counts most of that; otherwise the share of words in common.</summary>
    internal static double Similarity(string a, string b)
    {
        var (na, nb) = (Normal(a), Normal(b));
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;
        if (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal))
            return 0.6 + 0.4 * Math.Min(na.Length, nb.Length) / Math.Max(na.Length, nb.Length);
        return Words(a, b);
    }

    private static double Words(string a, string b)
    {
        var (wa, wb) = (Normal(a).Split(' ').ToHashSet(), Normal(b).Split(' ').ToHashSet());
        return wa.Count == 0 || wb.Count == 0 ? 0 : wa.Intersect(wb).Count() / (double)wa.Union(wb).Count();
    }

    /// <summary>A text as compared: no case, accents or punctuation, straight quotes, single spaces.</summary>
    internal static string Normal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) || c is '%' or '$' or '.' ? char.ToLowerInvariant(c) : ' ');
        }
        return Regex.Replace(Regex.Replace(sb.ToString(), @"(?<!\d)\.|\.(?!\d)", " "), @"\s+", " ").Trim();
    }

    /// <summary>The figures in a text, commas dropped, in order: "$40.4 billion (282 million)" is 40.4 and 282.</summary>
    internal static string[] Figures(string? text) =>
        [.. Figure.Matches(text ?? "").Select(m => m.Value.Replace(",", "").TrimEnd('.'))];
    #endregion

    #region Fields
    /// <summary>How far a reading's time may sit outside a text's interval: the watcher samples about once a second.</summary>
    internal const double Slack = 1.5;

    private static readonly Regex Figure = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);
    #endregion
}

/// <summary>Where a text on screen came from, looked up rather than asked: the brief, a document page, Qloo, research.</summary>
internal sealed class FilmSources
{
    #region Constructors
    internal FilmSources(string? projectRoot, SessionContext? session)
    {
        brief = Normal(ReadBrief(projectRoot));
        pages = ReadPages(projectRoot);
        qloo = session?.Taste.All.Where(r => r.Ok)
            .SelectMany(r => r.Entities.OfType<Dictionary<string, object?>>()
                .Select(e => (Name: FilmReadBack.Normal(e.GetValueOrDefault("name") as string), r.Id)))
            .Where(x => x.Name.Length > 0).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Id) ?? [];
        research = session?.Research.All.Where(t => t.Status == "completed" && t.Result is not null)
            .Select(t => (t.Id, Text: Normal(JsonSerializer.Serialize(t.Result)))).ToArray() ?? [];
    }
    #endregion

    #region Methods
    /// <summary>The source of a text set on screen: <c>(kind, detail)</c>, kind one of <c>computed</c>, <c>brief</c>,
    /// <c>document</c>, <c>qloo</c>, <c>research</c>, <c>own words</c>, or <c>unsourced figure</c> for own words with a number in them.</summary>
    internal (string Kind, string? Detail) Classify(string text, IReadOnlyDictionary<string, string> computed)
    {
        if (computed.TryGetValue(text, out var working) || computed.TryGetValue(text.Trim(), out working)) return ("computed", working);
        var n = Normal(text);
        var figures = FilmReadBack.Figures(text);
        if (n.Length > 0 && brief.Contains(n, StringComparison.Ordinal)) return ("brief", null);

        // A figure is a document's when every figure in the text is on one of its pages; words, when the page has them.
        foreach (var (doc, number, pageText, pageFigures) in pages)
        {
            var found = figures.Length > 0 ? figures.All(pageFigures.Contains) : n.Length >= 4 && pageText.Contains(n, StringComparison.Ordinal);
            if (found) return ("document", $"{doc} p. {number}");
        }
        if (qloo.TryGetValue(FilmReadBack.Normal(text), out var handle)) return ("qloo", handle);
        foreach (var (id, body) in research)
            if (figures.Length > 0 ? figures.All(f => body.Contains(f, StringComparison.Ordinal)) : n.Length >= 4 && body.Contains(n, StringComparison.Ordinal))
                return ("research", id);
        return figures.Length > 0 ? ("unsourced figure", null) : ("own words", null);
    }
    #endregion

    #region Private methods
    private static string Normal(string? text) => FilmReadBack.Normal(text);

    /// <summary>The brief: the director's words between its markers, or the whole file.</summary>
    private static string ReadBrief(string? root)
    {
        if (root is null) return "";
        var path = Path.Combine(root, "brief.md");
        if (!File.Exists(path)) return "";
        var text = File.ReadAllText(path);
        var (a, b) = (text.IndexOf("BRIEF-BEGIN", StringComparison.Ordinal), text.IndexOf("BRIEF-END", StringComparison.Ordinal));
        return a >= 0 && b > a ? text[a..b] : text;
    }

    /// <summary>
    /// Every page of every document in <c>documents/</c> that <c>Documents.ask</c> has read as text, from its page cache
    /// (keyed by the file's hash, as the reader keys it): the document, its page number, the page's text and figures.
    /// </summary>
    private static (string Doc, int Page, string Text, HashSet<string> Figures)[] ReadPages(string? root)
    {
        if (root is null) return [];
        var docs = Path.Combine(root, "documents");
        var cache = Path.Combine(root, ".polson", "documents", "pages");
        if (!Directory.Exists(docs) || !Directory.Exists(cache)) return [];

        var found = new List<(string, int, string, HashSet<string>)>();
        foreach (var file in Directory.EnumerateFiles(docs))
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))[..32];
            var json = Path.Combine(cache, hash, "pages.json");
            if (!File.Exists(json)) continue;
            var pagesNode = JsonNode.Parse(File.ReadAllText(json))?["Pages"]?.AsArray() ?? [];
            var name = "documents/" + Path.GetFileName(file);
            for (var i = 0; i < pagesNode.Count; i++)
            {
                var text = pagesNode[i]?.GetValue<string>() ?? "";
                found.Add((name, i + 1, Normal(text), [.. FilmReadBack.Figures(text)]));
            }
        }
        return [.. found];
    }
    #endregion

    #region Fields
    private readonly string brief;
    private readonly (string Doc, int Page, string Text, HashSet<string> Figures)[] pages;
    private readonly Dictionary<string, string> qloo;
    private readonly (string Id, string Text)[] research;
    #endregion
}
