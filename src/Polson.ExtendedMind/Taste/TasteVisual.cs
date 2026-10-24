namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Visual research over a set of Qloo pictures: where their colours crowd and where the wheel is empty, and which
/// words recur in how Qloo describes their look.
/// </summary>
/// <remarks>
/// Lupton's <i>Visual Research</i> (<i>Graphic Design Thinking</i>, pp. 38–41, after 2x4): collect a brand space,
/// visualise one aspect of it, and read off where the field is crowded and where a newcomer could stand apart —
/// 2x4's <i>Battle for Blue</i> found the corporate palette packed into blue. These are the measuring halves of
/// that method; the collecting is <see cref="TasteToolkit.Images"/> and the reading is the agent's.
/// </remarks>
public static partial class TasteVisual
{
    #region Properties
    /// <summary>
    /// Approximate names for the twelve 30° sectors of the OKLCH hue wheel, from 0°. OKLCH's hues are not HSL's:
    /// pure sRGB red sits near 29°, yellow near 110°, blue near 264°.
    /// </summary>
    public static readonly string[] SectorNames =
        ["pink-red", "red-orange", "orange", "yellow", "yellow-green", "green", "teal", "cyan", "blue", "violet", "purple", "magenta"];
    #endregion

    #region Methods
    /// <summary>
    /// Where a set of pictures' lead hues fall on the wheel: <c>{ n, neutral, sectors, crowded, open, light, dark, summary }</c>.
    /// </summary>
    /// <remarks>
    /// A picture counts in the sector of its lead hue, or as neutral when it has none (a black-on-white logo). Each
    /// sector is <c>{ from, to, name, count, names }</c>; <c>crowded</c> holds the fullest sector or sectors when two or
    /// more pictures share one, <c>open</c> the sectors no picture is in. <c>light</c> and <c>dark</c> count pictures
    /// whose mean lightness is over 0.7 or under 0.4.
    /// </remarks>
    public static Dictionary<string, object?> ColourField(IReadOnlyList<TasteImage> images)
    {
        var measured = images.Where(i => i.Success).ToArray();
        var sectors = Enumerable.Range(0, 12).Select(s => (From: s * 30, Names: new List<string>())).ToArray();
        var neutral = new List<string>();
        foreach (var image in measured)
        {
            if (image.Lead?.GetValueOrDefault("hue") is double hue) sectors[(int)(hue / 30) % 12].Names.Add(image.Name);
            else neutral.Add(image.Name);
        }

        var max = sectors.Max(s => s.Names.Count);
        var crowded = max >= 2 ? sectors.Where(s => s.Names.Count == max).ToArray() : [];
        var open = sectors.Where(s => s.Names.Count == 0).ToArray();
        string Label((int From, List<string> Names) s) => $"{SectorNames[s.From / 30]} {s.From}–{s.From + 30}°";

        var parts = new List<string> { $"{measured.Length} pictures" };
        if (neutral.Count > 0) parts.Add($"{neutral.Count} neutral ({string.Join(", ", neutral.Take(4))}{(neutral.Count > 4 ? ", …" : "")})");
        if (crowded.Length > 0)
            parts.Add("crowded in " + string.Join(" and ", crowded.Select(s => $"{Label(s)} ({s.Names.Count}: {string.Join(", ", s.Names.Take(4))})")));
        parts.Add($"{open.Length} of 12 hue sectors empty" + (open.Length > 0 ? $", e.g. {string.Join(", ", open.Take(4).Select(s => SectorNames[s.From / 30]))}" : ""));

        return new()
        {
            ["n"] = measured.Length,
            ["neutral"] = new Dictionary<string, object?> { ["count"] = neutral.Count, ["names"] = neutral.ToArray() },
            ["sectors"] = sectors.Select(s => (object?)new Dictionary<string, object?>
            {
                ["from"] = s.From, ["to"] = s.From + 30, ["name"] = SectorNames[s.From / 30],
                ["count"] = s.Names.Count, ["names"] = s.Names.ToArray(),
            }).ToArray(),
            ["crowded"] = crowded.Select(s => (object?)Label(s)).ToArray(),
            ["open"] = open.Select(s => (object?)Label(s)).ToArray(),
            ["light"] = measured.Count(i => i.Lightness > 0.7),
            ["dark"] = measured.Count(i => i.Lightness < 0.4),
            ["summary"] = string.Join("; ", parts) + ".",
        };
    }

    /// <summary>
    /// The words that recur across Qloo's style descriptions: <c>[{ word, count, share, names }]</c>, most common
    /// first, each word counted once per entity.
    /// </summary>
    /// <remarks>
    /// Qloo's descriptions are its own prose about each entity's look, not a measurement, and they share a house
    /// vocabulary: "clean" and "modern" recur because the writer favours them as well as because the brands do.
    /// A word the whole set shares is still worth reading as what the field already claims.
    /// </remarks>
    public static object?[] Vocabulary(IEnumerable<(string Name, string? Text)> described, int take = 20, int min = 2)
    {
        var sets = described.Where(d => !string.IsNullOrWhiteSpace(d.Text)).ToArray();
        return [.. sets
            .SelectMany(d => Word().Matches(d.Text!.ToLowerInvariant()).Select(m => m.Value)
                .Where(w => !Stop.Contains(w)).Distinct().Select(w => (Word: w, d.Name)))
            .GroupBy(p => p.Word)
            .Select(g => (Word: g.Key, Names: g.Select(p => p.Name).Distinct().ToArray()))
            .Where(g => g.Names.Length >= Math.Max(1, min))
            .OrderByDescending(g => g.Names.Length).ThenBy(g => g.Word, StringComparer.Ordinal)
            .Take(Math.Clamp(take, 1, 100))
            .Select(g => (object?)new Dictionary<string, object?>
            {
                ["word"] = g.Word, ["count"] = g.Names.Length,
                ["share"] = Math.Round((double)g.Names.Length / sets.Length, 3), ["names"] = g.Names,
            })];
    }
    #endregion

    #region Fields
    [GeneratedRegex(@"\p{L}[\p{L}'-]{3,}")]
    private static partial Regex Word();

    // Function words, and the description's own scaffolding ("the brand features an aesthetic that…").
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "about", "across", "against", "along", "also", "among", "an", "and", "that", "this", "these", "those", "with",
        "within", "without", "from", "into", "onto", "over", "under", "while", "where", "which", "whose", "their", "them",
        "they", "there", "then", "than", "through", "throughout", "both", "each", "every", "such", "very", "more", "most",
        "often", "between", "beyond", "around", "upon", "yet", "its", "it's", "has", "have", "having", "been", "being",
        "brand", "brand's", "film", "film's", "show", "visual", "visuals", "aesthetic", "aesthetics", "design", "style",
        "features", "featuring", "characterized", "defined", "utilizes", "utilizing", "uses", "using", "emphasizes",
        "emphasizing", "combines", "combining", "reflects", "reflecting", "highlights", "highlighting", "creates",
        "creating", "balances", "balancing", "blends", "blending", "identity", "approach", "elements", "overall",
        // A film's description is about its making as much as its look: over Berlin's sixteen films, "production",
        // "cinematography", "create" and "moments" outranked every word about colour or mood.
        "cinematography", "cinematic", "production", "create", "moments", "compositions", "composition", "framing",
        "editing", "camera", "sequences", "scenes", "imagery", "storytelling", "narrative", "performances",
        "heighten", "heightens", "heightened", "emphasize", "underscore", "underscores", "evoke", "evokes", "evoking",
        "capture", "captures", "convey", "conveys", "highly", "pairs", "paired", "pairing",
    };
    #endregion
}
