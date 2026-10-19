namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Polson.ExtendedMind.ParallelSearch;

/// <summary>
/// One answer from Qloo, as a script reads it: what was asked, what came back, and what it rests on.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a plain read; nothing here touches the network. Answers are fetched by the <c>Taste</c> MCP
/// tool — the agent has to read them to reason, so they arrive in its context — and kept here so a later script
/// can cite the evidence a mark rests on.
/// </para>
/// <para>
/// Values are plain dictionaries, lists and primitives, so <c>result.entities[0].name</c> works in JS. Lists are
/// arrays rather than <c>IReadOnlyList</c>: Jint caches a member's accessor per runtime type, and a property
/// whose type alternated threw (the <c>RequisitionVerdict.Triggers</c> lesson).
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members are PascalCase here; Jint resolves the JS camelCase spelling onto
/// them, so <c>result.fromCache</c> reaches <see cref="FromCache"/>.
/// </para>
/// </remarks>
public sealed class TasteResult
{
    #region Properties
    /// <summary>This session's handle for the answer: <c>t1</c>, <c>t2</c>…</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The operation: <c>resolve</c>, <c>insights</c>, <c>describe</c>, <c>demographics</c>, <c>compare</c>, <c>audiences</c>.</summary>
    public string Op { get; init; } = string.Empty;

    /// <summary>What it was for, in the agent's words.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The Qloo endpoint and canonical query, without the key. The provenance line.</summary>
    public string Request { get; init; } = string.Empty;

    public bool Ok { get; init; }

    public string? Error { get; init; }

    public string? Remedy { get; init; }

    public bool FromCache { get; init; }

    /// <summary>When Qloo gave a cached answer, ISO 8601; null for a fresh one.</summary>
    public string? CachedUtc { get; init; }

    public string? RequestId { get; init; }

    public string[] Warnings { get; init; } = [];

    /// <summary>Where Qloo placed the location, as <c>{ id, name, disambiguation }</c>, or null.</summary>
    public Dictionary<string, object?>? Locality { get; init; }

    /// <summary>Entities, each <c>{ id, name, type, types, disambiguation, popularity, affinity, tags, explainability, properties }</c>.</summary>
    public object?[] Entities { get; init; } = [];

    /// <summary>Tags, each <c>{ id, name, type, popularity, affinity }</c>.</summary>
    public object?[] Tags { get; init; } = [];

    /// <summary>Audiences, each <c>{ id, name, type, parents }</c>.</summary>
    public object?[] Audiences { get; init; } = [];

    /// <summary>For <c>demographics</c>: one block per signal, age bands and gender as affinities, as Qloo sent them.</summary>
    public object?[] Demographics { get; init; } = [];

    /// <summary>For <c>compare</c>: Qloo's result as sent.</summary>
    public object? Comparison { get; init; }

    /// <summary>
    /// For <c>profile</c>: <c>{ audience, locality, floor, domains, placeAesthetics, requests }</c>. Each domain is
    /// <c>{ type, distinctive, shared, audienceRequest, baselineRequest }</c>: <c>distinctive</c> is what this audience
    /// loves that its place's baseline does not list — the evidence a direction cites.
    /// </summary>
    public Dictionary<string, object?>? Profile { get; init; }

    public string RetrievedUtc { get; init; } = DateTime.UtcNow.ToString("O");
    #endregion

    #region Methods
    /// <summary>The caption line for this answer: what was asked of Qloo, and when.</summary>
    public string Cite() =>
        $"Qloo Insights API, {Request} ({(FromCache ? "cached, retrieved " + CachedUtc : "retrieved " + RetrievedUtc)}"
        + (RequestId is null ? ")" : $", request {RequestId})");

    /// <summary>
    /// How the films and shows in this answer pace themselves, in Qloo's own tags — for a <c>profile</c>, the audience's
    /// distinctive ones: <c>{ fast, slow, both, untagged, n, fastShare, fastTitles, slowTitles, vocabulary }</c>. See
    /// <see cref="TastePace"/>.
    /// </summary>
    public Dictionary<string, object?> Pace() => TastePace.Count(Entities);

    public override string ToString() => $"{Id} [{Op}] {Label} ({(Ok ? "ok" : "failed")})";

    #endregion
}

/// <summary>
/// The Qloo answers fetched during one session, and the session's allowance. Held on the session so an answer
/// fetched by one tool call is readable by a later script.
/// </summary>
public sealed class TasteRegistry
{
    #region Constructors
    public TasteRegistry(QlooBudget? budget = null) => Budget = budget ?? new QlooBudget(0);
    #endregion

    #region Properties
    public QlooBudget Budget { get; }

    public TasteResult[] All
    {
        get { lock (results) return [.. results]; }
    }
    #endregion

    #region Methods
    /// <summary>Records a response under the next id and returns it.</summary>
    public TasteResult Add(string op, string label, QlooResponse response)
    {
        lock (results)
        {
            var result = TasteObjects.Result($"t{results.Count + 1}", op, label, response);
            results.Add(result);
            return result;
        }
    }

    /// <summary>Records a result built elsewhere — a profile — under the next id, which the builder is given.</summary>
    public TasteResult Add(Func<string, TasteResult> build)
    {
        lock (results)
        {
            var result = build($"t{results.Count + 1}");
            results.Add(result);
            return result;
        }
    }

    public TasteResult? Get(string id)
    {
        lock (results) return results.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The Qloo type of an entity this session has seen — in any answer's entities, or a profile's shared lists —
    /// or null.
    /// </summary>
    public string? TypeOf(string entityId) =>
        All.SelectMany(r => r.Entities.Concat(SharedOf(r)))
            .OfType<Dictionary<string, object?>>()
            .Where(e => string.Equals(e.GetValueOrDefault("id") as string, entityId, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.GetValueOrDefault("type") as string)
            .FirstOrDefault(t => t is not null);
    #endregion

    #region Private methods
    private static IEnumerable<object?> SharedOf(TasteResult r) =>
        r.Profile?.GetValueOrDefault("domains") is object?[] domains
            ? domains.OfType<Dictionary<string, object?>>().SelectMany(d => d.GetValueOrDefault("shared") as object?[] ?? [])
            : [];
    #endregion

    #region Fields
    private readonly List<TasteResult> results = [];
    #endregion
}

/// <summary>
/// The <c>Taste</c> global: Qloo answers already fetched this session, for a script to read and cite. Fetching
/// is the <c>Taste</c> MCP tool's job.
/// </summary>
/// <remarks>
/// Exposed to the JavaScript sandbox. Members are PascalCase here; Jint resolves the JS camelCase spelling onto
/// them, so <c>Taste.latest</c> reaches <see cref="Latest"/>.
/// </remarks>
public sealed class TasteToolkit
{
    #region Constructors
    public TasteToolkit(TasteRegistry registry) => this.registry = registry;
    #endregion

    #region Properties
    public TasteResult[] Results => registry.All;

    public int Count => registry.All.Length;

    public TasteResult? Latest => registry.All is { Length: > 0 } all ? all[^1] : null;

    public QlooBudget Budget => registry.Budget;
    #endregion

    #region Methods
    /// <summary>The answer with this id (<c>'t3'</c>), or null.</summary>
    public TasteResult? Get(string id) => registry.Get(id);

    /// <summary>The latest answer whose label contains <paramref name="text"/>, or null.</summary>
    public TasteResult? Find(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : registry.All.LastOrDefault(r => r.Label.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Two answers' pace side by side — two audiences' profiles — with Fisher's exact test on their fast and slow counts:
    /// <c>{ a, b, p, differ, summary }</c>. "No measurable difference" is an answer to report, not a reason to invent one.
    /// </summary>
    public Dictionary<string, object?> ComparePace(TasteResult a, TasteResult b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return TastePace.Compare(a.Pace(), b.Pace(), Named(a), Named(b));
    }

    /// <summary>
    /// Every audience's pace and every pair compared, adjusted for how many pairs there are:
    /// <c>{ audiences, pairs, differ, summary }</c>. For three or more audiences; two can use either call.
    /// </summary>
    public Dictionary<string, object?> ComparePaceAll(TasteResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Length < 2 || results.Any(r => r is null))
            throw new ArgumentException("Taste.comparePaceAll needs two or more profiles, e.g. [t1, t2, t3].", nameof(results));
        return TastePace.CompareAll([.. results.Select(r => (r.Pace(), Named(r)))]);
    }

    private static string Named(TasteResult r) => string.IsNullOrWhiteSpace(r.Label) ? r.Id : $"{r.Id} ({r.Label})";
    #endregion

    #region Fields
    private readonly TasteRegistry registry;
    #endregion
}

/// <summary>
/// Qloo entities and tags as the plain dictionaries scripts read. Shared by <see cref="TasteResult"/> and the MCP
/// layer's profile builder; not itself exposed to the sandbox.
/// </summary>
public static class TasteObjects
{
    /// <summary>Builds the record of one response. Never throws: a failed response is recorded as failed.</summary>
    public static TasteResult Result(string id, string op, string label, QlooResponse response) => new TasteResult
    {
        Id = id,
        Op = op,
        Label = label,
        Request = response.Request,
        Ok = response.Success,
        Error = response.Error,
        Remedy = response.Remedy,
        FromCache = response.FromCache,
        CachedUtc = response.CachedUtc?.ToString("O"),
        RequestId = response.RequestId,
        Warnings = [.. response.Warnings],
        Locality = response is QlooInsightsResponse { Locality: { } l }
            ? new() { ["id"] = l.EntityId, ["name"] = l.Name, ["disambiguation"] = l.Disambiguation }
            : null,
        Entities = response switch
        {
            QlooInsightsResponse i => [.. i.Entities.Select(TasteObjects.Entity)],
            QlooEntitiesResponse e => [.. e.Entities.Select(TasteObjects.Entity)],
            _ => [],
        },
        Tags = response switch
        {
            QlooInsightsResponse i => [.. i.Tags.Select(TasteObjects.Tag)],
            QlooTagsResponse t => [.. t.Tags.Select(TasteObjects.Tag)],
            _ => [],
        },
        Audiences = response is QlooAudiencesResponse a
            ? [.. a.Audiences.Select(x => (object?)new Dictionary<string, object?>
              { ["id"] = x.Id, ["name"] = x.Name, ["type"] = x.Type, ["parents"] = x.Parents.ToArray() })]
            : [],
        Demographics = response is QlooInsightsResponse d ? [.. d.Demographics.Select(e => JsonInterop.ToClr(e))] : [],
        Comparison = response is QlooCompareResponse c ? JsonInterop.ToClr(c.Results) : null,
    };

    /// <summary>
    /// The record of a profile, built from several responses. <see cref="Entities"/> holds every distinctive entity
    /// across the domains and <see cref="Tags"/> the place's aesthetic tags, so a script reads either without
    /// walking the profile.
    /// </summary>
    public static TasteResult ProfileResult(
        string id, string label, Dictionary<string, object?> profile, IReadOnlyList<QlooResponse> responses,
        object?[] distinctive, object?[] placeTags, QlooLocality? locality, IEnumerable<string> extraWarnings)
    {
        var failed = responses.FirstOrDefault(r => !r.Success);
        return new TasteResult
        {
            Id = id,
            Op = "profile",
            Label = label,
            Request = string.Join(" ; ", responses.Select(r => r.Request)),
            Ok = failed is null,
            Error = failed?.Error,
            Remedy = failed?.Remedy,
            FromCache = responses.Count > 0 && responses.All(r => r.FromCache),
            CachedUtc = responses.Where(r => r.CachedUtc is not null).Select(r => r.CachedUtc).Min()?.ToString("O"),
            RequestId = responses.Select(r => r.RequestId).FirstOrDefault(r => r is not null),
            Warnings = [.. responses.SelectMany(r => r.Warnings).Concat(extraWarnings).Distinct()],
            Locality = locality is null ? null : new() { ["id"] = locality.EntityId, ["name"] = locality.Name, ["disambiguation"] = locality.Disambiguation },
            Entities = distinctive,
            Tags = placeTags,
            Profile = profile,
        };
    }


    public static object? Entity(QlooEntity e) => new Dictionary<string, object?>
    {
        ["id"] = e.EntityId,
        ["name"] = e.Name,
        ["type"] = e.Types.FirstOrDefault(),
        ["types"] = e.Types.ToArray(),
        ["disambiguation"] = e.Disambiguation,
        ["popularity"] = e.Popularity,
        ["affinity"] = e.Affinity,
        ["tags"] = e.Tags.Select(Tag).ToArray(),
        ["explainability"] = JsonInterop.ToClr(e.Explainability),
        ["properties"] = JsonInterop.ToClr(e.Properties),
    };

    public static object? Tag(QlooTag t) => new Dictionary<string, object?>
    {
        ["id"] = t.Id, ["name"] = t.Name, ["type"] = t.Type, ["popularity"] = t.Popularity, ["affinity"] = t.Affinity,
    };
}
