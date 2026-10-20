namespace Polson.ExtendedMind.DocumentProcessing;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Watches a video in the project and answers a question about it. <c>Video.ask(...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A document read whose document moves.</b> Everything around the call — containment, the type
/// allowlist, the size check, the budget, the content-addressed cache, the scan for text addressed to
/// the reader, provenance and the run record — is <see cref="DocumentProcessor"/>'s, unchanged. Only the
/// service differs: Nemotron 3.5 Super VL on Nebius Token Factory, an OpenAI-compatible chat endpoint
/// that takes the clip as a <c>video_url</c> data URI. It is chosen whatever model the agent runs on.
/// </para>
/// <para>
/// <b>What it is good for, measured 2026-10-20 on our own renders.</b> It describes what happens and in
/// what order, reads large on-screen text exactly, and fills a JSON Schema properly. It samples about two
/// frames a second, so times are good to about a second and a glitch lasting a frame or two is missed;
/// it now and then reports a shot or a title card that is not there; and it judged a clip with empty
/// frames clean. A breakdown, then, not a verdict.
/// </para>
/// <para>
/// <b>Token Factory takes no PDF</b> (rejected in every request shape) and <b>does not discount cached
/// input</b>, which is why this is one-shot reading rather than the model an agent runs on.
/// </para>
/// </remarks>
public class VideoReader : DocumentProcessor
{
    #region Constructors
    /// <param name="apiKey">Read from <c>ApiKeys:NebiusTokenFactory</c>. Sent as a bearer token, never logged.</param>
    /// <param name="baseUrl">Read from <c>NebiusTokenFactory:BaseUrl</c>, e.g. <c>https://api.tokenfactory.us-north1.nebius.com/v1</c>.</param>
    /// <param name="budget">Reads allowed this session.</param>
    /// <param name="projectRoot">Clips are read from inside this directory and nowhere else.</param>
    /// <param name="model">Default model. Overridable per call.</param>
    /// <param name="cache">Where repeated answers are held. Null disables caching.</param>
    /// <param name="http">For tests. The reader owns one otherwise.</param>
    public VideoReader(string? apiKey, string? baseUrl, DocumentBudget budget, string? projectRoot = null,
        string model = DefaultVideoModel, DocumentCache? cache = null, HttpClient? http = null)
        : base(null, budget, projectRoot, model, cache)
    {
        this.apiKey = apiKey;
        endpoint = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/') + "/chat/completions";
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
        ownsHttp = http is null;
    }
    #endregion

    #region Properties
    /// <summary>Default model: Nemotron 3.5 Super VL, Token Factory's video-understanding model.</summary>
    public const string DefaultVideoModel = "nvidia/Nemotron-3.5-Super-VL";

    /// <summary>What a clip may be. MP4 is the one verified against the service; it is what <c>Motion</c> saves.</summary>
    public static readonly IReadOnlyDictionary<string, string> VideoTypes = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
    };

    /// <summary>Largest clip sent, in bytes. A data URI grows by a third on the way, and minutes of video are not the use.</summary>
    public const int MaxVideoBytes = 16 * 1024 * 1024;

    /// <summary>Room for the reasoning the model does before it answers; a 15 s clip used about 1–2k.</summary>
    public const int MaxOutputTokens = 16000;

    /// <summary>Below the 30 s script limit would be better, and a 15 s clip answers in 4–7 s; this only stops a hang.</summary>
    public const int RequestTimeoutSeconds = 120;

    public override bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey) && endpoint is not null;

    protected override string Kind => "video";

    protected override IReadOnlyDictionary<string, string> Types => VideoTypes;

    protected override int MaxBytes => MaxVideoBytes;
    #endregion

    #region Methods
    /// <summary>One chat completion with the clip attached as a <c>video_url</c> data URI.</summary>
    /// <remarks>
    /// Reasoning stays on. Switching it off cut output to a quarter and halved the time, but the director
    /// judged the reading would suffer, and output is the cheap part of a call that is mostly video.
    /// </remarks>
    protected override async Task<(string? Text, long Tokens, string? Finish)> Generate(
        byte[] bytes, string mime, string query, string model, string? schema, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxOutputTokens,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = query },
                    new JsonObject
                    {
                        ["type"] = "video_url",
                        ["video_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" },
                    }),
            }),
        };
        if (schema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "answer", ["schema"] = JsonNode.Parse(schema) },
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation, which would read as the caller giving up.
            throw new TimeoutException($"Token Factory did not answer within {http.Timeout.TotalSeconds:0} s.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Not an HttpRequestException, which Classify reads as "unreachable": the status code is
                // in the message so 429, 401 and 400 are told apart. The body is the service's, not ours,
                // and carries no key.
                throw new InvalidOperationException(
                    $"Token Factory answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
            }

            var reply = JsonNode.Parse(text);
            var choice = reply?["choices"]?[0];
            return (choice?["message"]?["content"]?.GetValue<string>(),
                reply?["usage"]?["total_tokens"]?.GetValue<long>() ?? 0,
                choice?["finish_reason"]?.GetValue<string>());
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsHttp) http.Dispose();
        base.Dispose(disposing);
    }
    #endregion

    #region Fields
    readonly string? apiKey;
    readonly string? endpoint;
    readonly HttpClient http;
    readonly bool ownsHttp;
    #endregion
}
