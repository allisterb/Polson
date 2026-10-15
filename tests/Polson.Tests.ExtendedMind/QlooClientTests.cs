namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using global::Polson.ExtendedMind.Taste;
using global::Polson.Tests;
using Xunit;

/// <summary>Tests for <see cref="QlooClient"/>, <see cref="QlooQuery"/> and <see cref="QlooParameters"/>.</summary>
/// <remarks>
/// <para>
/// <b>Every fixture here is synthetic</b> — invented names and ids in Qloo's response shapes. Qloo's terms
/// forbid storing its response data in a public repository, so nothing a live call returned is committed.
/// </para>
/// <para>
/// Offline except the Live region, which runs only with <c>POLSON_LIVE_QLOO=1</c> because every call counts
/// against a 10,000-a-month quota.
/// </para>
/// </remarks>
public class QlooClientTests : TestsRuntime
{
    #region Fields
    private const string InsightsWithLocality = """
        {
          "success": true,
          "results": {
            "entities": [
              {
                "name": "The Harbour Choir", "entity_id": "E-0001", "type": "urn:entity", "subtype": "urn:entity:artist",
                "popularity": 0.61, "disambiguation": "Harbour Choir",
                "query": { "affinity": 0.93, "explainability": { "signal.location": 1.0 } },
                "tags": [ { "name": "Gospel", "tag_id": "urn:tag:genre:music:gospel", "type": "urn:tag:genre:music" } ],
                "properties": { "description": "An invented choir." }
              },
              { "name": "Second Act", "entity_id": "E-0002", "type": "urn:entity", "subtype": "urn:entity:artist", "popularity": 0.4,
                "query": { "affinity": 0.71 } }
            ]
          },
          "warnings": [ { "type": "Could not calculate explainability" } ],
          "query": { "localities": { "signal": { "entity_id": "L-01", "name": "Port Sample", "disambiguation": "Port Sample, Examplia",
                                                 "location": { "lat": 6.5, "lon": 3.4 } } } }
        }
        """;

    private const string EmptyInsights = """{ "success": true, "results": { "entities": [] } }""";

    private const string SearchResults = """
        { "results": [ { "name": "Invented Film", "entity_id": "M-77", "types": ["urn:entity:movie"], "disambiguation": "2020",
                         "tags": [ { "name": "Drama", "tag_id": "urn:tag:genre:media:drama", "type": "urn:tag:genre:media" } ] } ] }
        """;

    private const string TagResults = """
        { "success": true, "results": { "tags": [ { "id": "urn:tag:aesthetic_property:qloo:sample_warm", "name": "Sample Warm Palette",
                                                    "type": "urn:tag:aesthetic_property:qloo", "popularity": 0.9 } ] } }
        """;

    private const string AudienceResults = """
        { "success": true, "results": { "audiences": [ { "entity_id": "A-1", "name": "Sample Community", "parents": ["urn:audience:communities"] } ] } }
        """;
    #endregion

    #region Query
    [Fact]
    public void Query_IsCanonicalAndInvariant()
    {
        var a = new QlooQuery { ["take"] = 5, ["filter.type"] = "urn:entity:movie", ["filter.popularity.min"] = 0.9 };
        var b = new QlooQuery { ["filter.popularity.min"] = 0.9, ["filter.type"] = "urn:entity:movie", ["take"] = 5 };

        Assert.Equal(a.ToQueryString(), b.ToQueryString());
        Assert.Equal("filter.popularity.min=0.9&filter.type=urn%3Aentity%3Amovie&take=5", a.ToQueryString());
    }

    [Fact]
    public void Query_JoinsListsWithCommasAndDropsEmptyValues()
    {
        var q = new QlooQuery { ["signal.demographics.age"] = new[] { "45_to_54", "55_and_older" }, ["signal.location.query"] = "" };

        Assert.Equal("45_to_54,55_and_older", q["signal.demographics.age"]);
        Assert.False(q.Has("signal.location.query"));

        q["signal.demographics.age"] = null;
        Assert.Equal(0, q.Count);
    }
    #endregion

    #region Refusals before any request
    [Theory]
    [InlineData("signal.locaton.query", "signal.location.query")]          // a typo with one close match
    [InlineData("filter.popularty.min", "filter.popularity.min")]
    public async Task Insights_RefusesAnUnknownParameterAndSuggestsTheNearOne(string typo, string meant)
    {
        var (client, handler) = MakeClient(EmptyInsights);

        var r = await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:artist", [typo] = "x" });

        Assert.Equal(QlooFailure.InvalidRequest, r.Failure);
        Assert.Contains($"'{typo}'", r.Error);
        Assert.Contains($"'{meant}'", r.Error);
        Assert.Null(handler.Request);
    }

    /// <summary>A name far from every parameter gets no suggestion: a wrong suggestion is obeyed.</summary>
    [Fact]
    public async Task Insights_OffersNoSuggestionWhenNothingIsClose()
    {
        var (client, _) = MakeClient(EmptyInsights);

        var r = await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:artist", ["mood"] = "warm" });

        Assert.Equal(QlooFailure.InvalidRequest, r.Failure);
        Assert.DoesNotContain("Did you mean", r.Error);
    }

    [Theory]
    [InlineData(null, "needs filter.type")]
    [InlineData("urn:entity:music", "urn:entity:artist")]
    [InlineData("urn:entity:food_and_drink", "urn:entity:place")]
    public async Task Insights_RefusesAMissingOrUnknownType(string? type, string expected)
    {
        var (client, handler) = MakeClient(EmptyInsights);

        var r = await client.Insights(new QlooQuery { ["filter.type"] = type, ["take"] = 3 });

        Assert.Equal(QlooFailure.InvalidRequest, r.Failure);
        Assert.Contains(expected, r.Error);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData("signal.demographics.age", "55_plus", "age band")]
    [InlineData("signal.demographics.gender", "women", "male or female")]
    [InlineData("filter.popularity.min", "90", "between 0 and 1")]
    [InlineData("take", "0", "at least 1")]
    [InlineData("operator.filter.tags", "and", "'union' or 'intersection'")]
    [InlineData("feature.explainability", "yes", "true or false")]
    public async Task Insights_RefusesAValueOutsideItsDocumentedSet(string name, string value, string expected)
    {
        var (client, handler) = MakeClient(EmptyInsights);

        var r = await client.Insights(new QlooQuery
        {
            ["filter.type"] = "urn:entity:movie", ["signal.location.query"] = "Port Sample", [name] = value,
        });

        Assert.Equal(QlooFailure.InvalidRequest, r.Failure);
        Assert.Contains(expected, r.Error);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Insights_DemographicsNeedsSomethingToDescribe()
    {
        var (client, handler) = MakeClient(EmptyInsights);

        var r = await client.Insights(new QlooQuery { ["filter.type"] = "urn:demographics" });

        Assert.Equal(QlooFailure.InvalidRequest, r.Failure);
        Assert.Contains("signal.interests.entities", r.Error);
        Assert.Null(handler.Request);
    }
    #endregion

    #region Warnings that do not refuse
    /// <summary>The guide is incomplete, so a parameter it does not list for a type warns rather than refuses.</summary>
    [Fact]
    public async Task Insights_WarnsWhenAFilterIsNotListedForTheType()
    {
        var (client, handler) = MakeClient(InsightsWithLocality);

        var r = await client.Insights(new QlooQuery
        {
            ["filter.type"] = "urn:entity:artist", ["filter.release_year.min"] = 2000, ["signal.location.query"] = "Port Sample",
        });

        Assert.True(r.Success, r.Error);
        Assert.NotNull(handler.Request);
        Assert.Contains(r.Warnings, w => w.Contains("filter.release_year.min") && w.Contains("urn:entity:artist"));
    }

    /// <summary>Measured 2026-10-15: an age band with no place returns the all-time list.</summary>
    [Fact]
    public async Task Insights_WarnsAboutDemographicsWithoutAPlace()
    {
        var (client, _) = MakeClient(InsightsWithLocality);

        var r = await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:movie", ["signal.demographics.age"] = "55_and_older" });

        Assert.True(r.Success);
        Assert.Contains(r.Warnings, w => w.Contains("signal.location.query"));
    }

    [Fact]
    public async Task Insights_AnEmptyAnswerIsASuccessThatSaysWhatItMightMean()
    {
        var (client, _) = MakeClient(EmptyInsights);

        var r = await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:brand", ["signal.location.query"] = "Nowhere" });

        Assert.True(r.Success);
        Assert.True(r.IsEmpty);
        Assert.Contains(r.Warnings, w => w.Contains("no results") && w.Contains("unsupported"));
    }
    #endregion

    #region Request and response
    [Fact]
    public async Task Insights_SendsAGetWithTheKeyInAHeaderAndNeverInTheRequestRecord()
    {
        var (client, handler) = MakeClient(InsightsWithLocality, key: "secret-test-key");

        var r = await client.Insights(new QlooQuery
        {
            ["filter.type"] = "urn:entity:artist", ["signal.location.query"] = "Port Sample", ["take"] = 2,
        });

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/v2/insights", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("secret-test-key", handler.Request.Headers.GetValues("X-Api-Key").Single());
        Assert.DoesNotContain("secret-test-key", handler.Request.RequestUri.ToString());
        Assert.DoesNotContain("secret-test-key", r.Request);
        Assert.Equal("/v2/insights?filter.type=urn%3Aentity%3Aartist&signal.location.query=Port%20Sample&take=2", r.Request);
    }

    [Fact]
    public async Task Insights_ReadsEntitiesLocalityWarningsAndHeaders()
    {
        var (client, _) = MakeClient(InsightsWithLocality, headers: new()
        {
            ["X-Second-RateLimit-Limit"] = "5", ["X-Month-RateLimit-Limit"] = "10000", ["X-Month-RateLimit-Remaining"] = "9001",
            ["x-qloo-request-id"] = "req-123",
        });

        var r = await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:artist", ["signal.location.query"] = "Port Sample" });

        Assert.True(r.Success, r.Error);
        var first = r.Entities[0];
        Assert.Equal("E-0001", first.EntityId);
        Assert.Equal("urn:entity:artist", first.Types[0]);
        Assert.Equal(0.93, first.Affinity);
        Assert.Equal(0.61, first.Popularity);
        Assert.NotNull(first.Explainability);
        Assert.Equal("Gospel", first.Tags.Single().Name);
        Assert.Equal("Port Sample, Examplia", r.Locality!.Disambiguation);
        Assert.Contains("Qloo: Could not calculate explainability", r.Warnings);
        Assert.Equal(9001, r.RateLimit!.MonthlyRemaining);
        Assert.Equal(9001, client.LastRateLimit!.MonthlyRemaining);
        Assert.Equal("req-123", r.RequestId);
    }

    [Fact]
    public async Task Search_ReadsTheTypesList()
    {
        var (client, handler) = MakeClient(SearchResults);

        var r = await client.Search("Invented Film", ["urn:entity:movie"], take: 2);

        Assert.Equal("/search", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Contains("types=urn%3Aentity%3Amovie", handler.Request.RequestUri.Query);
        Assert.Equal(["urn:entity:movie"], r.Entities.Single().Types);
        Assert.Equal("2020", r.Entities.Single().Disambiguation);
    }

    [Fact]
    public async Task Tags_SendsASemanticQueryAndReadsTheTypedTags()
    {
        var (client, handler) = MakeClient(TagResults);

        var r = await client.Tags("warm palette", ["urn:tag:aesthetic_property:qloo"]);

        var sent = handler.Request!.RequestUri!.Query;
        Assert.Contains("feature.semantic_search=true", sent);
        Assert.Contains("filter.query=warm%20palette", sent);
        Assert.Equal("urn:tag:aesthetic_property:qloo", r.Tags.Single().Type);
    }

    [Fact]
    public async Task Audiences_ReadsNamesAndParents()
    {
        var (client, handler) = MakeClient(AudienceResults);

        var r = await client.Audiences("urn:audience:communities");

        Assert.Contains("filter.parents.types=urn%3Aaudience%3Acommunities", handler.Request!.RequestUri!.Query);
        Assert.Equal("Sample Community", r.Audiences.Single().Name);
        Assert.Equal(["urn:audience:communities"], r.Audiences.Single().Parents);
    }

    [Fact]
    public async Task Compare_PrefixesEachSide()
    {
        var (client, handler) = MakeClient("""{ "success": true, "results": { "tags": [] } }""");

        var r = await client.Compare(["E-1"], ["E-2", "E-3"], "urn:entity:movie");

        var sent = handler.Request!.RequestUri!.Query;
        Assert.Contains("a.signal.interests.entities=E-1", sent);
        Assert.Contains("b.signal.interests.entities=E-2%2CE-3", sent);
        Assert.True(r.Success);
        Assert.NotNull(r.Results);
    }
    #endregion

    #region Failures
    [Fact]
    public async Task Unauthorized_NamesTheBaseUrlInTheRemedy()
    {
        var (client, _) = MakeClient("""{ "message": "No API key found in request" }""", HttpStatusCode.Unauthorized);

        var r = await client.AudienceTypes();

        Assert.Equal(QlooFailure.Auth, r.Failure);
        Assert.Equal("No API key found in request", r.Error);
        Assert.Contains("hackathon.api.qloo.com", r.Remedy);
    }

    [Fact]
    public async Task Forbidden_IsAnUnsupportedType()
    {
        var (client, _) = MakeClient("""{ "error": "You do not have permission to access urn:entity:sample" }""", HttpStatusCode.Forbidden);

        var r = await client.Audiences();

        Assert.Equal(QlooFailure.UnsupportedType, r.Failure);
        Assert.Contains("permission", r.Error);
    }

    [Fact]
    public async Task RateLimited_RetriesAndHonoursRetryAfter()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var handler = new StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = Respond("""{ "message": "slow down" }""", HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return limited;
            }
            return Respond(AudienceResults);
        });
        var client = Offline(handler, waits);

        var r = await client.Audiences();

        Assert.True(r.Success, r.Error);
        Assert.Equal(2, calls);
        Assert.Contains(TimeSpan.FromSeconds(2), waits);
    }

    [Fact]
    public async Task MonthlyQuotaSpent_IsNotRetried()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            var spent = Respond("""{ "message": "quota" }""", HttpStatusCode.TooManyRequests);
            spent.Headers.Add("X-Month-RateLimit-Remaining", "0");
            return spent;
        });
        var client = Offline(handler, []);

        var r = await client.Audiences();

        Assert.Equal(QlooFailure.QuotaExhausted, r.Failure);
        Assert.Equal(1, calls);
        Assert.Contains("monthly quota", r.Remedy);
    }

    [Fact]
    public async Task ServiceError_IsRetriedThenReported()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return Respond("<html>bad gateway</html>", HttpStatusCode.BadGateway); });
        var client = Offline(handler, []);

        var r = await client.Audiences();

        Assert.Equal(QlooFailure.ServiceError, r.Failure);
        Assert.Equal(QlooClient.MaxAttempts, calls);
        Assert.Equal("<html>bad gateway</html>", r.RawBody);
    }

    [Fact]
    public async Task MalformedBody_KeepsTheBody()
    {
        var (client, _) = MakeClient("{ not json");

        var r = await client.Audiences();

        Assert.Equal(QlooFailure.MalformedResponse, r.Failure);
        Assert.Equal("{ not json", r.RawBody);
    }

    [Fact]
    public async Task RequestsArePacedUnderTheRateLimit()
    {
        var waits = new List<TimeSpan>();
        var client = Offline(new StubHandler(_ => Respond(AudienceResults)), waits);
        client.Spacing = TimeSpan.FromMilliseconds(220);

        await client.Audiences();
        await client.Audiences();

        Assert.Contains(waits, w => w > TimeSpan.Zero && w <= TimeSpan.FromMilliseconds(220));
    }

    [Theory]
    [InlineData("http://hackathon.api.qloo.com")]
    [InlineData("not a url")]
    public void Constructor_RefusesABaseUrlThatIsNotHttps(string baseUrl) =>
        Assert.Throws<ArgumentException>(() => new QlooClient("k", new HttpClient(), baseUrl));
    #endregion

    #region Cache and budget
    [Fact]
    public async Task Cache_ARepeatedQuestionIsAnsweredWithoutAsking()
    {
        using var dir = new TempDir();
        var calls = 0;
        var budget = new QlooBudget(10);
        var client = Offline(new StubHandler(_ => { calls++; return Respond(InsightsWithLocality); }), [], cache: new QlooCache(dir.Path), budget: budget);
        var query = new QlooQuery { ["filter.type"] = "urn:entity:artist", ["signal.location.query"] = "Port Sample" };

        var fresh = await client.Insights(query);
        var again = await client.Insights(query.Clone());

        Assert.Equal(1, calls);
        Assert.False(fresh.FromCache);
        Assert.True(again.FromCache);
        Assert.NotNull(again.CachedUtc);
        Assert.Equal(fresh.Entities.Select(e => e.EntityId), again.Entities.Select(e => e.EntityId));
        Assert.Equal(fresh.Locality, again.Locality);
        Assert.Contains("Qloo: Could not calculate explainability", again.Warnings);
        Assert.Equal((1, 1), (budget.Spent, budget.CacheHits));
    }

    [Fact]
    public async Task Cache_ADifferentQuestionOrServiceIsADifferentEntry()
    {
        using var dir = new TempDir();
        var calls = 0;
        var client = Offline(new StubHandler(_ => { calls++; return Respond(InsightsWithLocality); }), [], cache: new QlooCache(dir.Path));

        await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:artist", ["signal.location.query"] = "Port Sample" });
        await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:artist", ["signal.location.query"] = "Other Town" });

        Assert.Equal(2, calls);
        Assert.NotEqual(QlooCache.KeyOf("https://hackathon.api.qloo.com", "/v2/tags"), QlooCache.KeyOf("https://api.qloo.com", "/v2/tags"));
    }

    [Fact]
    public async Task Cache_AFailureIsNotKeptAndTheKeyIsNeverWritten()
    {
        using var dir = new TempDir();
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1 ? Respond("{}", HttpStatusCode.Unauthorized) : Respond(AudienceResults));
        var client = Offline(handler, [], key: "secret-test-key", cache: new QlooCache(dir.Path));

        var failed = await client.Audiences();
        var ok = await client.Audiences();
        var hit = await client.Audiences();

        Assert.Equal(QlooFailure.Auth, failed.Failure);
        Assert.True(ok.Success && !ok.FromCache);
        Assert.True(hit.FromCache);
        Assert.Equal(2, calls);
        Assert.All(System.IO.Directory.GetFiles(dir.Path), f => Assert.DoesNotContain("secret-test-key", System.IO.File.ReadAllText(f)));
    }

    [Fact]
    public async Task Cache_AnUnreadableEntryIsAMissAndIsRewritten()
    {
        using var dir = new TempDir();
        var cache = new QlooCache(dir.Path);
        var calls = 0;
        var client = Offline(new StubHandler(_ => { calls++; return Respond(AudienceResults); }), [], cache: cache);
        var key = QlooCache.KeyOf(QlooClient.HackathonBaseUrl, "/v2/audiences?take=50");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir.Path, key + ".json"), "{ corrupt");

        var r = await client.Audiences();

        Assert.True(r.Success && !r.FromCache, r.Error);
        Assert.Equal(1, calls);
        Assert.NotNull(cache.Get(key));
    }

    /// <summary>A query refused by the validator sends nothing, so it costs nothing and is not cached.</summary>
    [Fact]
    public async Task Budget_ARefusedQueryIsNotCharged()
    {
        var budget = new QlooBudget(5);
        var client = Offline(new StubHandler(_ => Respond(EmptyInsights)), [], budget: budget);

        var r = await client.Insights(new QlooQuery { ["filter.type"] = "urn:entity:music" });

        Assert.Equal(QlooFailure.InvalidRequest, r.Failure);
        Assert.Equal(0, budget.Spent);
    }

    [Fact]
    public async Task Budget_WhenSpentRefusesButStillServesTheCache()
    {
        using var dir = new TempDir();
        var calls = 0;
        var budget = new QlooBudget(1);
        var client = Offline(new StubHandler(_ => { calls++; return Respond(AudienceResults); }), [], cache: new QlooCache(dir.Path), budget: budget);

        var first = await client.Audiences();
        var other = await client.Audiences("urn:audience:life_stage");
        var repeat = await client.Audiences();

        Assert.True(first.Success);
        Assert.Equal(QlooFailure.BudgetExhausted, other.Failure);
        Assert.Contains("allowance of 1", other.Error);
        Assert.True(repeat.FromCache);
        Assert.Equal(1, calls);
        Assert.Equal(1, budget.Refused);
    }

    /// <summary>Every request sent draws on Qloo's quota, so a retry is charged too.</summary>
    [Fact]
    public async Task Budget_ChargesEachAttempt()
    {
        var calls = 0;
        var budget = new QlooBudget(10);
        var client = Offline(new StubHandler(_ => ++calls == 1 ? Respond("{}", HttpStatusCode.TooManyRequests) : Respond(AudienceResults)), [], budget: budget);

        var r = await client.Audiences();

        Assert.True(r.Success);
        Assert.Equal(2, budget.Spent);
    }

    [Fact]
    public async Task Budget_StopsRetryingWhenTheAllowanceRunsOut()
    {
        var calls = 0;
        var budget = new QlooBudget(1);
        var client = Offline(new StubHandler(_ => { calls++; return Respond("{}", HttpStatusCode.BadGateway); }), [], budget: budget);

        var r = await client.Audiences();

        Assert.Equal(1, calls);
        Assert.Equal(QlooFailure.BudgetExhausted, r.Failure);
    }

    /// <summary>Once Qloo reports the month at or below the reserve, nothing more is sent, whatever the run has left.</summary>
    [Fact]
    public async Task Budget_TheMonthlyReserveIsNotSpent()
    {
        using var dir = new TempDir();
        var calls = 0;
        var budget = new QlooBudget(100, monthlyReserve: 1000);
        var client = Offline(new StubHandler(_ =>
        {
            calls++;
            var r = Respond(AudienceResults);
            r.Headers.Add("X-Month-RateLimit-Remaining", "900");
            return r;
        }), [], cache: new QlooCache(dir.Path), budget: budget);

        var first = await client.Audiences();
        var next = await client.Audiences("urn:audience:communities");
        var cached = await client.Audiences();

        Assert.True(first.Success);
        Assert.True(budget.ReserveReached);
        Assert.Equal(900, budget.MonthlyRemaining);
        Assert.Equal(QlooFailure.BudgetExhausted, next.Failure);
        Assert.Contains("reserve", next.Error);
        Assert.True(cached.FromCache);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Budget_ZeroTurnsQlooOff()
    {
        var calls = 0;
        var client = Offline(new StubHandler(_ => { calls++; return Respond(AudienceResults); }), [], budget: new QlooBudget(0));

        var r = await client.Audiences();

        Assert.Equal(QlooFailure.BudgetExhausted, r.Failure);
        Assert.Contains("turned off", r.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Budget_ChargesAreExactUnderConcurrency()
    {
        var budget = new QlooBudget(50);

        var charged = Enumerable.Range(0, 200).AsParallel().Count(_ => budget.TryCharge() is null);

        Assert.Equal(50, charged);
        Assert.Equal(50, budget.Spent);
        Assert.Equal(150, budget.Refused);
    }

    [Fact]
    public void Budget_RefusesNegativeSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QlooBudget(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QlooBudget(10, -1));
    }
    #endregion

    #region Live
    /// <summary>
    /// One real call each. Asserts on shape and on facts a director would check — never on data, which is
    /// not committed. Runs only with <c>POLSON_LIVE_QLOO=1</c>.
    /// </summary>
    [Fact]
    public async Task Live_AudienceTypesAndAPlacedQuery()
    {
        if (Environment.GetEnvironmentVariable("POLSON_LIVE_QLOO") != "1") return;
        var key = config["ApiKeys:Qloo"];
        Assert.False(string.IsNullOrWhiteSpace(key), "ApiKeys:Qloo is not set in testappsettings.json");
        using var client = new QlooClient(key!, baseUrl: config["Qloo:BaseUrl"]);

        var types = await client.AudienceTypes();
        Assert.True(types.Success, $"{types.Failure}: {types.Error}");
        Assert.Contains("urn:audience:communities", types.Types);

        var placed = await client.Insights(new QlooQuery
        {
            ["filter.type"] = "urn:entity:artist", ["signal.location.query"] = "Lagos", ["take"] = 3,
        });
        Assert.True(placed.Success, $"{placed.Failure}: {placed.Error}");
        Assert.NotEmpty(placed.Entities);
        Assert.Contains("Nigeria", placed.Locality?.Disambiguation ?? "");
        Assert.NotNull(placed.RateLimit?.MonthlyRemaining);
    }
    #endregion

    #region Helpers
    private static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static QlooClient Offline(StubHandler handler, List<TimeSpan> waits, string key = "test-key", QlooCache? cache = null, QlooBudget? budget = null) =>
        new(key, new HttpClient(handler), QlooClient.HackathonBaseUrl, cache, budget)
        {
            Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; },
            Spacing = TimeSpan.Zero,
        };

    private static (QlooClient Client, StubHandler Handler) MakeClient(
        string body, HttpStatusCode status = HttpStatusCode.OK, string key = "test-key", Dictionary<string, string>? headers = null)
    {
        var handler = new StubHandler(_ =>
        {
            var response = Respond(body, status);
            foreach (var (name, value) in headers ?? []) response.Headers.Add(name, value);
            return response;
        });
        return (Offline(handler, [], key), handler);
    }

    /// <summary>A fresh cache directory per test, removed afterwards.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("polson-qloo-").FullName;

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(responder(request));
        }
    }
    #endregion
}
