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

    public string RetrievedUtc { get; init; } = DateTime.UtcNow.ToString("O");
    #endregion

    #region Methods
    /// <summary>The caption line for this answer: what was asked of Qloo, and when.</summary>
    public string Cite() =>
        $"Qloo Insights API, {Request} ({(FromCache ? "cached, retrieved " + CachedUtc : "retrieved " + RetrievedUtc)}"
        + (RequestId is null ? ")" : $", request {RequestId})");

    public override string ToString() => $"{Id} [{Op}] {Label} ({(Ok ? "ok" : "failed")})";

    /// <summary>Builds the record of one response. Never throws: a failed response is recorded as failed.</summary>
    public static TasteResult From(string id, string op, string label, QlooResponse response) => new()
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
            QlooInsightsResponse i => [.. i.Entities.Select(EntityObject)],
            QlooEntitiesResponse e => [.. e.Entities.Select(EntityObject)],
            _ => [],
        },
        Tags = response switch
        {
            QlooInsightsResponse i => [.. i.Tags.Select(TagObject)],
            QlooTagsResponse t => [.. t.Tags.Select(TagObject)],
            _ => [],
        },
        Audiences = response is QlooAudiencesResponse a
            ? [.. a.Audiences.Select(x => (object?)new Dictionary<string, object?>
              { ["id"] = x.Id, ["name"] = x.Name, ["type"] = x.Type, ["parents"] = x.Parents.ToArray() })]
            : [],
        Demographics = response is QlooInsightsResponse d ? [.. d.Demographics.Select(e => JsonInterop.ToClr(e))] : [],
        Comparison = response is QlooCompareResponse c ? JsonInterop.ToClr(c.Results) : null,
    };

    internal static object? EntityObject(QlooEntity e) => new Dictionary<string, object?>
    {
        ["id"] = e.EntityId,
        ["name"] = e.Name,
        ["type"] = e.Types.FirstOrDefault(),
        ["types"] = e.Types.ToArray(),
        ["disambiguation"] = e.Disambiguation,
        ["popularity"] = e.Popularity,
        ["affinity"] = e.Affinity,
        ["tags"] = e.Tags.Select(TagObject).ToArray(),
        ["explainability"] = JsonInterop.ToClr(e.Explainability),
        ["properties"] = JsonInterop.ToClr(e.Properties),
    };

    internal static object? TagObject(QlooTag t) => new Dictionary<string, object?>
    {
        ["id"] = t.Id, ["name"] = t.Name, ["type"] = t.Type, ["popularity"] = t.Popularity, ["affinity"] = t.Affinity,
    };
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
            var result = TasteResult.From($"t{results.Count + 1}", op, label, response);
            results.Add(result);
            return result;
        }
    }

    public TasteResult? Get(string id)
    {
        lock (results) return results.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }
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
    #endregion

    #region Fields
    private readonly TasteRegistry registry;
    #endregion
}
