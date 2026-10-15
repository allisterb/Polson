namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// What Qloo's Insights API accepts, and the check run on every query before it is sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Qloo ignores a parameter it does not support and answers 200 with no results</b> (its developer
/// guide, and our own probes). A typo therefore reads as "this audience likes nothing", which this project
/// refuses to let a parse failure mean. So a query is checked here first:
/// </para>
/// <list type="bullet">
/// <item>An unknown name, a value outside a documented set, or an unknown <c>filter.type</c> is
/// <b>refused</b> — these are certainly wrong.</item>
/// <item>A known parameter that the Entity Type Parameter Guide does not list for this
/// <c>filter.type</c> is a <b>warning</b>, not a refusal, because the guide is incomplete: it lists no
/// <c>signal.location.query</c> for movies or artists, yet the API applies it (Lagos, 2026-10-15).</item>
/// </list>
/// <para>
/// Sources, read 2026-10-15: the Parameter Reference (Filters, Signals and Output tabs), the Entity Type
/// Parameter Guide, and the parameters Qloo's own MIT-licensed harness sends (<c>@qloo/qloo-harness</c>
/// 0.1.26, <c>dist/qloo-tools.js</c>) — weights and trending dates appear only there.
/// </para>
/// </remarks>
public static class QlooParameters
{
    #region Properties
    /// <summary>The <c>filter.type</c> values the Insights API accepts with a hackathon key.</summary>
    public static IReadOnlyList<string> InsightsTypes { get; } =
    [
        "urn:entity:artist", "urn:entity:book", "urn:entity:brand", "urn:entity:destination",
        "urn:entity:movie", "urn:entity:person", "urn:entity:place", "urn:entity:podcast",
        "urn:entity:tv_show", "urn:entity:video_game", "urn:tag", "urn:demographics", "urn:heatmap",
    ];

    /// <summary>The six bands <c>signal.demographics.age</c> accepts.</summary>
    public static IReadOnlyList<string> AgeBands { get; } =
        ["24_and_younger", "25_to_29", "30_to_34", "35_to_44", "45_to_54", "55_and_older"];

    public static IReadOnlyList<string> Genders { get; } = ["male", "female"];

    /// <summary>Every parameter name the Insights API documents or Qloo's own client sends.</summary>
    public static IReadOnlySet<string> Known { get; } = BuildKnown();
    #endregion

    #region Methods
    /// <summary>
    /// Checks an Insights query. Returns the reason to refuse it — null when it may be sent — and the
    /// warnings to carry with the response either way.
    /// </summary>
    public static (string? Refusal, IReadOnlyList<string> Warnings) CheckInsights(QlooQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var warnings = new List<string>();

        if (query["filter.type"] is not string type)
            return ($"An Insights query needs filter.type — one of {string.Join(", ", InsightsTypes)}.", warnings);

        if (!InsightsTypes.Contains(type))
        {
            return ($"filter.type '{type}' is not one Qloo accepts.{Suggest(type, InsightsTypes)} "
                  + $"Accepted: {string.Join(", ", InsightsTypes)}. (Music is urn:entity:artist; food and "
                  + "drink is urn:entity:place or urn:entity:brand.)", warnings);
        }

        foreach (var name in query.Names)
        {
            if (!Known.Contains(name))
            {
                return ($"'{name}' is not a Qloo Insights parameter, and Qloo would ignore it silently and "
                      + $"answer with no results.{Suggest(name, Known)}", warnings);
            }
        }

        if (CheckValues(query) is string bad) return (bad, warnings);

        if (type == "urn:demographics" && !query.Has("signal.interests.entities") && !query.Has("signal.interests.tags"))
            return ("filter.type urn:demographics needs signal.interests.entities or signal.interests.tags — the "
                  + "entity or tag whose audience to describe.", warnings);

        if (type == "urn:entity:destination" && !query.Has("signal.interests.entities"))
            return ("filter.type urn:entity:destination needs signal.interests.entities.", warnings);

        var restricted = query.Names.Where(n => IsTypeSpecific(n) && !AllowedFor(type).Contains(n)).ToArray();
        if (restricted.Length > 0)
        {
            warnings.Add($"{string.Join(", ", restricted)} {(restricted.Length == 1 ? "is" : "are")} not listed for "
                       + $"{type} in Qloo's Entity Type Parameter Guide. Qloo ignores unsupported parameters "
                       + "silently, so this may have had no effect.");
        }

        var demographic = query.Names.Where(n => n.StartsWith("signal.demographics.", StringComparison.Ordinal)).ToArray();
        if (demographic.Length > 0 && !query.Names.Any(n => n.StartsWith("signal.location", StringComparison.Ordinal))
            && type != "urn:demographics")
        {
            warnings.Add("A demographic signal without signal.location.query returns close to the all-time popular "
                       + "list rather than this audience's own taste (measured 2026-10-15). Give the audience a place.");
        }

        return (null, warnings);
    }

    /// <summary>
    /// The one closest name, but only when it is unambiguous and close — a suggestion is obeyed, so a wrong
    /// one does more harm than none (CLAUDE.md §2: the vertical channel carries wrong signals too).
    /// </summary>
    internal static string Suggest(string name, IEnumerable<string> candidates)
    {
        var scored = candidates.Select(c => (Name: c, Distance: Distance(name, c))).OrderBy(c => c.Distance).ToArray();
        if (scored.Length == 0 || scored[0].Distance > 2) return string.Empty;
        if (scored.Length > 1 && scored[1].Distance == scored[0].Distance) return string.Empty;
        return $" Did you mean '{scored[0].Name}'? Check that it is the parameter you meant before using it.";
    }

    private static string? CheckValues(QlooQuery query)
    {
        static IEnumerable<string> Items(object? value) =>
            (value as string ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var age in Items(query["signal.demographics.age"]))
            if (!AgeBands.Contains(age))
                return $"signal.demographics.age '{age}' is not a Qloo age band.{Suggest(age, AgeBands)} Bands: {string.Join(", ", AgeBands)}.";

        if (query["signal.demographics.gender"] is string gender && !Genders.Contains(gender))
            return $"signal.demographics.gender '{gender}' must be male or female.";

        foreach (var name in new[] { "filter.popularity.min", "filter.popularity.max" })
            if (query[name] is string text && !(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 1))
                return $"{name} is a percentile between 0 and 1 (0.9 is the 90th); '{text}' is not.";

        foreach (var name in new[] { "take", "page", "diversify.take" })
            if (query[name] is string text && !(int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1))
                return $"{name} must be a whole number of at least 1; '{text}' is not.";

        if (query["offset"] is string offset && !int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return $"offset must be a whole number of at least 0; '{offset}' is not.";

        foreach (var name in query.Names.Where(n => n.StartsWith("operator.", StringComparison.Ordinal)))
            if (query[name] is string op && op is not ("union" or "intersection"))
                return $"{name} is 'union' or 'intersection'; '{op}' is not.";

        foreach (var name in query.Names.Where(n => n.StartsWith("feature.", StringComparison.Ordinal)))
            if (query[name] is string flag && flag is not ("true" or "false"))
                return $"{name} is true or false; '{flag}' is not.";

        return null;
    }

    /// <summary>Names whose meaning depends on the result type — filters other than the universal ones.</summary>
    private static bool IsTypeSpecific(string name) =>
        (name.StartsWith("filter.", StringComparison.Ordinal) && name != "filter.type")
        || name is "bias.quality" or "sort_by" or "heatmap.dimensions" or "output.heatmap.boundary"
        || name.StartsWith("diversify.", StringComparison.Ordinal)
        || name is "operator.filter.release_country";

    private static IReadOnlySet<string> AllowedFor(string type) =>
        TypeFilters.TryGetValue(type, out var set) ? set : EmptySet;

    private static IReadOnlySet<string> BuildKnown()
    {
        string[] filters =
        [
            "filter.address", "filter.audience.types", "filter.content_rating", "filter.date_of_birth.max",
            "filter.date_of_birth.min", "filter.date_of_death.max", "filter.date_of_death.min",
            "filter.dimensions.hotel_class", "filter.dimensions.price_level", "filter.entities", "filter.entity_ids",
            "filter.exclude.entities", "filter.exclude.entities.query", "filter.exclude.location",
            "filter.exclude.location.geohash", "filter.exclude.location.query", "filter.exclude.tags",
            "filter.external.exists", "filter.external.resy.count.max", "filter.external.resy.count.min",
            "filter.external.resy.party_size.max", "filter.external.resy.party_size.min",
            "filter.external.resy.rating.max", "filter.external.resy.rating.min",
            "filter.external.tripadvisor.rating.count.max", "filter.external.tripadvisor.rating.count.min",
            "filter.external.tripadvisor.rating.max", "filter.external.tripadvisor.rating.min",
            "filter.finale_year.max", "filter.finale_year.min", "filter.gender", "filter.geocode.admin1_region",
            "filter.geocode.admin2_region", "filter.geocode.country_code", "filter.geocode.name",
            "filter.hotel_class.max", "filter.hotel_class.min", "filter.hours", "filter.ids",
            "filter.latest_known_year.max", "filter.latest_known_year.min", "filter.location",
            "filter.location.geohash", "filter.location.query", "filter.location.radius", "filter.parents.types",
            "filter.popularity.max", "filter.popularity.min", "filter.price_level.max", "filter.price_level.min",
            "filter.price_range.max", "filter.price_range.min", "filter.price_range.from", "filter.price_range.to",
            "filter.properties.business_rating.max", "filter.properties.business_rating.min",
            "filter.properties.resy.rating.max", "filter.properties.resy.rating.min",
            "filter.publication_year.max", "filter.publication_year.min", "filter.rating.max", "filter.rating.min",
            "filter.references_brand", "filter.release_country", "filter.release_date.max", "filter.release_date.min",
            "filter.release_year.max", "filter.release_year.min", "filter.results.entities",
            "filter.results.entities.query", "filter.resy.rating_count.max", "filter.resy.rating_count.min",
            "filter.resy.rating.party.max", "filter.resy.rating.party.min", "filter.tag.types", "filter.tags",
            "filter.type",
        ];
        string[] signals =
        [
            "bias.quality", "bias.trends", "operator.signal.interests.tags", "signal.demographics.age",
            "signal.demographics.audiences", "signal.demographics.audiences.weight", "signal.demographics.gender",
            "signal.interests.entities", "signal.interests.entities.query", "signal.interests.tags",
            "signal.location", "signal.location.query", "signal.location.radius",
            // Sent by Qloo's own client, absent from the reference.
            "signal.interests.entities.weight", "signal.interests.tags.weight", "signal.location.weight",
        ];
        string[] output =
        [
            "diversify.by", "diversify.take", "feature.explainability", "heatmap.dimensions", "offset",
            "output.heatmap.boundary", "page", "sort_by", "take",
            "operator.exclude.tags", "operator.filter.external.exists", "operator.filter.release_country",
            "operator.filter.tags",
        ];
        return new HashSet<string>([.. filters, .. signals, .. output], StringComparer.Ordinal);
    }

    private static Dictionary<string, IReadOnlySet<string>> BuildTypeFilters()
    {
        string[] common =
        [
            "filter.exclude.entities", "filter.exclude.entities.query", "filter.exclude.tags", "operator.exclude.tags",
            "filter.external.exists", "operator.filter.external.exists", "filter.parents.types",
            "filter.popularity.min", "filter.popularity.max", "filter.results.entities",
            "filter.results.entities.query", "filter.tags", "operator.filter.tags",
        ];
        string[] located =
        [
            "filter.geocode.name", "filter.geocode.admin1_region", "filter.geocode.admin2_region",
            "filter.geocode.country_code", "filter.location", "filter.location.query", "filter.location.radius",
            "filter.location.geohash", "filter.exclude.location", "filter.exclude.location.query",
            "filter.exclude.location.geohash",
        ];
        string[] screen =
        [
            "filter.content_rating", "filter.release_year.min", "filter.release_year.max", "filter.release_country",
            "operator.filter.release_country", "filter.rating.min", "filter.rating.max",
        ];
        static IReadOnlySet<string> Set(params string[][] groups) =>
            new HashSet<string>(groups.SelectMany(g => g), StringComparer.Ordinal);

        return new(StringComparer.Ordinal)
        {
            ["urn:entity:artist"] = Set(common),
            ["urn:entity:brand"] = Set(common),
            ["urn:entity:podcast"] = Set(common),
            ["urn:entity:video_game"] = Set(common),
            ["urn:entity:book"] = Set(common, ["filter.publication_year.min", "filter.publication_year.max"]),
            ["urn:entity:destination"] = Set(common, located),
            ["urn:entity:movie"] = Set(common, screen, ["filter.release_date.min", "filter.release_date.max"]),
            ["urn:entity:tv_show"] = Set(common, screen,
                ["filter.finale_year.min", "filter.finale_year.max", "filter.latest_known_year.min", "filter.latest_known_year.max"]),
            ["urn:entity:person"] = Set(common,
                ["filter.date_of_birth.min", "filter.date_of_birth.max", "filter.date_of_death.min", "filter.date_of_death.max", "filter.gender"]),
            ["urn:entity:place"] = Set(common, located,
            [
                "bias.quality", "sort_by", "filter.address", "filter.hours", "filter.references_brand",
                "filter.external.tripadvisor.rating.count.max", "filter.external.tripadvisor.rating.count.min",
                "filter.external.tripadvisor.rating.max", "filter.external.tripadvisor.rating.min",
                "filter.external.resy.count.max", "filter.external.resy.count.min",
                "filter.external.resy.party_size.max", "filter.external.resy.party_size.min",
                "filter.external.resy.rating.max", "filter.external.resy.rating.min",
                "filter.hotel_class.max", "filter.hotel_class.min", "filter.price_level.min", "filter.price_level.max",
                "filter.price_range.from", "filter.price_range.to", "filter.price_range.min", "filter.price_range.max",
                "filter.properties.business_rating.min", "filter.properties.business_rating.max",
                "filter.properties.resy.rating.min", "filter.properties.resy.rating.max",
                "filter.resy.rating_count.min", "filter.resy.rating_count.max",
                "filter.resy.rating.party.min", "filter.resy.rating.party.max",
            ]),
            ["urn:heatmap"] = Set(["filter.location", "filter.location.query", "filter.location.radius",
                "heatmap.dimensions", "filter.dimensions.hotel_class", "filter.dimensions.price_level",
                "output.heatmap.boundary"]),
            ["urn:tag"] = Set(["diversify.by", "diversify.take"]),
            ["urn:demographics"] = Set([]),
        };
    }

    /// <summary>Optimal string alignment distance: a transposition counts as one edit.</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
    #endregion

    #region Fields
    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    private static readonly Dictionary<string, IReadOnlySet<string>> TypeFilters = BuildTypeFilters();
    #endregion
}
