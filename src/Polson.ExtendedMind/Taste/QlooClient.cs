namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Typed client for Qloo's taste graph: Insights, entity and tag lookup, audiences, and audience comparison.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written against Qloo's REST API rather than wrapping <c>@qloo/qloo-harness</c>, which is a whole
/// terminal agent, not a client. Its request patterns were the reference (MIT, © 2026 Qloo; ledgered in
/// <c>reference/README.md</c>): comma-joined lists on a GET, the <c>X-Api-Key</c> header, retry on 429 and
/// 5xx with jittered backoff that honours <c>Retry-After</c>.
/// </para>
/// <para>
/// Nothing here throws for a remote fault. Every call returns a <see cref="QlooResponse"/> with a classified
/// <see cref="QlooFailure"/> and a <see cref="QlooResponse.Remedy"/> — the contract
/// <see cref="ParallelSearch.ParallelClient"/> keeps. An Insights query is checked by
/// <see cref="QlooParameters.CheckInsights"/> first, because Qloo answers an unsupported parameter with an
/// empty 200 rather than an error.
/// </para>
/// <para>
/// <b>Quota.</b> The hackathon key allows five requests a second and 10,000 a month (headers, 2026-10-15).
/// Requests are paced under the first; the second is reported on every response in
/// <see cref="QlooResponse.RateLimit"/> and in <see cref="LastRateLimit"/>, and fed to the
/// <see cref="QlooBudget"/>, whose monthly reserve refuses before the month runs out.
/// </para>
/// <para>
/// <b>Cache and budget sit inside the request path</b>, not in a layer above as they do for images: a cached
/// answer is a stored body re-read by the same parser, and a budget charge has to happen per request sent —
/// retries included — which only this loop can see. Without either (the default) it is a plain client.
/// </para>
/// <para>
/// Not exposed to the JavaScript sandbox. The record of what was asked and why belongs to the MCP layer above.
/// </para>
/// </remarks>
public sealed class QlooClient : Runtime, IDisposable
{
    #region Constructors
    /// <param name="apiKey">Read from <c>ApiKeys:Qloo</c>. Sent as <c>X-Api-Key</c> on each request, never logged.</param>
    /// <param name="httpClient">Optional. Neither mutated nor disposed; the key travels on the request.</param>
    /// <param name="baseUrl">From <c>Qloo:BaseUrl</c>. A hackathon key works only against <see cref="HackathonBaseUrl"/>.</param>
    /// <param name="cache">Optional. Answers already fetched are served from it and cost nothing.</param>
    /// <param name="budget">Optional. Without one, requests are unlimited.</param>
    public QlooClient(string apiKey, HttpClient? httpClient = null, string? baseUrl = null, QlooCache? cache = null, QlooBudget? budget = null)
    {
        Cache = cache;
        Budget = budget;
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (apiKey.Any(char.IsControl))
            throw new ArgumentException("The Qloo API key contains a control character.", nameof(apiKey));

        this.apiKey = apiKey.Trim();
        this.baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? HackathonBaseUrl : baseUrl.Trim()).TrimEnd('/');
        if (!Uri.TryCreate(this.baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new ArgumentException($"Qloo:BaseUrl must be an absolute https URL; '{baseUrl}' is not.", nameof(baseUrl));

        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }
    #endregion

    #region Properties
    public const string HackathonBaseUrl = "https://hackathon.api.qloo.com";

    /// <summary>Per-attempt deadline. Qloo's own example answers in under 100 ms; Insights with many signals takes seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public const int MaxAttempts = 3;

    /// <summary>Longest wait between attempts, whatever <c>Retry-After</c> asks for.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(8);

    /// <summary>Spacing between request starts: under the five-a-second limit with a margin.</summary>
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromMilliseconds(220);

    /// <summary>Cap on the body kept in <see cref="QlooResponse.RawBody"/> after a failure.</summary>
    public const int MaxRetainedBodyChars = 4000;

    /// <summary>The rate-limit headers from the most recent response, or null before the first.</summary>
    public QlooRateLimit? LastRateLimit { get; private set; }

    public QlooCache? Cache { get; }

    public QlooBudget? Budget { get; }

    /// <summary>How long the client waits. A test replaces it so retries and pacing cost no wall-clock time.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    internal TimeSpan Spacing { get; set; } = DefaultSpacing;
    #endregion

    #region Methods
    /// <summary>
    /// <c>/v2/insights</c>: recommendations, taste analysis, demographics or a heatmap, by <c>filter.type</c>.
    /// </summary>
    /// <remarks>
    /// The query is checked first; a refusal costs no request. An empty answer is a success with
    /// <see cref="QlooInsightsResponse.IsEmpty"/> set and a warning, because Qloo answers an unsupported
    /// parameter the same way.
    /// </remarks>
    public async Task<QlooInsightsResponse> Insights(QlooQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (refusal, warnings) = QlooParameters.CheckInsights(query);
        if (refusal is not null) return Refuse<QlooInsightsResponse>(refusal, "/v2/insights", query, warnings);

        var response = await Get<QlooInsightsResponse>("/v2/insights", query, (r, root) =>
        {
            var results = root.TryGetProperty("results", out var res) ? res : default;
            r.Entities = QlooJson.Array(results, "entities").Select(QlooEntity.From).OfType<QlooEntity>().ToArray();
            r.Tags = QlooJson.Array(results, "tags").Select(QlooTag.From).OfType<QlooTag>().ToArray();
            r.Demographics = QlooJson.Array(results, "demographics").Select(e => e.Clone()).ToArray();
            r.Heatmap = QlooJson.Array(results, "heatmap").Select(e => e.Clone()).ToArray();
            r.Locality = Locality(root);
        }, cancellationToken);

        response.Warn(warnings);
        if (response.IsEmpty)
        {
            response.Warn("Qloo returned no results. It also answers this way when a parameter is unsupported for "
                        + "the filter.type, or when the hackathon data has nothing for a place — broaden the query "
                        + "before concluding this audience likes nothing.");
        }
        return response;
    }

    /// <summary><c>/search</c>: entities by name, for resolving "Nollywood" or a film title to an id.</summary>
    /// <param name="types">Entity URNs to restrict to, such as <c>urn:entity:movie</c>.</param>
    public Task<QlooEntitiesResponse> Search(
        string name, IEnumerable<string>? types = null, int take = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(Refuse<QlooEntitiesResponse>("Search needs a name.", "/search", null));
        var query = new QlooQuery().Set("query", name).Set("types", types?.ToArray()).Set("take", Math.Clamp(take, 1, 50));
        return Get<QlooEntitiesResponse>("/search", query,
            (r, root) => r.Entities = QlooJson.Array(root, "results").Select(QlooEntity.From).OfType<QlooEntity>().ToArray(),
            cancellationToken);
    }

    /// <summary><c>/entities</c>: full records for known ids.</summary>
    public Task<QlooEntitiesResponse> Entities(IEnumerable<string> entityIds, CancellationToken cancellationToken = default)
    {
        var ids = (entityIds ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct().ToArray();
        if (ids.Length == 0) return Task.FromResult(Refuse<QlooEntitiesResponse>("Entities needs at least one id.", "/entities", null));
        return Get<QlooEntitiesResponse>("/entities", new QlooQuery().Set("entity_ids", ids),
            (r, root) => r.Entities = Results(root, "entities").Select(QlooEntity.From).OfType<QlooEntity>().ToArray(),
            cancellationToken);
    }

    /// <summary><c>/v2/tags</c>: tags matching words, such as "warm palette".</summary>
    /// <param name="semantic">Match by meaning rather than by name. What a designer's word needs.</param>
    /// <param name="tagTypes">Tag type URNs to restrict to, such as <c>urn:tag:aesthetic_property:qloo</c>.</param>
    public Task<QlooTagsResponse> Tags(
        string words, IEnumerable<string>? tagTypes = null, bool semantic = true, int take = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(words)) return Task.FromResult(Refuse<QlooTagsResponse>("Tags needs words to match.", "/v2/tags", null));
        var query = new QlooQuery().Set("filter.query", words).Set("feature.semantic_search", semantic)
            .Set("filter.tag.types", tagTypes?.ToArray()).Set("take", Math.Clamp(take, 1, 50));
        return Get<QlooTagsResponse>("/v2/tags", query,
            (r, root) => r.Tags = Results(root, "tags").Select(QlooTag.From).OfType<QlooTag>().ToArray(),
            cancellationToken);
    }

    /// <summary><c>/v2/audiences</c>: audiences, optionally under one type such as <c>urn:audience:communities</c>.</summary>
    public Task<QlooAudiencesResponse> Audiences(string? parentType = null, int take = 50, CancellationToken cancellationToken = default) =>
        Get<QlooAudiencesResponse>("/v2/audiences",
            new QlooQuery().Set("filter.parents.types", parentType).Set("take", Math.Clamp(take, 1, 100)),
            (r, root) => r.Audiences = Results(root, "audiences").Select(QlooAudience.From).OfType<QlooAudience>().ToArray(),
            cancellationToken);

    /// <summary><c>/v2/audiences/types</c>: the audience type URNs.</summary>
    public Task<QlooTypesResponse> AudienceTypes(CancellationToken cancellationToken = default) =>
        Types("/v2/audiences/types", "audience_types", cancellationToken);

    /// <summary><c>/v2/tags/types</c>: the tag type URNs.</summary>
    public Task<QlooTypesResponse> TagTypes(CancellationToken cancellationToken = default) =>
        Types("/v2/tags/types", "tag_types", cancellationToken);

    /// <summary><c>/v2/analysis/compare</c>: what separates two sets of entities' audiences.</summary>
    /// <param name="filterType">An Insights type to compare within, such as <c>urn:entity:movie</c>; null for tags.</param>
    public Task<QlooCompareResponse> Compare(
        IEnumerable<string> a, IEnumerable<string> b, string? filterType = null, int take = 20,
        CancellationToken cancellationToken = default)
    {
        var left = (a ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        var right = (b ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        if (left.Length == 0 || right.Length == 0)
            return Task.FromResult(Refuse<QlooCompareResponse>("Compare needs at least one entity id on each side.", "/v2/analysis/compare", null));
        if (filterType is not null && !QlooParameters.InsightsTypes.Contains(filterType))
            return Task.FromResult(Refuse<QlooCompareResponse>(
                $"filter.type '{filterType}' is not one Qloo accepts.{QlooParameters.Suggest(filterType, QlooParameters.InsightsTypes)}",
                "/v2/analysis/compare", null));

        var query = new QlooQuery().Set("a.signal.interests.entities", left).Set("b.signal.interests.entities", right)
            .Set("filter.type", filterType).Set("take", Math.Clamp(take, 1, 50));
        return Get<QlooCompareResponse>("/v2/analysis/compare", query,
            (r, root) => r.Results = root.TryGetProperty("results", out var res) ? res.Clone() : null,
            cancellationToken);
    }

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
        pacing.Dispose();
    }
    #endregion

    #region Internal methods
    private Task<QlooTypesResponse> Types(string path, string field, CancellationToken cancellationToken) =>
        Get<QlooTypesResponse>(path, new QlooQuery(), (r, root) =>
        {
            var list = Results(root, field).Any() ? Results(root, field) : QlooJson.Array(root, "results");
            r.Types = list.Select(t => t.ValueKind == JsonValueKind.String ? t.GetString() : QlooJson.String(t, "type", "id", "name"))
                .OfType<string>().ToArray();
        }, cancellationToken);

    /// <summary>
    /// Issues one GET with pacing and bounded retry, classifies the outcome, and parses on success. The path
    /// every call takes.
    /// </summary>
    private async Task<TResponse> Get<TResponse>(
        string path, QlooQuery query, Action<TResponse, JsonElement> parse, CancellationToken cancellationToken)
        where TResponse : QlooResponse, new()
    {
        var stopwatch = Stopwatch.StartNew();
        var request = Provenance(path, query);
        var url = baseUrl + path + (query.Count > 0 ? "?" + query.ToQueryString() : string.Empty);
        var key = QlooCache.KeyOf(baseUrl, request);

        if (Cache?.Get(key) is { } hit)
        {
            var cached = Read(hit.Body, 200, hit.RequestId, null, request, parse, stopwatch);
            if (cached.Success)
            {
                Budget?.CountHit();
                cached.FromCache = true;
                cached.CachedUtc = hit.RetrievedUtc;
                return cached;
            }
            // An entry the parser cannot read is not an answer; ask again, and the success rewrites it.
        }

        for (var attempt = 1; ; attempt++)
        {
            HttpStatusCode status;
            string body;
            QlooRateLimit? limit;
            string? requestId;
            TimeSpan? retryAfter;

            if (Budget?.TryCharge() is string refusal)
            {
                var refused = Failed<TResponse>(QlooFailure.BudgetExhausted, refusal, request, stopwatch);
                refused.RateLimit = LastRateLimit;
                return refused;
            }

            try
            {
                await Pace(cancellationToken);
                using var deadline = new CancellationTokenSource(DefaultTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.Add("X-Api-Key", apiKey);
                message.Headers.Accept.ParseAdd("application/json");

                using var response = await http.SendAsync(message, linked.Token);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(linked.Token);
                limit = ReadRateLimit(response);
                requestId = Header(response, "x-qloo-request-id") ?? Header(response, "x-request-id");
                retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failed<TResponse>(QlooFailure.Cancelled, "The call was cancelled.", request, stopwatch);
            }
            catch (OperationCanceledException ex)
            {
                if (attempt < MaxAttempts) { await Backoff(attempt, null, cancellationToken); continue; }
                return Failed<TResponse>(QlooFailure.Timeout, ex.Message, request, stopwatch);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < MaxAttempts) { await Backoff(attempt, null, cancellationToken); continue; }
                return Failed<TResponse>(QlooFailure.Network, ex.Message, request, stopwatch);
            }

            if (limit is not null) LastRateLimit = limit;
            Budget?.Observe(limit);
            var code = (int)status;

            if (code is < 200 or > 299)
            {
                var failure = Classify(status, limit);
                if (failure is QlooFailure.RateLimited or QlooFailure.ServiceError && attempt < MaxAttempts)
                {
                    await Backoff(attempt, retryAfter, cancellationToken);
                    continue;
                }

                var error = ErrorMessage(body);
                Error("Qloo {Path} failed with {Status} ({RequestId}): {Message}", path, code, requestId ?? "no request id", error);
                var failed = Failed<TResponse>(failure, error, request, stopwatch, code, body);
                failed.RequestId = requestId;
                failed.RateLimit = limit;
                return failed;
            }

            var result = Read(body, code, requestId, limit, request, parse, stopwatch);
            if (result.Success)
                Cache?.Put(key, new CachedQlooAnswer { Request = request, Body = body, RetrievedUtc = DateTime.UtcNow, RequestId = requestId });
            return result;
        }
    }

    /// <summary>Parses a successful body, fresh or cached. The one reader of Qloo's JSON.</summary>
    private static TResponse Read<TResponse>(
        string body, int code, string? requestId, QlooRateLimit? limit, string request,
        Action<TResponse, JsonElement> parse, Stopwatch stopwatch)
        where TResponse : QlooResponse, new()
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var result = new TResponse
            {
                Success = true,
                Failure = QlooFailure.None,
                StatusCode = code,
                RequestId = requestId,
                RateLimit = limit,
                Request = request,
            };
            parse(result, document.RootElement);
            result.Warn(ServiceWarnings(document.RootElement));
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            var failed = Failed<TResponse>(QlooFailure.MalformedResponse, ex.Message, request, stopwatch, code, body);
            failed.RequestId = requestId;
            return failed;
        }
    }

    /// <summary>Keeps request starts at least <see cref="Spacing"/> apart, across concurrent callers.</summary>
    private async Task Pace(CancellationToken cancellationToken)
    {
        await pacing.WaitAsync(cancellationToken);
        try
        {
            var wait = lastStart + Spacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Delay(wait, cancellationToken);
            lastStart = DateTimeOffset.UtcNow;
        }
        finally
        {
            pacing.Release();
        }
    }

    private Task Backoff(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var exponential = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) * (0.75 + Random.Shared.NextDouble() * 0.5));
        var wait = retryAfter is { } r && r > TimeSpan.Zero ? r : exponential;
        return Delay(wait < MaxRetryDelay ? wait : MaxRetryDelay, cancellationToken);
    }

    private static QlooFailure Classify(HttpStatusCode status, QlooRateLimit? limit) => status switch
    {
        HttpStatusCode.Unauthorized => QlooFailure.Auth,
        HttpStatusCode.Forbidden => QlooFailure.UnsupportedType,
        HttpStatusCode.TooManyRequests => limit?.MonthlyRemaining is 0 ? QlooFailure.QuotaExhausted : QlooFailure.RateLimited,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => QlooFailure.ServiceError,
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => QlooFailure.InvalidRequest,
        >= HttpStatusCode.InternalServerError => QlooFailure.ServiceError,
        _ => QlooFailure.Unknown,
    };

    /// <summary>Qloo's own <c>warnings</c>, such as "Could not calculate explainability".</summary>
    private static IEnumerable<string> ServiceWarnings(JsonElement root) =>
        QlooJson.Array(root, "warnings")
            .Select(w => w.ValueKind == JsonValueKind.String
                ? w.GetString()
                : string.Join(": ", new[] { QlooJson.String(w, "type"), QlooJson.String(w, "message") }.OfType<string>()))
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => $"Qloo: {w}");

    /// <summary>Where a single <c>signal.location.query</c> landed. Read from both spellings Qloo has used.</summary>
    private static QlooLocality? Locality(JsonElement root)
    {
        if (!root.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "localities", "locality" })
            if (query.TryGetProperty(name, out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("signal", out var s))
                return QlooLocality.From(s);
        return null;
    }

    /// <summary>A list under <c>results.{field}</c>, or <c>results</c> itself when it is the list.</summary>
    private static IEnumerable<JsonElement> Results(JsonElement root, string field)
    {
        if (!root.TryGetProperty("results", out var results)) return [];
        return results.ValueKind == JsonValueKind.Array ? results.EnumerateArray() : QlooJson.Array(results, field);
    }

    private static QlooRateLimit? ReadRateLimit(HttpResponseMessage response)
    {
        int? Int(string name) => int.TryParse(Header(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        double? Num(string name) => double.TryParse(Header(response, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        var limit = new QlooRateLimit(Int("X-Second-RateLimit-Limit"), Int("X-Second-RateLimit-Remaining"),
            Int("X-Month-RateLimit-Limit"), Int("X-Month-RateLimit-Remaining"), Num("X-Month-RateLimit-Reset"));
        return limit is (null, null, null, null, null) ? null : limit;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>The message from an error body, whichever shape it has, or the truncated body itself.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (QlooJson.String(root, "message", "error", "detail") is string flat) return flat;
                if (root.TryGetProperty("error", out var e) && QlooJson.String(e, "message", "detail") is string nested) return nested;
            }
        }
        catch (JsonException)
        {
            // An HTML page from a proxy, say. The body is the best there is.
        }
        return Truncate(body) ?? string.Empty;
    }

    private static string Provenance(string path, QlooQuery? query) =>
        query is { Count: > 0 } ? $"{path}?{query.ToQueryString()}" : path;

    private static T Refuse<T>(string reason, string path, QlooQuery? query, IEnumerable<string>? warnings = null)
        where T : QlooResponse, new()
    {
        var refused = new T { Success = false, Failure = QlooFailure.InvalidRequest, Error = reason, Request = Provenance(path, query) };
        if (warnings is not null) refused.Warn(warnings);
        return refused;
    }

    private static T Failed<T>(QlooFailure failure, string? error, string request, Stopwatch stopwatch, int? status = null, string? raw = null)
        where T : QlooResponse, new() =>
        new()
        {
            Success = false,
            Failure = failure,
            Error = error,
            Request = request,
            StatusCode = status,
            RawBody = Truncate(raw),
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };

    private static string? Truncate(string? body) => body is null
        ? null
        : body.Length <= MaxRetainedBodyChars ? body : body[..MaxRetainedBodyChars] + "…[truncated]";
    #endregion

    #region Fields
    private readonly string apiKey;
    private readonly string baseUrl;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim pacing = new(1, 1);
    private DateTimeOffset lastStart = DateTimeOffset.MinValue;
    #endregion
}
