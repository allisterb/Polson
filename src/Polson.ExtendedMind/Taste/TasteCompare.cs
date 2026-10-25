namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Qloo's <c>/v2/analysis/compare</c> answer cut down to what an agent can read: one row per tag, with how many
/// entities on each side carry it. Not exposed to the sandbox; scripts keep the whole answer as
/// <c>Taste.get(id).comparison</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Qloo sends one row per tag per pairing.</b> Its <c>tags</c> are the tags both sets carry, but each row is one tag
/// with one subset of each side's entities, and a tag several entities on each side carry comes back once for every
/// pairing. Measured on poster5 (2026-10-24), seven artists against seven: 404 rows for 74 distinct names, 358,332
/// characters, which the host refused to deliver at all. Its <c>a</c> and <c>b</c> list each side's tags with a
/// count. Here every tag is one row, counted on each side from all three — the larger count where they disagree, as
/// they did for "Electronic" (2 in <c>a</c>, 4 entities in the pairings).
/// </para>
/// <para>
/// <b>What separates the sets is the difference in those counts, not Qloo's score.</b> The shared rows' scores sat
/// between 0.50 and 0.73 for every tag in both answers measured, so they are reported as a range, not a ranking. A tag
/// on both lists can still lean hard one way — "Hip - Hop" on all five of one side and one of the other — so the
/// leaning lists rank by the difference in share, as <see cref="TasteContrast"/> does for titles' tags.
/// </para>
/// </remarks>
public static class TasteCompare
{
    #region Methods
    /// <summary>
    /// The compact form: <c>{ entities, rows, tags, tagsA, tagsB, shared, omitted, scoreRange, note }</c>, each list at most
    /// <paramref name="take"/> long. Every tag is <c>{ id, name, kind, a, b }</c>, <c>a</c> and <c>b</c> how many entities
    /// on each side carry it. <c>tagsA</c>/<c>tagsB</c>: carried by a larger share of that side, on at least two of its
    /// entities, most leaning first; <c>shared</c>: carried by both, most widely first.
    /// </summary>
    /// <param name="results">Qloo's <c>results</c> object.</param>
    /// <param name="take">Most rows per list.</param>
    /// <param name="handle">The answer's id, named in the note as where the whole answer is.</param>
    /// <param name="sizeA">Entities asked about on side A; 0 to infer from the answer.</param>
    /// <param name="sizeB">Entities asked about on side B; 0 to infer from the answer.</param>
    public static Dictionary<string, object?> Summary(JsonElement results, int take, string handle, int sizeA = 0, int sizeB = 0)
    {
        take = Math.Max(1, take);
        var whole = $"Taste.get('{handle}').comparison";
        if (results.ValueKind != JsonValueKind.Object || !results.TryGetProperty("tags", out var tagsRaw) || tagsRaw.ValueKind != JsonValueKind.Array)
        {
            // An unfamiliar shape is described, never pasted: pasted, it was the 358 KB reply this exists to prevent.
            return new()
            {
                ["shape"] = results.ValueKind == JsonValueKind.Object
                    ? results.EnumerateObject().ToDictionary(p => p.Name, p => (object?)Describe(p.Value))
                    : Describe(results),
                ["note"] = $"Qloo's comparison came back in a shape this tool does not summarise. The whole answer is {whole}.",
            };
        }

        var rows = tagsRaw.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToArray();
        var tags = new Dictionary<string, Tag>(StringComparer.Ordinal);
        Tag Of(JsonElement r)
        {
            var id = Text(r, "tag_id") ?? Text(r, "id") ?? Text(r, "name") ?? "?";
            if (!tags.TryGetValue(id, out var tag))
                tags[id] = tag = new Tag(id, Text(r, "name") ?? id, Kind(r), Number(r, "popularity") ?? 0);
            return tag;
        }

        var seenA = new HashSet<string>(StringComparer.Ordinal);
        var seenB = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var tag = Of(r);
            foreach (var e in Signals(r, "a.signal.interests.entities")) { tag.PairedA.Add(e); seenA.Add(e); }
            foreach (var e in Signals(r, "b.signal.interests.entities")) { tag.PairedB.Add(e); seenB.Add(e); }
        }
        foreach (var (side, isA) in new[] { ("a", true), ("b", false) })
        {
            if (!results.TryGetProperty(side, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var r in list.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
            {
                var tag = Of(r);
                var count = r.TryGetProperty("query", out var q) ? (int)(Number(q, "count") ?? 0) : 0;
                if (isA) tag.ListedA = Math.Max(tag.ListedA, count); else tag.ListedB = Math.Max(tag.ListedB, count);
            }
        }

        var all = tags.Values.ToArray();
        var nA = sizeA > 0 ? sizeA : Math.Max(seenA.Count, all.Select(t => t.A).DefaultIfEmpty(0).Max());
        var nB = sizeB > 0 ? sizeB : Math.Max(seenB.Count, all.Select(t => t.B).DefaultIfEmpty(0).Max());
        double Lean(Tag t) => t.A / (double)Math.Max(1, nA) - t.B / (double)Math.Max(1, nB);

        var tagsA = all.Where(t => t.A >= Math.Min(2, nA) && Lean(t) > 0)
            .OrderByDescending(Lean).ThenByDescending(t => t.A).ThenByDescending(t => t.Popularity).ThenBy(t => t.Name, StringComparer.Ordinal).ToArray();
        var tagsB = all.Where(t => t.B >= Math.Min(2, nB) && Lean(t) < 0)
            .OrderBy(Lean).ThenByDescending(t => t.B).ThenByDescending(t => t.Popularity).ThenBy(t => t.Name, StringComparer.Ordinal).ToArray();
        var shared = all.Where(t => t.A > 0 && t.B > 0)
            .OrderByDescending(t => Math.Min(t.A / (double)Math.Max(1, nA), t.B / (double)Math.Max(1, nB)))
            .ThenByDescending(t => t.A + t.B).ThenByDescending(t => t.Popularity).ThenBy(t => t.Name, StringComparer.Ordinal).ToArray();

        var scores = rows.Select(r => r.TryGetProperty("query", out var q) ? Number(q, "score") : null).OfType<double>().ToArray();
        var omitted = new Dictionary<string, object?>
        {
            ["tagsA"] = Math.Max(0, tagsA.Length - take),
            ["tagsB"] = Math.Max(0, tagsB.Length - take),
            ["shared"] = Math.Max(0, shared.Length - take),
        };
        var cut = omitted.Values.Any(v => v is int n && n > 0);

        return new()
        {
            ["entities"] = new Dictionary<string, object?> { ["a"] = nA, ["b"] = nB },
            ["rows"] = rows.Length,
            ["tags"] = all.Length,
            ["tagsA"] = tagsA.Take(take).Select(Row).ToArray(),
            ["tagsB"] = tagsB.Take(take).Select(Row).ToArray(),
            ["shared"] = shared.Take(take).Select(Row).ToArray(),
            ["omitted"] = omitted,
            ["scoreRange"] = scores.Length == 0 ? null : new object?[] { Math.Round(scores.Min(), 3), Math.Round(scores.Max(), 3) },
            ["note"] = $"One row per tag, a and b = how many of the {nA} and {nB} entities on each side carry it (Qloo sent "
                     + $"{rows.Length} pairing rows). What SEPARATES the sets is tagsA and tagsB: tags a larger share of one side "
                     + "carries, most leaning first. shared: tags both carry, most widely first. "
                     + (scores.Length == 0 ? "" : $"Qloo's scores all fall between {scores.Min():0.00} and {scores.Max():0.00}, so they rank nothing. ")
                     + (cut ? $"Lists stop at {take} (take); 'omitted' says how many more each has. " : "")
                     + $"The whole answer is {whole}.",
        };
    }
    #endregion

    #region Private methods
    /// <summary>One tag, counted on each side from the pairing rows and from that side's own list.</summary>
    private sealed class Tag(string id, string name, string? kind, double popularity)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string? Kind { get; } = kind;
        public double Popularity { get; } = popularity;
        public HashSet<string> PairedA { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PairedB { get; } = new(StringComparer.Ordinal);
        public int ListedA { get; set; }
        public int ListedB { get; set; }
        public int A => Math.Max(PairedA.Count, ListedA);
        public int B => Math.Max(PairedB.Count, ListedB);
    }

    private static object? Row(Tag t) =>
        new Dictionary<string, object?> { ["id"] = t.Id, ["name"] = t.Name, ["kind"] = t.Kind, ["a"] = t.A, ["b"] = t.B };

    /// <summary>A tag's kind without its namespace: <c>urn:tag:genre:music</c> is <c>genre:music</c>, <c>urn:tag:style:qloo</c> is <c>style</c>.</summary>
    private static string? Kind(JsonElement row) =>
        (Text(row, "subtype") ?? Text(row, "type")) is { } urn
            ? urn.Replace("urn:tag:", "", StringComparison.Ordinal) is var k && k.EndsWith(":qloo", StringComparison.Ordinal) ? k[..^5] : k
            : null;

    private static IEnumerable<string> Signals(JsonElement row, string side) =>
        row.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Object && q.TryGetProperty(side, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => Text(e, "entity_id")).OfType<string>()
            : [];

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            }
            : null;

    private static string Describe(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => $"{e.GetArrayLength()} items",
        JsonValueKind.Object => $"object of {e.EnumerateObject().Count()} fields",
        var kind => kind.ToString().ToLowerInvariant(),
    };
    #endregion
}
