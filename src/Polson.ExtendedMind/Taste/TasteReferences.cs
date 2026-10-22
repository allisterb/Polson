namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Checks the Qloo evidence a <c>direction.md</c> cites against the answers this session actually received:
/// <c>Taste.checkReferences()</c>. The Qloo counterpart of <c>Documents.ask</c>'s figure-on-its-page check.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it reads.</b> Every table whose last column is <i>Kind</i>, and of those the rows whose kind names Qloo —
/// the shape the poster, trailer and report workflows write. In each row: the Taste handles cited (<c>t5</c>,
/// <c>**t1**</c>), or, with none in the row, the handles in its section's first paragraph; the titles, written in
/// italics (<c>*The Farewell*</c>); and the tags, written in quotes with their item number (<c>"Glitzy" (item 10)</c>)
/// or bold (<c>**"Glitzy"**</c>).
/// </para>
/// <para>
/// <b>What counts as found.</b> A title is found when a handle the row cites returned it — as an entity, or in a
/// profile's shared list — or when its id is in that handle's request, which is how a <c>describe</c> names the films
/// it was asked about. A tag is found when a cited handle returned it. A title another handle returned is still not
/// found, and says which handle: the row cites the wrong evidence. Matching ignores case, accents, punctuation, a
/// leading article and a trailing year.
/// </para>
/// <para>
/// <b>What it is for.</b> A reference an agent remembers rather than reads looks exactly like one Qloo returned,
/// and a brand that resolved to a different entity ("Starbucks Reserve" came back as plain Starbucks) is cited
/// under the name the brief gave. Neither is visible in the film; both are visible here.
/// </para>
/// </remarks>
public static class TasteReferences
{
    #region Methods
    /// <summary>The check of <paramref name="text"/> against <paramref name="results"/>: <c>{ passed, rows, unverified, checked, summary }</c>.</summary>
    public static Dictionary<string, object?> Check(string text, IReadOnlyList<TasteResult> results)
    {
        var byId = results.ToDictionary(r => r.Id.ToLowerInvariant(), r => r);
        var idsByName = results
            .SelectMany(r => EntitiesOf(r))
            .Where(e => e.Id is not null)
            .GroupBy(e => Key(e.Name))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id!).Distinct().ToArray());

        var rows = new List<object?>();
        var unverified = new List<string>();
        int titles = 0, tags = 0;

        foreach (var row in Rows(text))
        {
            var problems = new List<string>();
            var handles = row.Handles.Length > 0 ? row.Handles : row.LeadHandles;
            if (handles.Length == 0) problems.Add("cites no Taste handle (t1, t2…), in the row or its section's first paragraph");

            var cited = new List<TasteResult>();
            foreach (var h in handles)
            {
                if (!byId.TryGetValue(h, out var r)) problems.Add($"{h} is not an answer of this session");
                else if (!r.Ok) problems.Add($"{h} failed: {r.Error}");
                else cited.Add(r);
            }

            var titleChecks = row.Titles.Select(t =>
            {
                var key = Key(t);
                var found = cited.FirstOrDefault(r => Returned(r, key) || Asked(r, key, idsByName));
                var elsewhere = found is null ? results.FirstOrDefault(r => r.Ok && Returned(r, key))?.Id : null;
                if (found is null)
                    unverified.Add(elsewhere is null ? $"*{t}*" : $"*{t}* (returned by {elsewhere}, not by {string.Join(", ", handles)})");
                return (object?)new Dictionary<string, object?>
                {
                    ["title"] = t, ["found"] = found is not null, ["in"] = found?.Id, ["elsewhere"] = elsewhere,
                };
            }).ToArray();

            var tagChecks = row.Tags.Select(t =>
            {
                var key = Key(t);
                var found = cited.FirstOrDefault(r => TagsOf(r).Any(n => Key(n) == key));
                if (found is null) unverified.Add($"\"{t}\"");
                return (object?)new Dictionary<string, object?> { ["tag"] = t, ["found"] = found is not null, ["in"] = found?.Id };
            }).ToArray();

            titles += titleChecks.Length;
            tags += tagChecks.Length;
            unverified.AddRange(problems.Select(p => $"{Short(row.Choice)}: {p}"));
            rows.Add(new Dictionary<string, object?>
            {
                ["section"] = row.Section,
                ["choice"] = row.Choice,
                ["handles"] = handles,
                ["titles"] = titleChecks,
                ["tags"] = tagChecks,
                ["problems"] = problems.ToArray(),
            });
        }

        var passed = rows.Count > 0 && unverified.Count == 0;
        var summary = rows.Count == 0
            ? "No Qloo rows found: no table whose last column is Kind has a row of kind Qloo."
            : $"{rows.Count} Qloo row{(rows.Count == 1 ? "" : "s")}; {titles} title{(titles == 1 ? "" : "s")} and {tags} tag{(tags == 1 ? "" : "s")} checked"
              + (unverified.Count == 0 ? "; every one was returned by the query it cites." : $"; {unverified.Count} not verified: {string.Join("; ", unverified)}.");

        return new Dictionary<string, object?>
        {
            ["passed"] = passed,
            ["rows"] = rows.ToArray(),
            ["unverified"] = unverified.ToArray(),
            ["checked"] = titles + tags,
            ["summary"] = summary,
        };
    }

    /// <summary>A name as compared: no accents, case, punctuation, leading article or trailing year.</summary>
    internal static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var text = TrailingYear.Replace(name.Trim(), "");
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var key = Space.Replace(sb.ToString(), " ").Trim();
        return LeadingArticle.Replace(key, "");
    }
    #endregion

    #region Private methods
    private static bool Returned(TasteResult r, string key) => EntitiesOf(r).Any(e => Key(e.Name) == key);

    /// <summary>Whether an entity of that name, known anywhere this session, is one of the ids this answer was asked about.</summary>
    private static bool Asked(TasteResult r, string key, Dictionary<string, string[]> idsByName) =>
        idsByName.TryGetValue(key, out var ids) && ids.Any(id => r.Request.Contains(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>An answer's entities, with a profile's shared lists, as (id, name).</summary>
    private static IEnumerable<(string? Id, string? Name)> EntitiesOf(TasteResult r)
    {
        var shared = r.Profile?.GetValueOrDefault("domains") is object?[] domains
            ? domains.OfType<Dictionary<string, object?>>().SelectMany(d => d.GetValueOrDefault("shared") as object?[] ?? [])
            : [];
        return r.Entities.Concat(shared).OfType<Dictionary<string, object?>>()
            .Select(e => (e.GetValueOrDefault("id") as string, e.GetValueOrDefault("name") as string));
    }

    private static IEnumerable<string> TagsOf(TasteResult r) =>
        r.Tags.OfType<Dictionary<string, object?>>().Select(t => t.GetValueOrDefault("name") as string).OfType<string>();

    /// <summary>The Qloo rows of every Kind table, with the section each sits under and that section's lead handles.</summary>
    private static IEnumerable<Row> Rows(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string section = "";
        string[] lead = [];
        var leadState = Lead.Closed;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Heading.Match(line) is { Success: true } h)
            {
                section = h.Groups[2].Value.Trim('*', '_', ' ');
                lead = [];
                leadState = Lead.Waiting;
                continue;
            }

            if (!line.TrimStart().StartsWith('|'))
            {
                // The first paragraph under a heading names the profile a section rests on.
                var blank = line.Trim().Length == 0;
                if (!blank && leadState != Lead.Closed) { lead = [.. lead, .. Handles(line)]; leadState = Lead.Reading; }
                else if (blank && leadState == Lead.Reading) leadState = Lead.Closed;
                continue;
            }
            leadState = Lead.Closed;

            // A table: its header, then the separator, then rows until the first line that is not one.
            if (i + 1 >= lines.Length || !Separator.IsMatch(lines[i + 1])) continue;
            var header = Cells(line);
            var isKind = header.Count >= 2 && header[^1].Contains("kind", StringComparison.OrdinalIgnoreCase);
            i += 2;
            for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
            {
                if (!isKind) continue;
                var cells = Cells(lines[i]);
                if (cells.Count < 2 || !cells[^1].Contains("qloo", StringComparison.OrdinalIgnoreCase)) continue;

                var evidence = string.Join(" | ", cells.Take(cells.Count - 1).Skip(1));
                var choice = cells[0];
                yield return new Row(
                    section,
                    Space.Replace(choice.Replace("*", ""), " ").Trim(),
                    Handles(lines[i]),
                    lead.Distinct().ToArray(),
                    [.. Italics(choice + " " + evidence).Distinct()],
                    [.. QuotedTags(evidence).Distinct()]);
            }
            i--;
        }
    }

    private static string[] Handles(string text) =>
        [.. HandleRef.Matches(text).Select(m => "t" + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct()];

    /// <summary>Italic spans that are titles: not a quotation in italics, not a number.</summary>
    private static IEnumerable<string> Italics(string text) =>
        Italic.Matches(text).Select(m => m.Groups[1].Value.Trim())
            .Where(t => t.Length > 0 && !"\"“'‘".Contains(t[0]) && t.Any(char.IsLetter));

    private static IEnumerable<string> QuotedTags(string text) =>
        TagRef.Matches(text).Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim()).Where(t => t.Length > 0);

    /// <summary>A Markdown table row's cells, honouring <c>\|</c> and pipes inside backticks.</summary>
    private static List<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|")) text = text[..^1];

        var cells = new List<string>();
        var cell = new StringBuilder();
        var code = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|') { cell.Append('|'); i++; continue; }
            if (c == '`') code = !code;
            if (c == '|' && !code) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "…";
    #endregion

    #region Types
    private sealed record Row(string Section, string Choice, string[] Handles, string[] LeadHandles, string[] Titles, string[] Tags);

    private enum Lead { Waiting, Reading, Closed }
    #endregion

    #region Fields
    private static readonly Regex Heading = new(@"^(#{2,4})\s+(.*\S)\s*$", RegexOptions.Compiled);
    private static readonly Regex Separator = new(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex HandleRef = new(@"(?<![\w-])t(\d{1,3})(?![\w-])", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<!\*)\*([^*\n]{1,80})\*(?!\*)", RegexOptions.Compiled);

    /// <summary><c>**"Glitzy"**</c>, or <c>"Glitzy" (item 10)</c> / <c>(10)</c> / <c>(t6 item 10)</c>, bold or not.</summary>
    private static readonly Regex TagRef = new(
        @"\*\*[""“]([^""”\n]{1,40})[""”]\*\*|[""“]([^""”\n]{1,40})[""”]\**\s*\((?:t\d{1,3},?\s*)?(?:item\s*)?\d+\)", RegexOptions.Compiled);

    private static readonly Regex TrailingYear = new(@"\s*\((?:19|20)\d{2}\)\s*$", RegexOptions.Compiled);
    private static readonly Regex LeadingArticle = new(@"^(?:the|a|an)\s+", RegexOptions.Compiled);
    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);
    #endregion
}
