namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using global::Polson.Tests;
using Polson.ExtendedMind.DocumentProcessing;
using Xunit;

/// <summary>
/// Which Nemotron endpoint a request goes to, in what order, and what is reported when none answers.
/// </summary>
public class NemotronChatTests : TestsRuntime
{
    #region Fields
    private static readonly NemotronEndpoint Nim = new(NemotronEndpoint.Nim, "https://nim.example/v1", "nim-key", NemotronEndpoint.NimModel);
    private static readonly NemotronEndpoint Nebius = new(NemotronEndpoint.Nebius, "https://tokenfactory.example/v1/", "nebius-key", NemotronEndpoint.NebiusModel);
    #endregion

    #region Methods
    private static JsonArray Content() => new(new JsonObject { ["type"] = "text", ["text"] = "q" });

    private static string Reply(string content) =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = "stop" }),
            ["usage"] = new JsonObject { ["total_tokens"] = 7 },
        }.ToJsonString();

    [Fact]
    public void TestNimIsPreferredByDefaultAndEitherCanBeFirst()
    {
        Assert.Equal([Nim, Nebius], NemotronEndpoint.InOrder(null, Nim, Nebius));
        Assert.Equal([Nim, Nebius], NemotronEndpoint.InOrder(" NIM ", Nebius, Nim));
        Assert.Equal([Nebius, Nim], NemotronEndpoint.InOrder("nebius", Nim, Nebius));
        Assert.Null(NemotronEndpoint.InOrder("token-factory", Nim, Nebius));
    }

    [Fact]
    public async Task TestThePreferredEndpointAnswersAloneWithItsOwnModelIdAndKey()
    {
        var handler = new Script(_ => (HttpStatusCode.OK, Reply("391")));
        using var chat = new NemotronChat([Nim, Nebius], new HttpClient(handler));

        var (text, tokens, _, endpoint) = await chat.Ask(NemotronChat.DefaultModel, Content(), null, CancellationToken.None);

        Assert.Equal(("391", 7L, NemotronEndpoint.Nim), (text, tokens, endpoint));
        var sent = Assert.Single(handler.Sent);
        Assert.Equal("https://nim.example/v1/chat/completions", sent.Uri);
        Assert.Equal("Bearer nim-key", sent.Authorization);
        Assert.Equal(NemotronEndpoint.NimModel, sent.Model);
    }

    [Fact]
    public async Task TestAnEndpointWithoutAKeyIsLeftOut()
    {
        var handler = new Script(_ => (HttpStatusCode.OK, Reply("x")));
        using var chat = new NemotronChat([Nim with { ApiKey = null }, Nebius], new HttpClient(handler));

        Assert.Equal([Nebius], chat.Endpoints);
        var (_, _, _, endpoint) = await chat.Ask(NemotronChat.DefaultModel, Content(), null, CancellationToken.None);
        Assert.Equal(NemotronEndpoint.Nebius, endpoint);
        Assert.Equal("https://tokenfactory.example/v1/chat/completions", Assert.Single(handler.Sent).Uri);
    }

    [Fact]
    public async Task TestAModelNamedOtherwiseIsSentAsItIsToEachEndpoint()
    {
        var handler = new Script(uri => uri.Contains("nim") ? (HttpStatusCode.NotFound, "{}") : (HttpStatusCode.OK, Reply("x")));
        using var chat = new NemotronChat([Nim, Nebius], new HttpClient(handler));

        await chat.Ask("nvidia/other-model", Content(), null, CancellationToken.None);

        Assert.Equal(["nvidia/other-model", "nvidia/other-model"], handler.Sent.ConvertAll(s => s.Model));
    }

    [Fact]
    public async Task TestWhenEveryEndpointFailsTheLastFailuresKindIsThrownNamingEach()
    {
        // nim answers 500; Token Factory times out, which HttpClient reports as a cancellation nobody asked for.
        var handler = new Script(uri => uri.Contains("nim") ? (HttpStatusCode.InternalServerError, "{\"detail\":\"down\"}") : throw new TaskCanceledException());
        using var chat = new NemotronChat([Nim, Nebius], new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => chat.Ask(NemotronChat.DefaultModel, Content(), null, CancellationToken.None));

        Assert.Contains("nim answered 500", ex.Message);
        Assert.Contains("nebius did not answer", ex.Message);
        Assert.Equal(2, handler.Sent.Count);
    }

    [Fact]
    public async Task TestOneEndpointFailingAloneIsThrownAsItself()
    {
        var handler = new Script(_ => (HttpStatusCode.TooManyRequests, "{}"));
        using var chat = new NemotronChat([Nebius], new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Ask(NemotronChat.DefaultModel, Content(), null, CancellationToken.None));

        Assert.StartsWith("Nemotron on nebius answered 429", ex.Message);
    }

    [Fact]
    public async Task TestTheCallerCancellingStopsAtTheFirstEndpoint()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Script(_ => { cts.Cancel(); throw new TaskCanceledException(); });
        using var chat = new NemotronChat([Nim, Nebius], new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.Ask(NemotronChat.DefaultModel, Content(), null, cts.Token));

        Assert.Single(handler.Sent);
    }
    #endregion

    #region Types
    /// <summary>Answers each request from its URI, and keeps what it was sent.</summary>
    private sealed class Script(Func<string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Model)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(CancellationToken.None))!;
            Sent.Add((uri, request.Headers.Authorization?.ToString(), body["model"]!.GetValue<string>()));
            var (status, text) = answer(uri);
            return new HttpResponseMessage(status) { Content = new StringContent(text) };
        }
    }
    #endregion
}
