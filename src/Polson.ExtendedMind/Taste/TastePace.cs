namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// How an audience's films and shows pace themselves, in Qloo's own tags, and whether two audiences differ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Qloo describes pace; it does not measure cutting.</b> Its film and TV entities carry plot and style tags such as
/// "Fast paced", "Action packed", "Kinetic" and "Slow burn". Counted over what an audience distinctively loves, they
/// say whether its screen taste leans fast or slow. That <i>motivates</i> a trailer's holds; it does not show this audience
/// prefers them. The vocabulary is the one found in Qloo's answers on 2026-10-17
/// (<c>docs/internal/audience-tempo-measurement.md</c>): every tag here occurred, and none was invented.
/// </para>
/// <para>
/// Measured on the <i>Be Cool</i> audiences: Tokyo 45–54's distinctive films and shows, 14 fast and 12 slow; Mumbai's,
/// 23 and 5; Fisher's exact p = 0.040. The <i>Be Cool</i> fans in both cities love the same fast Hollywood comedies, and their
/// music runs at the same ~100 BPM, so the beat was shared and the holds were not.
/// </para>
/// </remarks>
public static class TastePace
{
    #region Properties
    /// <summary>Qloo's tags for fast pacing, as they occur in its answers.</summary>
    public static IReadOnlyList<string> Fast { get; } = ["Fast paced", "Action packed", "Kinetic", "Frenetic", "Frenetic montage"];

    /// <summary>Qloo's tags for slow pacing, as they occur in its answers.</summary>
    public static IReadOnlyList<string> Slow { get; } =
        ["Slow burn", "Slow burn pacing", "Meditative pacing", "Deliberate pacing", "Deliberate quiet pacing"];
    #endregion

    #region Methods
    /// <summary>
    /// The pace of the films and shows among <paramref name="entities"/> (other types are skipped): <c>{ fast, slow,
    /// both, untagged, n, fastShare, fastTitles, slowTitles, vocabulary }</c>. An entity counts as fast or slow when it
    /// carries tags of one kind only; <c>fastShare</c> is fast over fast plus slow, or null when neither occurs.
    /// </summary>
    public static Dictionary<string, object?> Count(IEnumerable<object?> entities)
    {
        var (fast, slow) = (Set(Fast), Set(Slow));
        List<string> fastTitles = [], slowTitles = [], bothTitles = [];
        var (n, untagged) = (0, 0);
        foreach (var e in entities.OfType<IDictionary<string, object?>>())
        {
            if (!IsScreen(e)) continue;
            n++;
            var tags = (e.TryGetValue("tags", out var t) ? t as IEnumerable<object?> : null)?
                .OfType<IDictionary<string, object?>>().Select(x => x.TryGetValue("name", out var v) ? v as string : null)
                .OfType<string>().Select(s => s.Trim().ToLowerInvariant()).ToHashSet() ?? [];
            var (isFast, isSlow) = (tags.Overlaps(fast), tags.Overlaps(slow));
            var name = e.TryGetValue("name", out var nm) ? nm as string ?? "?" : "?";
            if (isFast && isSlow) bothTitles.Add(name);
            else if (isFast) fastTitles.Add(name);
            else if (isSlow) slowTitles.Add(name);
            else untagged++;
        }
        var decided = fastTitles.Count + slowTitles.Count;
        return new()
        {
            ["fast"] = fastTitles.Count,
            ["slow"] = slowTitles.Count,
            ["both"] = bothTitles.Count,
            ["untagged"] = untagged,
            ["n"] = n,
            ["fastShare"] = decided == 0 ? null : Math.Round(fastTitles.Count / (double)decided, 3),
            ["fastTitles"] = fastTitles.ToArray(),
            ["slowTitles"] = slowTitles.ToArray(),
            ["vocabulary"] = new Dictionary<string, object?> { ["fast"] = Fast.ToArray(), ["slow"] = Slow.ToArray() },
        };
    }

    /// <summary>
    /// Two audiences' pace side by side, and Fisher's exact test on their fast and slow counts: <c>{ a, b, p, differ,
    /// summary }</c>, <c>differ</c> when p &lt; 0.05. Small counts make a real difference hard to show, so "no measurable
    /// difference" is an answer, not a failure: say so rather than inventing one.
    /// </summary>
    public static Dictionary<string, object?> Compare(Dictionary<string, object?> a, Dictionary<string, object?> b, string nameA, string nameB)
    {
        int I(Dictionary<string, object?> d, string k) => d[k] is int i ? i : 0;
        var p = FisherExact(I(a, "fast"), I(a, "slow"), I(b, "fast"), I(b, "slow"));
        var differ = p < 0.05;
        string Lean(Dictionary<string, object?> d) => d["fastShare"] is double s ? $"{s:P0} fast ({I(d, "fast")} fast, {I(d, "slow")} slow)" : "no pace tags";
        return new()
        {
            ["a"] = a,
            ["b"] = b,
            ["p"] = Math.Round(p, 4),
            ["differ"] = differ,
            ["summary"] = $"{nameA}: {Lean(a)}; {nameB}: {Lean(b)}; Fisher's exact p = {p:0.000}" +
                (differ ? ", a measurable difference." : ", no measurable difference at these counts."),
        };
    }

    /// <summary>
    /// Several audiences' pace: each one's lean, every pair under Fisher's exact test, and the pairs that differ once the
    /// p-values are adjusted for how many pairs there are (Holm): <c>{ audiences, pairs, differ, summary }</c>.
    /// </summary>
    /// <remarks>
    /// The adjustment is the point of having this rather than calling <see cref="Compare"/> in a loop. Six audiences make
    /// fifteen pairs, and at p &lt; 0.05 one in twenty pairs with no real difference still comes out "different": unadjusted,
    /// a set of six would usually show a difference that is not there, and a trailer would be paced on it. Holm keeps the
    /// chance of any false difference across the whole set at 0.05, and is never weaker than testing one pair.
    /// </remarks>
    public static Dictionary<string, object?> CompareAll(IReadOnlyList<(Dictionary<string, object?> Pace, string Name)> audiences)
    {
        int I(Dictionary<string, object?> d, string k) => d[k] is int i ? i : 0;

        var pairs = new List<(int A, int B, double P)>();
        for (var i = 0; i < audiences.Count; i++)
            for (var j = i + 1; j < audiences.Count; j++)
                pairs.Add((i, j, FisherExact(I(audiences[i].Pace, "fast"), I(audiences[i].Pace, "slow"),
                                             I(audiences[j].Pace, "fast"), I(audiences[j].Pace, "slow"))));

        // Holm: the k-th smallest p is multiplied by (m - k), and the adjusted values are kept non-decreasing.
        var adjusted = new double[pairs.Count];
        var order = Enumerable.Range(0, pairs.Count).OrderBy(k => pairs[k].P).ToArray();
        var running = 0d;
        for (var rank = 0; rank < order.Length; rank++)
        {
            running = Math.Max(running, Math.Min(1, (pairs.Count - rank) * pairs[order[rank]].P));
            adjusted[order[rank]] = running;
        }

        var rows = pairs.Select((pair, k) => new Dictionary<string, object?>
        {
            ["a"] = audiences[pair.A].Name,
            ["b"] = audiences[pair.B].Name,
            ["p"] = Math.Round(pair.P, 4),
            ["pAdjusted"] = Math.Round(adjusted[k], 4),
            ["differ"] = adjusted[k] < 0.05,
        }).ToList();

        var leans = audiences.Select(x => new Dictionary<string, object?>
        {
            ["name"] = x.Name, ["fast"] = I(x.Pace, "fast"), ["slow"] = I(x.Pace, "slow"), ["fastShare"] = x.Pace["fastShare"],
        }).ToList();

        string Lean((Dictionary<string, object?> Pace, string Name) x) =>
            x.Pace["fastShare"] is double s ? $"{x.Name} {s:P0} fast" : $"{x.Name} no pace tags";
        var differing = rows.Where(r => r["differ"] is true).ToList();
        var summary = string.Join("; ", audiences.Select(Lean)) + ". " + (differing.Count == 0
            ? $"No pair differs measurably at these counts ({rows.Count} pairs, Holm-adjusted)."
            : "Measurably different: " + string.Join(", ", differing.Select(r => $"{r["a"]} vs {r["b"]} (adjusted p = {r["pAdjusted"]:0.000})"))
              + $" ({differing.Count} of {rows.Count} pairs, Holm-adjusted).");

        return new()
        {
            ["audiences"] = leans,
            ["pairs"] = rows,
            ["differ"] = differing.Count > 0,
            ["summary"] = summary,
        };
    }

    /// <summary>Two-sided Fisher's exact test on the table [[a, b], [c, d]]: the probability of a table at least as extreme.</summary>
    internal static double FisherExact(int a, int b, int c, int d)
    {
        var (n, row, col) = (a + b + c + d, a + b, a + c);
        if (n == 0 || row == 0 || col == 0 || row == n || col == n) return 1;
        double LogChoose(int k, int r) => LogFactorial(k) - LogFactorial(r) - LogFactorial(k - r);
        double P(int x) => Math.Exp(LogChoose(row, x) + LogChoose(n - row, col - x) - LogChoose(n, col));
        var observed = P(a);
        var total = 0d;
        for (var x = Math.Max(0, col - (n - row)); x <= Math.Min(row, col); x++)
            if (P(x) <= observed * (1 + 1e-9)) total += P(x);
        return Math.Min(1, total);
    }

    private static double LogFactorial(int k)
    {
        var s = 0d;
        for (var i = 2; i <= k; i++) s += Math.Log(i);
        return s;
    }

    private static bool IsScreen(IDictionary<string, object?> e)
    {
        var types = (e.TryGetValue("types", out var ts) ? ts as IEnumerable<object?> : null)?.OfType<string>()
            ?? (e.TryGetValue("type", out var t) && t is string one ? [one] : []);
        return types.Any(x => x is "urn:entity:movie" or "urn:entity:tv_show");
    }

    private static HashSet<string> Set(IEnumerable<string> tags) => [.. tags.Select(t => t.ToLowerInvariant())];
    #endregion
}
