namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// What separates two audiences: <c>Taste.contrast(a, b)</c> over two profiles, with no further Qloo call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership and rank, not affinity.</b> A profile's distinctive list is what an audience loves that its place's
/// baseline does not list, each with its rank in the audience's own answer. Two such lists split three ways: titles
/// distinctive to both (<c>shared</c> — what a set of pieces has in common), and titles distinctive to one only
/// (<c>onlyA</c>, <c>onlyB</c> — what each piece's evidence is). Affinity is not compared: each profile is its own
/// query, and measured, Qloo's affinity saturates near the top of any list, so two lists' affinities say less
/// than which titles are on them.
/// </para>
/// <para>
/// <b>Tags are weak evidence and are given with their counts.</b> For each side, the tags its own titles carry more
/// often than the other side's do, with how many titles carry each and which. Measured, two halves of one
/// audience's titles shared none of their tags, so a tag carried by one or two titles is a lead, not a finding.
/// </para>
/// </remarks>
public static class TasteContrast
{
    #region Methods
    /// <summary>
    /// Two profiles compared: <c>{ a, b, domains, pace, summary }</c>. Each domain is <c>{ type, nA, nB, shared, onlyA,
    /// onlyB, overlap, tagsA, tagsB }</c>; a shared title is <c>{ id, name, rankA, rankB }</c>, a one-sided one <c>{ id, name,
    /// rank }</c>, and a tag <c>{ tag, a, b, titles }</c>.
    /// </summary>
    public static Dictionary<string, object?> Compare(TasteResult a, TasteResult b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        foreach (var r in new[] { a, b })
        {
            if (r.Op != "profile")
                throw new ArgumentException($"Taste.contrast needs two profiles; {r.Id} is a '{r.Op}'. Fetch each audience with the Taste tool, op 'profile'.");
            if (!r.Ok) throw new ArgumentException($"Taste.contrast: {r.Id} failed ({r.Error}), so there is nothing to compare.");
        }

        var domainsA = Domains(a);
        var domainsB = Domains(b);
        var domains = domainsA.Keys.Union(domainsB.Keys).Select(type =>
        {
            var listA = domainsA.GetValueOrDefault(type) ?? [];
            var listB = domainsB.GetValueOrDefault(type) ?? [];
            var byIdB = listB.ToDictionary(e => e.Id, StringComparer.Ordinal);
            var idsA = listA.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

            var shared = listA.Where(e => byIdB.ContainsKey(e.Id))
                .Select(e => (e, other: byIdB[e.Id])).OrderBy(x => x.e.Rank + x.other.Rank)
                .Select(x => (object?)new Dictionary<string, object?> { ["id"] = x.e.Id, ["name"] = x.e.Name, ["rankA"] = x.e.Rank, ["rankB"] = x.other.Rank })
                .ToArray();
            var onlyA = listA.Where(e => !byIdB.ContainsKey(e.Id)).OrderBy(e => e.Rank).ToArray();
            var onlyB = listB.Where(e => !idsA.Contains(e.Id)).OrderBy(e => e.Rank).ToArray();
            var union = listA.Count + listB.Count - shared.Length;

            return (object?)new Dictionary<string, object?>
            {
                ["type"] = type,
                ["nA"] = listA.Count,
                ["nB"] = listB.Count,
                ["shared"] = shared,
                ["onlyA"] = onlyA.Select(Ranked).ToArray(),
                ["onlyB"] = onlyB.Select(Ranked).ToArray(),
                ["overlap"] = union == 0 ? null : Math.Round(shared.Length / (double)union, 3),
                ["tagsA"] = Leaning(onlyA, onlyB),
                ["tagsB"] = Leaning(onlyB, onlyA),
            };
        }).ToArray();

        var pace = TastePace.Compare(a.Pace(), b.Pace(), Named(a), Named(b));

        // Each count with the first titles behind it, so the summary alone says which titles are whose. A count on its
        // own read as nothing to cite, and the titles live a level down, under domains[i] (poster5 looked for c.onlyA).
        static string Titles(object? list) => ((object?[])list!).Cast<Dictionary<string, object?>>().Take(3)
            .Select(e => e["name"] as string ?? "?").ToArray() is { Length: > 0 } names
                ? $" ({string.Join(", ", names)}{(((object?[])list!).Length > 3 ? ", …" : "")})"
                : "";
        var counts = domains.Cast<Dictionary<string, object?>>().Select(d =>
            $"{Short((string)d["type"]!)}: {((object?[])d["shared"]!).Length} shared{Titles(d["shared"])}, "
            + $"{((object?[])d["onlyA"]!).Length} only {a.Id}{Titles(d["onlyA"])}, {((object?[])d["onlyB"]!).Length} only {b.Id}{Titles(d["onlyB"])}");

        return new Dictionary<string, object?>
        {
            ["a"] = new Dictionary<string, object?> { ["id"] = a.Id, ["label"] = a.Label },
            ["b"] = new Dictionary<string, object?> { ["id"] = b.Id, ["label"] = b.Label },
            ["domains"] = domains,
            ["pace"] = pace,
            // Pace first: it is the one measured difference here, with a test behind it, and poster5 found it only by
            // dumping Object.keys — it was the last clause of a long summary, after the counts.
            ["summary"] = $"Pace — {pace["summary"]} Titles — {string.Join("; ", counts)}. "
                        + "The lists are per domain: c.domains[i].shared, .onlyA, .onlyB, .tagsA, .tagsB; the pace counts and titles are c.pace.a and c.pace.b.",
        };
    }
    #endregion

    #region Private methods
    private sealed record Item(string Id, string Name, int Rank, string[] Tags);

    /// <summary>Each domain's distinctive titles, by type.</summary>
    private static Dictionary<string, List<Item>> Domains(TasteResult r) =>
        (r.Profile?.GetValueOrDefault("domains") as object?[] ?? []).OfType<Dictionary<string, object?>>()
            .Where(d => d.GetValueOrDefault("type") is string)
            .ToDictionary(d => (string)d["type"]!, d => (d.GetValueOrDefault("distinctive") as object?[] ?? [])
                .OfType<Dictionary<string, object?>>()
                .Where(e => e.GetValueOrDefault("id") is string)
                .Select(e => new Item(
                    (string)e["id"]!,
                    e.GetValueOrDefault("name") as string ?? "?",
                    e.GetValueOrDefault("audienceRank") is int rank ? rank : int.MaxValue,
                    [.. (e.GetValueOrDefault("tags") as object?[] ?? []).OfType<Dictionary<string, object?>>()
                        .Select(t => t.GetValueOrDefault("name") as string).OfType<string>().Distinct()]))
                .ToList());

    private static object? Ranked(Item e) =>
        new Dictionary<string, object?> { ["id"] = e.Id, ["name"] = e.Name, ["rank"] = e.Rank };

    /// <summary>
    /// The tags <paramref name="side"/>'s titles carry more often than <paramref name="other"/>'s, as shares of each
    /// side, at least two titles: <c>{ tag, a, b, titles }</c> with <c>a</c> this side's count and <c>b</c> the other's.
    /// </summary>
    private static object?[] Leaning(IReadOnlyList<Item> side, IReadOnlyList<Item> other)
    {
        if (side.Count == 0) return [];
        var otherCounts = other.SelectMany(e => e.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return [.. side.SelectMany(e => e.Tags.Select(t => (Tag: t, Title: e.Name)))
            .GroupBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.First().Tag, Mine: g.Count(), Theirs: otherCounts.GetValueOrDefault(g.Key), Titles: g.Select(x => x.Title).ToArray()))
            .Where(x => x.Mine >= 2 && x.Mine / (double)side.Count > x.Theirs / (double)Math.Max(other.Count, 1))
            .OrderByDescending(x => x.Mine / (double)side.Count - x.Theirs / (double)Math.Max(other.Count, 1))
            .ThenBy(x => x.Tag, StringComparer.Ordinal)
            .Take(8)
            .Select(x => (object?)new Dictionary<string, object?>
            {
                ["tag"] = x.Tag, ["a"] = x.Mine, ["b"] = x.Theirs, ["titles"] = x.Titles.Take(4).ToArray(),
            })];
    }

    private static string Short(string urn) => urn.StartsWith("urn:entity:", StringComparison.Ordinal) ? urn["urn:entity:".Length..] : urn;

    private static string Named(TasteResult r) => string.IsNullOrWhiteSpace(r.Label) ? r.Id : $"{r.Id} ({r.Label})";
    #endregion
}
