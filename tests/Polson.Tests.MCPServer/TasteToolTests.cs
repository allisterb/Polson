namespace Polson.Tests.MCPServer;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Polson.ExtendedMind.Taste;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// The <c>Taste</c> MCP tool in-process: what it refuses before sending, what it returns, and that a script reads
/// the answer back through the <c>Taste</c> global.
/// </summary>
/// <remarks>
/// <para>
/// Every Qloo response here is <b>synthetic</b> — invented names in Qloo's shapes — because Qloo's terms forbid
/// storing its responses in a public repository.
/// </para>
/// <para>
/// In its own collection because it writes the static <see cref="DrawingMcpTools.TasteClient"/>, and xUnit runs
/// test classes in parallel.
/// </para>
/// </remarks>
[Collection(TasteCollection.Name)]
public class TasteToolTests : TestsRuntime, IDisposable
{
    #region Fields
    private const string Insights = """
        {
          "success": true,
          "results": { "entities": [
            { "name": "Sample Choir", "entity_id": "E-1", "type": "urn:entity", "subtype": "urn:entity:artist", "popularity": 0.6,
              "query": { "affinity": 0.9 }, "tags": [ { "name": "Gospel", "tag_id": "urn:tag:genre:music:gospel", "type": "urn:tag:genre:music" } ] }
          ] },
          "query": { "localities": { "signal": { "entity_id": "L-1", "name": "Port Sample", "disambiguation": "Port Sample, Examplia" } } }
        }
        """;

    private const string Search = """
        { "results": [
          { "name": "Twin Title", "entity_id": "M-1", "types": ["urn:entity:movie"], "disambiguation": "1999" },
          { "name": "Twin Title", "entity_id": "M-2", "types": ["urn:entity:movie"], "disambiguation": "2021" }
        ] }
        """;

    private readonly QlooClient? previous = DrawingMcpTools.TasteClient;
    private int calls;
    #endregion

    #region Methods
    public void Dispose()
    {
        DrawingMcpTools.TasteClient = previous;
        GC.SuppressFinalize(this);
    }

    private void Serve(string body) =>
        DrawingMcpTools.TasteClient = new QlooClient("test-key", new HttpClient(new StubHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), QlooClient.HackathonBaseUrl);
    #endregion

    #region Tests
    [Fact]
    public async Task WithoutAKey_SaysSoAndDoesNotInviteGuessing()
    {
        DrawingMcpTools.TasteClient = null;

        var reply = await new DrawingMcpTools().Taste("insights", type: "artist", audience: """{"location":"Port Sample"}""");

        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("No Qloo API key", reply["error"]!.GetValue<string>());
        Assert.Contains("Do not guess", reply["remedy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Status_IsFreeAndWorksWithoutAKey()
    {
        DrawingMcpTools.TasteClient = null;

        var reply = await new DrawingMcpTools().Taste("status");

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.False(reply["configured"]!.GetValue<bool>());
        Assert.NotNull(reply["budget"]);
    }

    [Theory]
    [InlineData("insights", null, null, null, "needs a type")]
    [InlineData("insights", "artist", """{"age":"55_and_older"}""", null, "needs a location")]
    [InlineData("insights", "artist", """{"location":"X","mood":"warm"}""", null, "audience has no 'mood'")]
    [InlineData("insights", "artist", "not json", null, "not valid JSON")]
    [InlineData("insights", "artist", """{"location":"X"}""", """{"signal.locaton.query":"Y"}""", "signal.location.query")]
    [InlineData("insights", "music_video", """{"location":"X"}""", null, "needs a type")]
    [InlineData("describe", null, null, null, "describe needs entities")]
    [InlineData("dance", null, null, null, "Unknown op")]
    public async Task Refusals_SendNothing(string op, string? type, string? audience, string? query, string expected)
    {
        Serve(Insights);

        var reply = await new DrawingMcpTools().Taste(op, type: type, audience: audience, query: query);

        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains(expected, reply["error"]!.GetValue<string>());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Insights_ReturnsCompactEntitiesWhereTheyWerePlacedAndTheSpend()
    {
        Serve(Insights);

        var reply = await new DrawingMcpTools().Taste("insights", type: "artist",
            audience: """{"location":"Port Sample","age":["45_to_54","55_and_older"]}""", label: "older listeners");

        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.StartsWith("t", reply["id"]!.GetValue<string>());
        Assert.Contains("signal.demographics.age=45_to_54%2C55_and_older", reply["request"]!.GetValue<string>());
        Assert.Equal("Sample Choir", reply["entities"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Gospel", reply["entities"]![0]!["tags"]![0]!.GetValue<string>());
        Assert.Equal("Port Sample, Examplia", reply["locality"]!["disambiguation"]!.GetValue<string>());
        Assert.True(reply["budget"]!["spent"]!.GetValue<int>() >= 1);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Resolve_FlagsTwoExactMatchesAsAmbiguous()
    {
        Serve(Search);

        var reply = await new DrawingMcpTools().Taste("resolve", name: "Twin Title", type: "movie");

        Assert.True(reply["ambiguous"]!.GetValue<bool>());
        Assert.Equal(2, reply["candidates"]!.AsArray().Count);
        Assert.Equal("1999", reply["candidates"]![0]!["disambiguation"]!.GetValue<string>());
    }

    /// <summary>The point of keeping answers on the session: a later script reads and cites them.</summary>
    [Fact]
    public async Task AScriptReadsTheAnswerBackThroughTheTasteGlobal()
    {
        Serve(Insights);
        var tools = new DrawingMcpTools();
        var reply = await tools.Taste("insights", type: "artist", audience: """{"location":"Port Sample"}""", label: "harbour taste");
        var id = reply["id"]!.GetValue<string>();

        var result = await tools.ExecuteScript($$"""
            const r = Taste.get('{{id}}');
            log(JSON.stringify({ name: r.entities[0].name, tag: r.entities[0].tags[0].name, place: r.locality.name,
                                 found: Taste.find('harbour').id, cite: r.cite() }));
            """, render: false);

        Assert.True(result.Success, result.Error);
        var line = result.Logs.Single(l => l.StartsWith("[LOG] {"))["[LOG] ".Length..];
        var read = JsonNode.Parse(line)!;
        Assert.Equal("Sample Choir", read["name"]!.GetValue<string>());
        Assert.Equal("Gospel", read["tag"]!.GetValue<string>());
        Assert.Equal("Port Sample", read["place"]!.GetValue<string>());
        Assert.Equal(id, read["found"]!.GetValue<string>());
        Assert.StartsWith("Qloo Insights API, /v2/insights?", read["cite"]!.GetValue<string>());
    }

    /// <summary>
    /// The chain the poster workflow will run, against the real service: a placed audience, then what its films
    /// share. Asserts on shape only — Qloo's data is never committed. Runs with <c>POLSON_LIVE_QLOO=1</c>; about
    /// four requests of the month.
    /// </summary>
    [Fact]
    public async Task Live_APlacedAudienceAndWhatItsFilmsShare()
    {
        if (Environment.GetEnvironmentVariable("POLSON_LIVE_QLOO") != "1") return;
        var key = config["ApiKeys:Qloo"];
        Assert.False(string.IsNullOrWhiteSpace(key), "ApiKeys:Qloo is not set in testappsettings.json");
        DrawingMcpTools.TasteClient = new QlooClient(key!, baseUrl: config["Qloo:BaseUrl"]);
        var tools = new DrawingMcpTools();

        var films = await tools.Taste("insights", type: "movie", audience: """{"location":"Mexico City","age":"55_and_older"}""",
            query: """{"filter.popularity.min":0.5}""", take: 6, label: "older Mexico City films");
        Assert.True(films["ok"]!.GetValue<bool>(), films.ToJsonString());
        Assert.Contains("Mexico", films["locality"]!["disambiguation"]!.GetValue<string>());
        var ids = string.Join(",", films["entities"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));

        // No type: these are films, so only the kinds that describe one, and nothing from places or brands.
        var shared = await tools.Taste("describe", entities: ids, take: 12, label: "what they share");
        Assert.True(shared["ok"]!.GetValue<bool>(), shared.ToJsonString());
        Assert.NotNull(shared["note"]);
        Assert.All(shared["tags"]!.AsArray(), t => Assert.Contains(t!["type"]!.GetValue<string>(), TasteDescribe.FilmKinds));

        var status = await tools.Taste("status");
        Assert.NotNull(status["budget"]!["monthlyRemaining"]);
    }

    /// <summary>A resolved locality id is sent as signal.location; a name is fuzzy-matched through signal.location.query.</summary>
    [Fact]
    public void Location_AnIdIsSentAsAnIdAndANameAsAQuery()
    {
        var byId = DrawingMcpTools.TasteQuery("urn:entity:movie", """{"location":"1b96428c-0000-4000-8000-000000000001"}""", null, 5).Query!;
        var byName = DrawingMcpTools.TasteQuery("urn:entity:movie", """{"location":"Port Sample","age":"55_and_older"}""", null, 5).Query!;

        Assert.Equal("1b96428c-0000-4000-8000-000000000001", byId["signal.location"]);
        Assert.False(byId.Has("signal.location.query"));
        Assert.Equal("Port Sample", byName["signal.location.query"]);
        Assert.Equal("55_and_older", byName["signal.demographics.age"]);
    }

    [Fact]
    public async Task Resolve_CanLookUpALocality()
    {
        string? sent = null;
        DrawingMcpTools.TasteClient = new QlooClient("test-key", new HttpClient(new StubHandler(r =>
        {
            sent = r.RequestUri!.Query;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Search, Encoding.UTF8, "application/json") };
        })), QlooClient.HackathonBaseUrl);

        await new DrawingMcpTools().Taste("resolve", name: "Port Sample", type: "locality");

        Assert.Contains("types=urn%3Aentity%3Alocality", sent);
    }

    [Fact]
    public void TagKinds_MapToQlooTagTypes() =>
        Assert.Equal(
            ["urn:tag:aesthetic_property:qloo", "urn:tag:motif:qloo", "urn:tag:genre:media"],
            DrawingMcpTools.TasteTagTypes("aesthetic, motif, urn:tag:genre:media, urn:entity:movie"));

    /// <summary>
    /// Unfiltered, Qloo's top tags over films were a credit card and a hotel rating (trailer5), so films this session
    /// resolved get the film kinds and a note; 'all' and ids never seen get the overview.
    /// </summary>
    [Fact]
    public async Task Describe_FilmsWithNoTypeAskForTheFilmKindsAndSaySo()
    {
        const string tags = """{ "results": { "tags": [ { "name": "Sample Gloom", "tag_id": "urn:tag:style:qloo:sample_gloom", "type": "urn:tag", "subtype": "urn:tag:style:qloo" } ] } }""";
        DrawingMcpTools.TasteClient = new QlooClient("test-key", new HttpClient(new StubHandler(r =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(r.RequestUri!.AbsolutePath.Contains("insights") ? tags : Search, Encoding.UTF8, "application/json"),
            })), QlooClient.HackathonBaseUrl);
        var tools = new DrawingMcpTools();
        await tools.Taste("resolve", name: "Twin Title", type: "movie");

        var films = await tools.Taste("describe", entities: "M-1,M-2");
        var all = await tools.Taste("describe", entities: "M-1,M-2", type: "all");
        var unseen = await tools.Taste("describe", entities: Guid.NewGuid().ToString());

        var sent = Uri.UnescapeDataString(films["request"]!.GetValue<string>());
        Assert.Contains("filter.tag.types=urn:tag:style:qloo,urn:tag:theme:qloo", sent);
        Assert.Contains("diversify.by=subtype", sent);
        Assert.Contains("only the kinds that describe", films["note"]!.GetValue<string>());
        Assert.Equal("Sample Gloom", films["tags"]![0]!["name"]!.GetValue<string>());

        Assert.DoesNotContain("filter.tag.types", all["request"]!.GetValue<string>());
        Assert.Null(all["note"]);
        Assert.DoesNotContain("filter.tag.types", unseen["request"]!.GetValue<string>());
        Assert.Contains("places and brands", unseen["note"]!.GetValue<string>());
    }

    /// <summary>
    /// A synthetic Qloo for a profile: the audience's list (age signal present) is E-1, E-2, E-3; the place's baseline
    /// is E-2, E-9; tag results are the place's aesthetics.
    /// </summary>
    private void ServeProfile() =>
        DrawingMcpTools.TasteClient = new QlooClient("test-key", new HttpClient(new StubHandler(r =>
        {
            Interlocked.Increment(ref calls);
            var q = Uri.UnescapeDataString(r.RequestUri!.Query);
            string Entity(string id, string name) => $$"""{ "name": "{{name}}", "entity_id": "{{id}}", "type": "urn:entity", "subtype": "urn:entity:movie", "query": { "affinity": 0.9 } }""";
            var body = q.Contains("filter.type=urn:tag")
                ? """{ "results": { "tags": [ { "id": "urn:tag:aesthetic_property:qloo:sample_bold", "name": "Sample Bold Prints", "type": "urn:tag", "subtype": "urn:tag:aesthetic_property:qloo" } ] } }"""
                : q.Contains("signal.demographics.age")
                    ? $$"""{ "results": { "entities": [ {{Entity("E-1", "Audience Only")}}, {{Entity("E-2", "Everyone")}}, {{Entity("E-3", "Also Theirs")}} ] }, "query": { "localities": { "signal": { "entity_id": "L-1", "name": "Port Sample", "disambiguation": "Port Sample, Examplia" } } } }"""
                    : $$"""{ "results": { "entities": [ {{Entity("E-2", "Everyone")}}, {{Entity("E-9", "Baseline Only")}} ] } }""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), QlooClient.HackathonBaseUrl);

    [Fact]
    public async Task Profile_KeepsWhatTheAudienceLovesThatItsPlaceDoesNot()
    {
        ServeProfile();
        var tools = new DrawingMcpTools();

        var reply = await tools.Taste("profile", audience: """{"location":"Port Sample","age":"55_and_older"}""", type: "movie", label: "older Port Sample");

        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal(3, reply["requests"]!.GetValue<int>());                     // audience, baseline, place tags
        var movies = reply["domains"]![0]!;
        Assert.Equal(["Audience Only", "Also Theirs"], movies["distinctive"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()));
        Assert.Equal([1, 3], movies["distinctive"]!.AsArray().Select(e => e!["rank"]!.GetValue<int>()));
        Assert.Equal(1, movies["sharedCount"]!.GetValue<int>());
        Assert.Equal("Sample Bold Prints", reply["placeAesthetics"]![0]!.GetValue<string>());
        Assert.Equal("Port Sample, Examplia", reply["locality"]!["disambiguation"]!.GetValue<string>());
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Profile_TheBaselineDropsTheAgeAndTheFloorIsApplied()
    {
        string? baselineQuery = null, audienceQuery = null;
        DrawingMcpTools.TasteClient = new QlooClient("test-key", new HttpClient(new StubHandler(r =>
        {
            var q = Uri.UnescapeDataString(r.RequestUri!.Query);
            if (!q.Contains("urn:tag")) { if (q.Contains("signal.demographics.age")) audienceQuery = q; else baselineQuery = q; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "results": { "entities": [], "tags": [] } }""", Encoding.UTF8, "application/json") };
        })), QlooClient.HackathonBaseUrl);

        var reply = await new DrawingMcpTools().Taste("profile", audience: """{"location":"Port Sample","age":"55_and_older"}""", type: "artist");

        Assert.Contains("filter.popularity.min=0.5", audienceQuery);
        Assert.Contains("signal.location.query=Port Sample", baselineQuery);
        Assert.DoesNotContain("signal.demographics", baselineQuery);
        Assert.Contains(reply["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("nothing distinctive"));
    }

    [Theory]
    [InlineData("""{"age":"55_and_older"}""", null, "needs a location")]
    [InlineData("""{"location":"Port Sample"}""", "movie,tag", "not an entity type")]
    [InlineData(null, null, "needs the audience's place")]
    public async Task Profile_RefusalsSendNothing(string? audience, string? type, string expected)
    {
        ServeProfile();

        var reply = await new DrawingMcpTools().Taste("profile", audience: audience, type: type);

        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains(expected, reply["error"]!.GetValue<string>());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Profile_AScriptReadsTheWholeProfile()
    {
        ServeProfile();
        var tools = new DrawingMcpTools();
        var id = (await tools.Taste("profile", audience: """{"location":"Port Sample","age":"55_and_older"}""", type: "movie"))["id"]!.GetValue<string>();

        var result = await tools.ExecuteScript($$"""
            const p = Taste.get('{{id}}');
            log(JSON.stringify({ first: p.profile.domains[0].distinctive[0].name, flat: p.entities.length,
                                 place: p.profile.placeAesthetics.tags[0].name, age: p.profile.audience.age }));
            """, render: false);

        Assert.True(result.Success, result.Error);
        var read = JsonNode.Parse(result.Logs.Single(l => l.StartsWith("[LOG] {"))["[LOG] ".Length..])!;
        Assert.Equal("Audience Only", read["first"]!.GetValue<string>());
        Assert.Equal(2, read["flat"]!.GetValue<int>());
        Assert.Equal("Sample Bold Prints", read["place"]!.GetValue<string>());
        Assert.Equal("55_and_older", read["age"]!.GetValue<string>());
    }

    private const string PlaceId = "1b96428c-0000-4000-8000-000000000001";

    /// <summary>
    /// Qloo as it answers a location given by id: a search names the place, and insights carry no locality echo.
    /// </summary>
    private void ServeProfileById() =>
        DrawingMcpTools.TasteClient = new QlooClient("test-key", new HttpClient(new StubHandler(r =>
        {
            Interlocked.Increment(ref calls);
            var q = Uri.UnescapeDataString(r.RequestUri!.Query);
            var body = r.RequestUri!.AbsolutePath.Contains("/search")
                ? $$"""{ "results": [ { "name": "Port Sample", "entity_id": "{{PlaceId}}", "types": ["urn:entity:locality"], "disambiguation": "Port Sample, Examplia" } ] }"""
                : q.Contains("filter.type=urn:tag")
                    ? """{ "results": { "tags": [] } }"""
                    : """{ "results": { "entities": [ { "name": "Theirs", "entity_id": "E-1", "type": "urn:entity", "subtype": "urn:entity:movie", "query": { "affinity": 0.9 } } ] } }""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), QlooClient.HackathonBaseUrl);

    /// <summary>
    /// A place given by id is named in the reply from the session's own resolve, since Qloo echoes only names.
    /// </summary>
    /// <remarks>Both poster runs passed Lagos by id, as the tool tells them to, and had nothing in the reply to confirm it by.</remarks>
    [Fact]
    public async Task Profile_APlaceGivenByIdIsNamedFromTheSessionsResolve()
    {
        ServeProfileById();
        var tools = new DrawingMcpTools();

        await tools.Taste("resolve", name: "Port Sample", type: "locality");
        var reply = await tools.Taste("profile", audience: $$"""{"location":"{{PlaceId}}","age":"24_and_younger"}""", type: "movie");

        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        Assert.Equal("Port Sample", reply["locality"]!["name"]!.GetValue<string>());
        Assert.Equal("Port Sample, Examplia", reply["locality"]!["disambiguation"]!.GetValue<string>());
        Assert.Contains("Qloo does not echo", reply["locality"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task Profile_AnUnresolvedIdSaysHowToFindOutWhereItIs()
    {
        ServeProfileById();

        var reply = await new DrawingMcpTools().Taste("profile", audience: $$"""{"location":"{{PlaceId}}","age":"24_and_younger"}""", type: "movie");

        Assert.Equal(PlaceId, reply["locality"]!["id"]!.GetValue<string>());
        Assert.Contains("never resolved", reply["locality"]!["note"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("film", "urn:entity:movie")]
    [InlineData("tv", "urn:entity:tv_show")]
    [InlineData("music", "urn:entity:artist")]
    [InlineData("brand", "urn:entity:brand")]
    [InlineData("urn:entity:podcast", "urn:entity:podcast")]
    [InlineData("music video", null)]
    public void TypeNames_MapToQlooUrns(string given, string? urn) =>
        Assert.Equal(urn, DrawingMcpTools.TasteType(given));
    #endregion

    #region Child Types
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
    #endregion
}

[CollectionDefinition(Name)]
public sealed class TasteCollection
{
    public const string Name = "Taste client statics";
}
