namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using global::Polson.ExtendedMind.ParallelSearch;
using global::Polson.Tests;
using Xunit;

/// <summary>
/// <see cref="ParallelClient.Decide"/> and the <see cref="FactChecker"/> over it: a stated fact checked on the web, one
/// claim per call.
/// </summary>
public class FactCheckerTests : TestsRuntime
{
    #region Fields
    /// <summary>A decision as the service returned one (probe, 2026-10-28), excerpts shortened: <c>usage</c> is an object here.</summary>
    private const string Supported = """
        {
          "mode": "fast",
          "answers": { "claim": { "type": "noul", "noul": 0.9989735410505979 } },
          "usage": { "input_tokens": 3847, "output_tokens": 0 },
          "citations": [
            { "url": "https://www.imdb.com/title/tt0377471", "title": "Be Cool (2005) - IMDb", "excerpts": ["Directed by F. Gary Gray."] },
            { "url": "https://en.wikipedia.org/wiki/Be_Cool", "excerpts": ["Directed by | F. Gary Gray"] },
            { "url": "https://s3.us-west-1.wasabisys.com/luminist/EB/L/Leonard%20-%20Be%20Cool.pdf", "excerpts": ["NOW A MAJOR MOTION PICTURE"] }
          ],
          "decision_id": "dec_1"
        }
        """;
    #endregion

    #region Methods
    [Fact]
    public async Task TestADecisionAsksOneTrueOrFalseQuestionAboutItsSubject()
    {
        var handler = new Stub(_ => Supported);
        using var client = new ParallelClient("test-key", new HttpClient(handler));

        var response = await client.Decide("Be Cool (2005 film)", "Be Cool (2005) was directed by F. Gary Gray.");

        Assert.True(response.Success, response.Error);
        Assert.Equal("https://api.parallel.ai/v1/decisions", handler.Uris.Single());
        var body = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Assert.Equal("Be Cool (2005 film)", body.GetProperty("state").GetProperty("subject").GetString());
        var question = body.GetProperty("questions").GetProperty(ParallelClient.DecisionQuestion);
        Assert.Equal("noul", question.GetProperty("type").GetString());
        Assert.Contains("Be Cool (2005) was directed by F. Gary Gray.", question.GetProperty("instructions").GetString());
        Assert.Equal(0.9989735410505979, response.Answers[ParallelClient.DecisionQuestion].Noul);
        Assert.Equal(3847, response.Usage!.InputTokens);
        Assert.Equal(3, response.Citations.Count);
    }

    [Fact]
    public async Task TestADecisionWithoutASubjectIsRefusedBeforeAnyCall()
    {
        var handler = new Stub(_ => Supported);
        using var client = new ParallelClient("test-key", new HttpClient(handler));

        var response = await client.Decide(" ", "Be Cool (2005) was directed by F. Gary Gray.");

        Assert.False(response.Success);
        Assert.Equal(ParallelFailure.InvalidRequest, response.Failure);
        Assert.Empty(handler.Uris);
    }

    [Theory]
    [InlineData(0.999, FactCheck.Supported)]
    [InlineData(0.9, FactCheck.Supported)]
    [InlineData(0.456, FactCheck.Unsettled)]
    [InlineData(0.1, FactCheck.Unsettled)]
    [InlineData(0.031, FactCheck.NotSupported)]
    public void TestTheProbabilityIsReadAsAVerdict(double probability, string verdict) =>
        Assert.Equal(verdict, FactChecker.VerdictOf(probability));

    /// <summary>The bucket holding a whole novel is kept in the record but is not a source to show.</summary>
    [Fact]
    public async Task TestACheckKeepsEverySourceAndMarksTheRecognisedOnes()
    {
        using var client = new ParallelClient("test-key", new HttpClient(new Stub(_ => Supported)));
        var check = await new FactChecker(client).Check("Be Cool (2005 film)", "Be Cool (2005) was directed by F. Gary Gray.");

        Assert.Equal(FactCheck.Supported, check.Verdict);
        Assert.Equal([true, true, false], check.Sources.Select(s => s.Recognised));
        Assert.Equal("supported (0.999): imdb.com, en.wikipedia.org", check.Detail());
    }

    [Fact]
    public async Task TestAClaimAskedAgainIsAnsweredFromTheCache()
    {
        var handler = new Stub(_ => Supported);
        using var client = new ParallelClient("test-key", new HttpClient(handler));
        var checker = new FactChecker(client);

        await checker.Check("Be Cool (2005 film)", "Be Cool (2005) was directed by F. Gary Gray.");
        await checker.Check(" Be Cool (2005 film)", "Be Cool (2005) was directed by F. Gary Gray. ");

        Assert.Single(handler.Uris);
        Assert.Equal(1, checker.Spent);
    }

    [Fact]
    public async Task TestChecksPastTheBudgetAreRecordedAsUnchecked()
    {
        var handler = new Stub(_ => Supported);
        using var client = new ParallelClient("test-key", new HttpClient(handler));
        var checker = new FactChecker(client, budget: 1);

        await checker.Check("a", "first claim.");
        var second = await checker.Check("a", "second claim.");

        Assert.Equal(FactCheck.NotChecked, second.Verdict);
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task TestARefusalIsNotAVerdict()
    {
        const string refusal = """{ "answers": { "claim": { "type": "refusal" } }, "citations": [], "decision_id": "d" }""";
        using var client = new ParallelClient("test-key", new HttpClient(new Stub(_ => refusal)));

        var check = await new FactChecker(client).Check("a", "a claim.");

        Assert.Equal(FactCheck.Refused, check.Verdict);
        Assert.Null(check.Probability);
    }

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Be_Cool", true)]
    [InlineData("https://www.imdb.com/title/tt0377471/fullcredits", true)]
    [InlineData("https://catalog.afi.com/Catalog/moviedetails/1", true)]
    [InlineData("https://www.loc.gov/item/1", true)]
    [InlineData("https://cs.stanford.edu/page", true)]
    [InlineData("https://s3.us-west-1.wasabisys.com/x.pdf", false)]
    [InlineData("https://notwikipedia.org/x", false)]
    [InlineData("http://84.74.53.98/film", false)]
    public void TestRecognisedSourcesAreMatchedOnTheHostAndItsParents(string url, bool recognised) =>
        Assert.Equal(recognised, FactChecker.IsRecognised(url));
    #endregion

    #region Child types
    private sealed class Stub(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "application/json") };
        }
    }
    #endregion
}
