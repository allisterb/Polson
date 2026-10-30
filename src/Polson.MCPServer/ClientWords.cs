namespace Polson.MCPServer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

using Polson.Drawing.Skia;
using Polson.Drawing.Svg;

/// <summary>
/// The words a logo run sets, held against the words the client wrote: a check recorded by the server, not the agent.
/// </summary>
/// <remarks>
/// polson_graphics_studio_logo (2026-10-30) was told every word on the mark is the client's, recorded "No tagline
/// invented (<c>tagline: ''</c>)", wrote "Never invent unapproved taglines" into its rules, and then set "DETERMINISTIC
/// VECTOR &amp; MOTION RUNTIME" under the mark with <c>paper.text</c> and "Autonomous Vector &amp; Motion Runtime" on a
/// mock-up with <c>fillText</c>. The rule had been read as one about the toolkit's <c>tagline</c> argument. So the
/// words are read off what was drawn: on the mark (<c>final</c>) a word the brief does not contain fails a check; on
/// the brand sheet and a mock-up, which carry labels of their own (a colour's name, a typeface), it is a warning.
/// </remarks>
internal static class ClientWords
{
    #region Methods
    /// <summary>Whether the project at <paramref name="projectRoot"/> is a logo run, from its <c>project.json</c>.</summary>
    public static bool Applies(string? projectRoot)
    {
        if (projectRoot is null) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "project.json")));
            return manifest.RootElement.TryGetProperty("workflow", out var w) && w.GetString() == "logo";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// What the client wrote: the brief between its markers, and any text file in <c>documents/</c>. Never the
    /// parameter table under the brief, which the agent fills in.
    /// </summary>
    public static string Brief(string projectRoot)
    {
        var text = "";
        try
        {
            var brief = File.ReadAllText(Path.Combine(projectRoot, "brief.md"));
            var (begin, end) = (brief.IndexOf(BriefBegin, StringComparison.Ordinal), brief.IndexOf(BriefEnd, StringComparison.Ordinal));
            text = begin >= 0 && end > begin ? brief[(begin + BriefBegin.Length)..end] : brief;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var documents = Path.Combine(projectRoot, "documents");
        if (Directory.Exists(documents))
            foreach (var file in Directory.EnumerateFiles(documents).Where(f => TextDocument.IsMatch(Path.GetExtension(f))))
                try { text += "\n" + File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return text;
    }

    /// <summary>Every line of text a canvas, its context, a paper or SVG markup drew; empty for anything else.</summary>
    public static IReadOnlyList<string> LinesOf(object? source) => source switch
    {
        SkiaCanvas c => [.. c.DrawnText.Select(l => l.Words)],
        CanvasRenderingContext2D ctx => LinesOf(ctx.Canvas),
        SnapPaper p => LinesOf(p.ToString()),
        string svg when svg.Contains("<svg", StringComparison.OrdinalIgnoreCase) => SvgLines(svg),
        _ => [],
    };

    /// <summary>The distinct words in <paramref name="lines"/> that <paramref name="brief"/> does not contain, in the order drawn.</summary>
    /// <remarks>
    /// A word is a run of letters, compared without case. Single letters (a monogram, a letter-spaced wordmark set a
    /// letter at a time), a few joining words, and anything with a digit (a size, a hex colour) are never counted.
    /// </remarks>
    public static string[] Foreign(IEnumerable<string> lines, string brief)
    {
        var known = Words(brief).Select(w => w.ToLowerInvariant()).ToHashSet();
        return [.. lines.SelectMany(Words)
            .Where(w => w.Length > 1 && !Joining.Contains(w.ToLowerInvariant()) && !known.Contains(w.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The claim the check records for one artifact; <c>Stage.check</c> refuses to write one itself.</summary>
    public static string Claim(string artifact) => $"{ClaimPrefix}{artifact}: every word is the brief's";

    /// <summary>What to do about words not in the brief on the mark, for the failed check's detail.</summary>
    public static string MarkDetail(string[] foreign) =>
        $"not in the brief: {List(foreign)}. The mark carries the client's name and, only if the brief gives one, its "
        + "tagline word for word. Take the rest off: a descriptor you wrote is yours, wherever it is drawn.";

    /// <summary>The warning for words not in the brief on a sheet or a mock-up, which may be the sheet's own labels.</summary>
    public static string SheetWarning(string what, string[] foreign) =>
        $"[WARN] words: {what} sets words the brief does not contain: {List(foreign)}. A label of the sheet's own (a "
        + "colour's name, a measurement, a typeface) is fine. A line about the client (a descriptor, a tagline, a "
        + "claim) is not, unless the brief says it word for word; a mock-up's sample copy is labelled a sample in "
        + "direction.md.";

    private static IReadOnlyList<string> SvgLines(string svg)
    {
        try { return [.. PosterTypeAudit.Lines(PosterTypeAudit.Parse(svg)).Select(l => l.Words)]; }
        catch (Exception) { return []; }   // unparsable markup has no lines to hold against the brief
    }

    private static IEnumerable<string> Words(string text) =>
        WordPattern.Matches(text).Where(m => !m.Value.Any(char.IsDigit)).Select(m => m.Value);

    private static string List(string[] words) => string.Join(", ", words.Take(12)) + (words.Length > 12 ? $" and {words.Length - 12} more" : "");
    #endregion

    #region Fields
    /// <summary>The prefix of the claim this check records.</summary>
    internal const string ClaimPrefix = "Client's words on ";

    private const string BriefBegin = "BRIEF-BEGIN";
    private const string BriefEnd = "BRIEF-END";

    private static readonly Regex WordPattern = new(@"[\p{L}\p{M}\d]+(?:['’][\p{L}]+)?", RegexOptions.Compiled);
    private static readonly Regex TextDocument = new(@"^\.(md|txt)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Joining = ["and", "the", "of", "for", "by", "in", "on", "at", "to", "an", "a"];
    #endregion
}
