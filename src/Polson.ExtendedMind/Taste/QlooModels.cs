namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

#region Outcome

/// <summary>Why a Qloo call did not succeed. <see cref="None"/> on success, including an empty one.</summary>
public enum QlooFailure
{
    None = 0,

    /// <summary>Refused by our own checks before any request was made, so nothing was spent.</summary>
    InvalidRequest,

    /// <summary>401. The key is wrong, or it was sent to the wrong environment.</summary>
    Auth,

    /// <summary>403. The key may not read this entity type.</summary>
    UnsupportedType,

    /// <summary>429 with requests left this month: the five-a-second limit.</summary>
    RateLimited,

    /// <summary>429 with nothing left this month.</summary>
    QuotaExhausted,

    Timeout,
    Network,
    ServiceError,

    /// <summary>The service answered 2xx with a body we could not read. The body is kept.</summary>
    MalformedResponse,

    Cancelled,
    Unknown,
}

/// <summary>Qloo's rate-limit headers, read from every response.</summary>
/// <remarks>Measured on the hackathon key, 2026-10-15: five requests a second and 10,000 a month.</remarks>
public sealed record QlooRateLimit(int? PerSecondLimit, int? PerSecondRemaining, int? MonthlyLimit, int? MonthlyRemaining, double? MonthlyResetSeconds);

/// <summary>
/// What every Qloo call returns. Nothing in <see cref="QlooClient"/> throws for a remote fault: the failure is
/// classified here, with a <see cref="Remedy"/> written for whoever reads it next.
/// </summary>
public class QlooResponse
{
    #region Properties
    public bool Success { get; internal set; }

    public QlooFailure Failure { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>What to do about <see cref="Failure"/>, or null on success.</summary>
    public string? Remedy => Failure switch
    {
        QlooFailure.None => null,
        QlooFailure.InvalidRequest => "Correct the query; nothing was sent or spent.",
        QlooFailure.Auth => "Check ApiKeys:Qloo, and that Qloo:BaseUrl is https://hackathon.api.qloo.com — a hackathon key answers 401 on staging and production.",
        QlooFailure.UnsupportedType => "This key may not read that filter.type. Use the closest supported type (music is urn:entity:artist; food and drink is urn:entity:place or urn:entity:brand).",
        QlooFailure.RateLimited => "Qloo allows five requests a second. Wait a moment and retry.",
        QlooFailure.QuotaExhausted => "This key's monthly quota is spent. Ask in #api-help on the Qloo hackathon Discord for more, and work from cached results meanwhile.",
        QlooFailure.Timeout or QlooFailure.Network => "Qloo could not be reached. Retry; if it persists, check https://docs.qloo.com/reference/api-system-status.",
        QlooFailure.ServiceError => "Qloo failed on its side. Retry later.",
        QlooFailure.MalformedResponse => "Qloo answered with a body this client could not read; RawBody holds it.",
        QlooFailure.Cancelled => null,
        _ => "See Error and StatusCode.",
    };

    public int? StatusCode { get; internal set; }

    /// <summary>Qloo's <c>x-qloo-request-id</c>: what to quote when asking Qloo about a call.</summary>
    public string? RequestId { get; internal set; }

    public long ElapsedMs { get; internal set; }

    /// <summary>The body of a failed or unreadable response, truncated. Null on success.</summary>
    public string? RawBody { get; internal set; }

    /// <summary>The endpoint and canonical query, without the key — provenance, and safe to show.</summary>
    public string Request { get; internal set; } = string.Empty;

    /// <summary>Ours (from <see cref="QlooParameters"/>) and Qloo's own <c>warnings</c>, in that order.</summary>
    public IReadOnlyList<string> Warnings => warnings;

    public QlooRateLimit? RateLimit { get; internal set; }
    #endregion

    #region Methods
    internal void Warn(string warning) => warnings.Add(warning);

    internal void Warn(IEnumerable<string> more) => warnings.AddRange(more);
    #endregion

    #region Fields
    private readonly List<string> warnings = [];
    #endregion
}
#endregion

#region Content

/// <summary>A Qloo tag: a genre, a keyword, an aesthetic property (<c>urn:tag:aesthetic_property:qloo</c>), and so on.</summary>
public sealed record QlooTag(string Id, string Name, string? Type, double? Popularity, double? Affinity)
{
    internal static QlooTag? From(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && QlooJson.String(e, "id", "tag_id") is string id
            ? new(id, QlooJson.String(e, "name") ?? id, QlooJson.String(e, "type", "subtype"), QlooJson.Number(e, "popularity"),
                  QlooJson.Number(e, "affinity") ?? (e.TryGetProperty("query", out var q) ? QlooJson.Number(q, "affinity") : null))
            : null;
}

/// <summary>
/// A Qloo entity — a film, an artist, a brand, a place. The fields a profile reasons with are typed; the rest
/// stays in <see cref="Properties"/> as Qloo sent it.
/// </summary>
public sealed record QlooEntity
{
    #region Properties
    public required string EntityId { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The entity's types, most specific first: <c>/search</c> sends a <c>types</c> list, Insights a
    /// <c>type</c> of <c>urn:entity</c> with the real type in <c>subtype</c>.
    /// </summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>A human tie-breaker Qloo supplies, such as a year or a country.</summary>
    public string? Disambiguation { get; init; }

    /// <summary>Popularity percentile, 0–1.</summary>
    public double? Popularity { get; init; }

    /// <summary>Affinity to the query's signals, from <c>query.affinity</c>. Present on Insights results only.</summary>
    public double? Affinity { get; init; }

    /// <summary>Per-result reasons when <c>feature.explainability=true</c> and Qloo could compute them.</summary>
    public JsonElement? Explainability { get; init; }

    public IReadOnlyList<QlooTag> Tags { get; init; } = [];

    public JsonElement? Properties { get; init; }
    #endregion

    #region Methods
    internal static QlooEntity? From(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || QlooJson.String(e, "entity_id", "id") is not string id) return null;

        var types = e.TryGetProperty("types", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(t => t.GetString()).OfType<string>().ToArray()
            : new[] { QlooJson.String(e, "subtype"), QlooJson.String(e, "type") }.OfType<string>().Distinct().ToArray();
        var query = e.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Object ? q : (JsonElement?)null;

        return new QlooEntity
        {
            EntityId = id,
            Name = QlooJson.String(e, "name") ?? id,
            Types = types,
            Disambiguation = QlooJson.String(e, "disambiguation"),
            Popularity = QlooJson.Number(e, "popularity"),
            Affinity = query is { } qq ? QlooJson.Number(qq, "affinity") : null,
            Explainability = query is { } qe && qe.TryGetProperty("explainability", out var x) ? x.Clone() : null,
            Tags = QlooJson.Array(e, "tags").Select(QlooTag.From).OfType<QlooTag>().ToArray(),
            Properties = e.TryGetProperty("properties", out var p) ? p.Clone() : null,
        };
    }
    #endregion
}

/// <summary>A Qloo audience, such as a community, a life stage or a global issue.</summary>
public sealed record QlooAudience(string Id, string Name, string? Type, IReadOnlyList<string> Parents)
{
    internal static QlooAudience? From(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && QlooJson.String(e, "entity_id", "id") is string id
            ? new(id, QlooJson.String(e, "name") ?? id, QlooJson.String(e, "type", "subtype"),
                  QlooJson.Array(e, "parents").Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : QlooJson.String(p, "type", "id"))
                      .OfType<string>().ToArray())
            : null;
}

/// <summary>
/// The place Qloo matched a <c>signal.location.query</c> to. Worth showing: the query is fuzzy-matched, so
/// "Lagos" could have been Lagos, Portugal.
/// </summary>
public sealed record QlooLocality(string EntityId, string Name, string? Disambiguation, double? Latitude, double? Longitude)
{
    internal static QlooLocality? From(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && QlooJson.String(e, "entity_id") is string id
            ? new(id, QlooJson.String(e, "name") ?? id, QlooJson.String(e, "disambiguation"),
                  e.TryGetProperty("location", out var l) ? QlooJson.Number(l, "lat") : null,
                  e.TryGetProperty("location", out var m) ? QlooJson.Number(m, "lon") : null)
            : null;
}
#endregion

#region Responses

/// <summary>An Insights answer. Which list is filled depends on <c>filter.type</c>.</summary>
public sealed class QlooInsightsResponse : QlooResponse
{
    public IReadOnlyList<QlooEntity> Entities { get; internal set; } = [];

    /// <summary>For <c>filter.type=urn:tag</c>.</summary>
    public IReadOnlyList<QlooTag> Tags { get; internal set; } = [];

    /// <summary>For <c>urn:demographics</c>: one block per signal, age bands and gender as affinities. Kept as sent.</summary>
    public IReadOnlyList<JsonElement> Demographics { get; internal set; } = [];

    /// <summary>For <c>urn:heatmap</c>: one cell per location. Kept as sent.</summary>
    public IReadOnlyList<JsonElement> Heatmap { get; internal set; } = [];

    /// <summary>Where Qloo placed a single <c>signal.location.query</c>, from <c>query.localities.signal</c>.</summary>
    public QlooLocality? Locality { get; internal set; }

    /// <summary>
    /// A 200 with nothing in it. Not a failure — but Qloo answers this way to an unsupported parameter too, so
    /// it always carries a warning saying so.
    /// </summary>
    public bool IsEmpty => Success && Entities.Count == 0 && Tags.Count == 0 && Demographics.Count == 0 && Heatmap.Count == 0;
}

/// <summary>Entities found by name (<c>/search</c>) or by id (<c>/entities</c>).</summary>
public sealed class QlooEntitiesResponse : QlooResponse
{
    public IReadOnlyList<QlooEntity> Entities { get; internal set; } = [];
}

public sealed class QlooTagsResponse : QlooResponse
{
    public IReadOnlyList<QlooTag> Tags { get; internal set; } = [];
}

public sealed class QlooAudiencesResponse : QlooResponse
{
    public IReadOnlyList<QlooAudience> Audiences { get; internal set; } = [];
}

/// <summary>A type listing (<c>/v2/audiences/types</c>, <c>/v2/tags/types</c>): the URNs alone.</summary>
public sealed class QlooTypesResponse : QlooResponse
{
    public IReadOnlyList<string> Types { get; internal set; } = [];
}

/// <summary><c>/v2/analysis/compare</c>. The shape is not specified closely enough to type, so it is kept as sent.</summary>
public sealed class QlooCompareResponse : QlooResponse
{
    public JsonElement? Results { get; internal set; }
}
#endregion

/// <summary>Tolerant reads over Qloo's JSON, which spells the same field more than one way.</summary>
internal static class QlooJson
{
    public static string? String(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                return s;
        return null;
    }

    public static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            }
            : null;

    public static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];
}
