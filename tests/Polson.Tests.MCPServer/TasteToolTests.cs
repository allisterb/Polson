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

        var shared = await tools.Taste("describe", entities: ids, type: "aesthetic,motif", take: 12, label: "what they share");
        Assert.True(shared["ok"]!.GetValue<bool>(), shared.ToJsonString());
        Assert.All(shared["tags"]!.AsArray(), t =>
            Assert.Contains(t!["type"]!.GetValue<string>(), new[] { "urn:tag:aesthetic_property:qloo", "urn:tag:motif:qloo" }));

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
