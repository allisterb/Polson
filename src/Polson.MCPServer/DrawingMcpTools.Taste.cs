namespace Polson.MCPServer;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Polson.ExtendedMind.Taste;

public partial class DrawingMcpTools
{
    #region Properties
    /// <summary>
    /// The Qloo client every session is a scope of, configured once at startup from <c>ApiKeys:Qloo</c>. Null
    /// when no key is configured, and the <c>Taste</c> tool says so plainly.
    /// </summary>
    /// <remarks>
    /// One client for the process, not one per session: Qloo's five-a-second limit and its monthly quota belong
    /// to the key, so the pacing and the last-known quota must be shared. Each call runs on
    /// <see cref="QlooClient.Scoped"/> with the session's own budget.
    /// </remarks>
    public static QlooClient? TasteClient { get; set; }

    /// <summary>Most results the tool returns in one reply. The full answer is kept for scripts either way.</summary>
    internal const int MaxTasteResults = 50;
    #endregion

    #region Methods
    [McpServerTool(Name = "Taste")]
    [Description("Asks Qloo's taste graph what an AUDIENCE actually loves — the artists, films, shows, brands and places " +
        "that over-index for a place, an age band, a gender or a community — and what those things share. Use it " +
        "BEFORE designing for an audience, so references, palette and imagery rest on measured taste rather than on " +
        "guesses about a group. Every answer carries the Qloo request it came from: cite it.\n\n" +
        "OPERATIONS (op):\n" +
        "- 'resolve': a name to Qloo ids, before using it as a signal. kind 'entity' (default; narrow with type), " +
        "'tag' (e.g. 'warm palette'), or 'audience' (e.g. 'Latino', 'Retirement'). Returns CANDIDATES. When `ambiguous` " +
        "is true, choose by disambiguation — and ASK THE DIRECTOR if the choice would change the result. Never pass an " +
        "id you have not resolved.\n" +
        "- 'insights': what an audience likes, of one type. Give `type` (artist, movie, tv_show, brand, book, podcast, " +
        "video_game, place, destination, person) and `audience` as JSON, e.g. {\"location\":\"Lagos\",\"age\":\"55_and_older\"}. " +
        "Extra Qloo parameters go in `query` as JSON. A location is REQUIRED with an age or gender: without one, Qloo " +
        "returns roughly the all-time popular list, not this audience. A place NAME is fuzzy-matched — 'Lagos' is also " +
        "in Portugal and Spain — so check `locality` in the reply, or resolve it first (op 'resolve', type 'locality') " +
        "and pass the chosen id as the location.\n" +
        "- 'describe': the tags a set of entities share, from `entities` (ids). The bridge from what an audience " +
        "watches to how a design should look. Give `type` as tag kinds to keep only those — 'aesthetic,motif' for " +
        "visual vocabulary (palettes, imagery), or 'theme', 'ambience', 'archetype'. Without it you get an overview " +
        "across every kind, which over films includes dishes and venues.\n" +
        "- 'demographics': who an entity appeals to, by age band and gender, from `entities`. Use it to check a " +
        "reference really skews to your audience before you borrow from it.\n" +
        "- 'compare': what separates two sets of entities' audiences: `entities` against `versus`.\n" +
        "- 'audiences': Qloo's audience ids under a type, e.g. type 'communities', 'life_stage', 'global_issues'.\n" +
        "- 'status': this session's allowance and the key's monthly quota. Free.\n\n" +
        "WHAT QLOO IS NOT. It holds measured taste, not culture's rules: it does not know which colours mean mourning " +
        "or which numbers are unlucky. Those come from research, cited separately — never present a Qloo result as one. " +
        "Results are AGGREGATE affinities: never describe them as facts about a person, and never infer health, beliefs " +
        "or identity from them.\n\n" +
        "COST. Requests are metered (5 a second, 10,000 a month for the whole studio) and this session has an " +
        "allowance (`budget` in every reply). A repeated question is answered from the cache and costs nothing, so " +
        "ask the same thing the same way. A query with an unknown or misspelt parameter is REFUSED before sending — " +
        "Qloo would otherwise ignore it silently and answer with nothing — so a refusal is free advice.\n\n" +
        "Every answer stays readable from later scripts as the `Taste` global: Taste.get('t3').entities, " +
        "Taste.latest.cite() for the caption line.")]
    public Task<JsonObject> Taste(
        [Description("'resolve', 'insights', 'describe', 'demographics', 'compare', 'audiences' or 'status'.")] string op,
        [Description("resolve: the name or words to look up.")] string? name = null,
        [Description("resolve: 'entity' (default), 'tag' or 'audience'.")] string? kind = null,
        [Description("insights and compare: the result type — artist, movie, tv_show, brand, book, podcast, video_game, place, destination, person, or a full urn. resolve: narrows an entity search, or for kind 'tag' the tag kind. describe: tag kinds to keep, comma-separated, e.g. 'aesthetic,motif'. audiences: the audience type, e.g. 'communities'.")] string? type = null,
        [Description("insights: the audience, as JSON: {\"location\":\"Mexico City\",\"age\":\"55_and_older\",\"gender\":\"female\",\"audiences\":[\"<audience id>\"]}. location is a place name, or a locality id from resolve (type 'locality'). Age bands: 24_and_younger, 25_to_29, 30_to_34, 35_to_44, 45_to_54, 55_and_older. location is required with age or gender.")] string? audience = null,
        [Description("insights: further Qloo Insights parameters as a JSON object, e.g. {\"filter.popularity.min\":0.5,\"signal.interests.entities\":[\"<id>\"],\"feature.explainability\":true}. Checked before sending.")] string? query = null,
        [Description("describe, demographics, compare: Qloo entity ids, comma-separated, from 'resolve' or an earlier answer.")] string? entities = null,
        [Description("compare: the second set of entity ids, comma-separated.")] string? versus = null,
        [Description("How many results (1-50; default 10, 25 for describe).")] int? take = null,
        [Description("What this is for, in your own words. Recorded, and how a script finds it later with Taste.find(...).")] string? label = null,
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    => RecordedAsync(nameof(Taste), async () =>
    {
        var session = Registry.GetOrCreate(GetSessionId(context?.Server));
        var registry = session.Taste;
        op = (op ?? string.Empty).Trim().ToLowerInvariant();
        label = Trimmed(label) ?? $"{op} {Trimmed(name) ?? Trimmed(type) ?? Trimmed(entities) ?? string.Empty}".Trim();

        if (op == "status") return TasteStatus(registry.Budget);

        if (TasteClient is null)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "No Qloo API key is configured, so taste data is unavailable for this whole run.",
                ["remedy"] = "Do not guess what an audience likes and present it as measured. Design from the brief, and "
                           + "tell the director the taste grounding could not be fetched.",
            };
        }

        var client = TasteClient.Scoped(registry.Budget);
        var count = Math.Clamp(take ?? (op == "describe" ? 25 : 10), 1, MaxTasteResults);
        var (call, refusal) = TastePlan(client, op, name, kind, type, audience, query, entities, versus, count, cancellationToken);

        if (refusal is not null || call is null)
        {
            refusal ??= "Nothing to do.";
            Events.Append("taste.refused", session.Stage, null, new Dictionary<string, object?> { ["op"] = op, ["label"] = label, ["reason"] = refusal });
            return new JsonObject
            {
                ["ok"] = false, ["op"] = op, ["error"] = refusal, ["note"] = "Nothing was sent or spent.",
                ["budget"] = TasteBudgetNode(registry.Budget),
            };
        }

        var response = await call();
        var result = registry.Add(op, label, response);
        Events.Append(response.Success ? "taste.query" : "taste.failed", session.Stage, null, new Dictionary<string, object?>
        {
            ["id"] = result.Id,
            ["op"] = op,
            ["label"] = label,
            ["request"] = response.Request,
            ["failure"] = response.Success ? null : response.Failure.ToString(),
            ["fromCache"] = response.FromCache,
            ["results"] = result.Entities.Length + result.Tags.Length + result.Audiences.Length + result.Demographics.Length,
            ["warnings"] = response.Warnings.Count,
            ["spent"] = registry.Budget.Spent,
            ["remaining"] = registry.Budget.Remaining,
            ["monthlyRemaining"] = registry.Budget.MonthlyRemaining,
        });

        return TasteReply(op, result, response, registry.Budget, Trimmed(name), count);
    });
    #endregion

    #region Internal methods
    internal const string TasteTypeList = "artist, movie, tv_show, brand, book, podcast, video_game, place, destination, person";

    /// <summary>
    /// The Qloo call one operation makes, or why it cannot be made. Deciding is separate from calling so every
    /// refusal is found before anything is sent.
    /// </summary>
    internal static (Func<Task<QlooResponse>>? Call, string? Refusal) TastePlan(
        QlooClient client, string op, string? name, string? kind, string? type, string? audience, string? query,
        string? entities, string? versus, int count, CancellationToken ct)
    {
        switch (op)
        {
            case "resolve":
            {
                if (Trimmed(name) is not string lookFor) return (null, "resolve needs a name.");
                return (Trimmed(kind) ?? "entity").ToLowerInvariant() switch
                {
                    "tag" => (async () => await client.Tags(lookFor, TasteTagType(type) is string tagType ? [tagType] : null, take: Math.Min(count, 10), cancellationToken: ct), null),
                    "audience" => (async () => await client.Audiences(TasteAudienceType(type), 100, lookFor, ct), null),
                    "entity" => (async () => await client.Search(lookFor,
                        TasteType(type) is string t && t.StartsWith("urn:entity:", StringComparison.Ordinal) ? [t] : null, Math.Min(count, 10), ct), null),
                    var other => (null, $"resolve kind is 'entity', 'tag' or 'audience'; '{other}' is not."),
                };
            }

            case "insights":
            {
                if (TasteType(type) is not string resultType) return (null, $"insights needs a type: {TasteTypeList}.");
                var (built, problem) = TasteQuery(resultType, audience, query, count);
                return built is null ? (null, problem) : (async () => await client.Insights(built, ct), null);
            }

            case "describe":
            {
                if (TasteIds(entities) is not { Length: > 0 } described) return (null, "describe needs entities: Qloo ids, comma-separated.");
                var q = new QlooQuery { ["filter.type"] = "urn:tag", ["signal.interests.entities"] = described, ["take"] = count };

                // Asked for kinds of tag: only those. Not asked: an overview, capped per kind — over films Qloo
                // returns tags from every domain, dishes and place categories included, and uncapped the
                // commonest kind would crowd out the rest.
                if (TasteTagTypes(type) is { Length: > 0 } kinds) q["filter.tag.types"] = kinds;
                else { q["diversify.by"] = "subtype"; q["diversify.take"] = Math.Max(3, count / 4); }
                return (async () => await client.Insights(q, ct), null);
            }

            case "demographics":
            {
                if (TasteIds(entities) is not { Length: > 0 } who) return (null, "demographics needs entities: Qloo ids, comma-separated.");
                var tags = who.Where(i => i.StartsWith("urn:tag:", StringComparison.Ordinal)).ToArray();
                var q = new QlooQuery
                {
                    ["filter.type"] = "urn:demographics",
                    ["signal.interests.entities"] = who.Except(tags).ToArray(),
                    ["signal.interests.tags"] = tags,
                };
                return (async () => await client.Insights(q, ct), null);
            }

            case "compare":
            {
                if (TasteIds(entities) is not { Length: > 0 } left || TasteIds(versus) is not { Length: > 0 } right)
                    return (null, "compare needs entities and versus: two sets of Qloo ids, comma-separated.");
                return (async () => await client.Compare(left, right, TasteType(type), count, ct), null);
            }

            case "audiences":
                return (async () => await client.Audiences(TasteAudienceType(type), 100, null, ct), null);

            default:
                return (null, $"Unknown op '{op}'. Use resolve, insights, describe, demographics, compare, audiences or status.");
        }
    }

    /// <summary>A short type name to its Qloo URN, or the URN as given; null when it is neither.</summary>
    internal static string? TasteType(string? type) => Trimmed(type)?.ToLowerInvariant() switch
    {
        null => null,
        "artist" or "music" or "musician" => "urn:entity:artist",
        "movie" or "film" or "films" or "movies" => "urn:entity:movie",
        "tv" or "tv_show" or "show" or "television" => "urn:entity:tv_show",
        "brand" or "book" or "podcast" or "place" or "destination" or "person" => $"urn:entity:{type!.Trim().ToLowerInvariant()}",
        "video_game" or "game" => "urn:entity:video_game",
        "locality" or "city" or "location" => "urn:entity:locality",
        "tag" => "urn:tag",
        "demographics" => "urn:demographics",
        "heatmap" => "urn:heatmap",
        var urn when urn.StartsWith("urn:", StringComparison.Ordinal) => urn,
        _ => null,
    };

    private static string? TasteTagType(string? type) => TasteTagTypes(type).FirstOrDefault();

    /// <summary>
    /// Tag kinds, comma-separated, as URNs: <c>aesthetic</c> is <c>urn:tag:aesthetic_property:qloo</c>, another short
    /// name <c>x</c> is <c>urn:tag:x:qloo</c>, and a URN passes through.
    /// </summary>
    internal static string[] TasteTagTypes(string? types) =>
        (types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant() switch
            {
                "aesthetic" or "aesthetics" or "palette" => "urn:tag:aesthetic_property:qloo",
                var urn when urn.StartsWith("urn:tag:", StringComparison.Ordinal) => urn,
                var other when other.StartsWith("urn:", StringComparison.Ordinal) => null,
                var shortName => $"urn:tag:{shortName.Replace(' ', '_')}:qloo",
            })
            .OfType<string>().Distinct().ToArray();

    private static string? TasteAudienceType(string? type) => Trimmed(type) switch
    {
        null => null,
        var urn when urn.StartsWith("urn:audience:", StringComparison.Ordinal) => urn,
        var shortName => "urn:audience:" + shortName.ToLowerInvariant().Replace(' ', '_'),
    };

    private static string[] TasteIds(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    /// <summary>The Insights query from a type, an audience and extra parameters, or the reason it cannot be built.</summary>
    internal static (QlooQuery? Query, string? Problem) TasteQuery(string resultType, string? audience, string? extra, int take)
    {
        var q = new QlooQuery { ["filter.type"] = resultType, ["take"] = take };

        if (Trimmed(extra) is string json)
        {
            if (TasteObject(json, "query") is not (JsonObject parameters, null)) return (null, TasteObject(json, "query").Problem);
            foreach (var (key, value) in parameters) q[key] = TasteValue(value);
        }

        if (Trimmed(audience) is string who)
        {
            if (TasteObject(who, "audience") is not (JsonObject a, null)) return (null, TasteObject(who, "audience").Problem);
            foreach (var key in a.Select(p => p.Key))
                if (key is not ("location" or "age" or "gender" or "audiences"))
                    return (null, $"audience has no '{key}'. It takes location, age, gender and audiences; other Qloo parameters go in query.");
            // A resolved locality id goes in signal.location, which takes Qloo ids; a name is fuzzy-matched through
            // signal.location.query. "Lagos" matches Lagos in Nigeria, Portugal and Spain (2026-10-15), so an id is
            // the way to be sure.
            if (TasteValue(a["location"]) is string place)
                q[Guid.TryParse(place, out _) ? "signal.location" : "signal.location.query"] = place;
            q["signal.demographics.age"] = TasteValue(a["age"]) ?? q["signal.demographics.age"];
            q["signal.demographics.gender"] = TasteValue(a["gender"]) ?? q["signal.demographics.gender"];
            q["signal.demographics.audiences"] = TasteValue(a["audiences"]) ?? q["signal.demographics.audiences"];
        }

        if ((q.Has("signal.demographics.age") || q.Has("signal.demographics.gender"))
            && !q.Names.Any(n => n.StartsWith("signal.location", StringComparison.Ordinal)))
        {
            return (null, "An age or gender needs a location as well — audience {\"location\": \"...\"}. Without one Qloo "
                        + "returns roughly the all-time popular list rather than this audience's taste (measured 2026-10-15).");
        }

        return (q, null);
    }

    private static (JsonObject? Value, string? Problem) TasteObject(string json, string what)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject o ? (o, null) : (null, $"{what} must be a JSON object.");
        }
        catch (JsonException ex)
        {
            return (null, $"{what} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>A JSON value as <see cref="QlooQuery"/> takes one: arrays become lists, the rest their text.</summary>
    private static object? TasteValue(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => array.Select(TasteValue).Where(v => v is not null).ToArray(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };

    private static JsonObject TasteBudgetNode(QlooBudget budget) => new()
    {
        ["spent"] = budget.Spent,
        ["remaining"] = budget.Remaining,
        ["cacheHits"] = budget.CacheHits,
        ["monthlyRemaining"] = budget.MonthlyRemaining,
        ["monthlyReserve"] = budget.MonthlyReserve,
    };

    private static JsonObject TasteStatus(QlooBudget budget) => new()
    {
        ["ok"] = true,
        ["op"] = "status",
        ["configured"] = TasteClient is not null,
        ["budget"] = TasteBudgetNode(budget),
        ["note"] = budget.ReserveReached
            ? "The monthly reserve is reached: only cached answers are available."
            : "Repeated questions are answered from the cache and cost nothing.",
    };

    /// <summary>
    /// The reply the agent reads: compact, because it lands in the context window. The full answer — every tag,
    /// every property — is kept on the session for scripts.
    /// </summary>
    private static JsonObject TasteReply(string op, TasteResult result, QlooResponse response, QlooBudget budget, string? asked, int count)
    {
        var reply = new JsonObject
        {
            ["ok"] = result.Ok,
            ["op"] = op,
            ["id"] = result.Id,
            ["label"] = result.Label,
            ["request"] = result.Request,
            ["fromCache"] = result.FromCache,
        };
        if (result.CachedUtc is not null) reply["cachedUtc"] = result.CachedUtc;
        if (!result.Ok)
        {
            reply["error"] = result.Error;
            reply["remedy"] = result.Remedy;
        }
        if (result.Warnings.Length > 0) reply["warnings"] = new JsonArray([.. result.Warnings.Select(w => (JsonNode?)w)]);
        if (result.Locality is { } place)
            reply["locality"] = new JsonObject { ["name"] = place["name"] as string, ["disambiguation"] = place["disambiguation"] as string };

        switch (response)
        {
            case QlooEntitiesResponse found when op == "resolve":
                var candidates = found.Entities.Take(count).ToArray();
                reply["candidates"] = new JsonArray([.. candidates.Select(e => (JsonNode)new JsonObject
                {
                    ["id"] = e.EntityId, ["name"] = e.Name, ["type"] = e.Types.FirstOrDefault(),
                    ["disambiguation"] = e.Disambiguation, ["popularity"] = Round(e.Popularity),
                })]);
                var exact = candidates.Count(e => string.Equals(e.Name, asked, StringComparison.OrdinalIgnoreCase));
                reply["ambiguous"] = exact > 1 || (exact == 0 && candidates.Length > 1);
                break;
            case QlooTagsResponse:
            case QlooAudiencesResponse:
                reply["candidates"] = new JsonArray([.. (result.Tags.Length > 0 ? result.Tags : result.Audiences).Take(count).Select(TasteNode)]);
                reply["ambiguous"] = (result.Tags.Length > 0 ? result.Tags : result.Audiences).Length > 1;
                break;
            case QlooInsightsResponse insights:
                if (insights.Entities.Count > 0)
                {
                    reply["entities"] = new JsonArray([.. insights.Entities.Take(count).Select(e => (JsonNode)new JsonObject
                    {
                        ["id"] = e.EntityId, ["name"] = e.Name, ["type"] = e.Types.FirstOrDefault(),
                        ["disambiguation"] = e.Disambiguation, ["affinity"] = Round(e.Affinity), ["popularity"] = Round(e.Popularity),
                        ["tags"] = new JsonArray([.. e.Tags.Take(8).Select(t => (JsonNode?)t.Name)]),
                    })]);
                }
                if (insights.Tags.Count > 0)
                {
                    reply["tags"] = new JsonArray([.. insights.Tags.Take(count).Select(t => (JsonNode)new JsonObject
                    {
                        ["id"] = t.Id, ["name"] = t.Name, ["type"] = t.Type, ["affinity"] = Round(t.Affinity), ["popularity"] = Round(t.Popularity),
                    })]);
                }
                if (insights.Demographics.Count > 0)
                    reply["demographics"] = new JsonArray([.. insights.Demographics.Select(d => JsonNode.Parse(d.GetRawText()))]);
                break;
            case QlooCompareResponse compared when compared.Results is { } raw:
                reply["comparison"] = JsonNode.Parse(raw.GetRawText());
                break;
        }

        reply["resultCount"] = result.Entities.Length + result.Tags.Length + result.Audiences.Length + result.Demographics.Length;
        reply["budget"] = TasteBudgetNode(budget);
        return reply;
    }

    private static JsonNode? TasteNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value);

    private static double? Round(double? value) => value is double v ? Math.Round(v, 3) : null;
    #endregion
}
