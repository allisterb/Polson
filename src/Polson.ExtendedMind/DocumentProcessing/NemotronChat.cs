namespace Polson.ExtendedMind.DocumentProcessing;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One chat completion on Nebius Token Factory, an OpenAI-compatible endpoint. Shared by <see cref="VideoReader"/>
/// and <see cref="NemotronDocumentProcessor"/>.
/// </summary>
/// <remarks>
/// Measured limits (2026-10-20): <b>at most 8 images per request</b>; no PDF part in any request shape; video as a
/// <c>video_url</c> data URI; JSON Schema through <c>response_format</c>; and cached input is reported but billed at
/// the full rate. Reasoning stays on: switching it off quartered the output, but the director judged the reading
/// would suffer.
/// </remarks>
public sealed class TokenFactoryChat : IDisposable
{
    #region Constructors
    /// <param name="apiKey">From <c>ApiKeys:NebiusTokenFactory</c>. Sent as a bearer token, never logged.</param>
    /// <param name="baseUrl">From <c>NebiusTokenFactory:BaseUrl</c>.</param>
    /// <param name="http">For tests; one is owned otherwise.</param>
    public TokenFactoryChat(string? apiKey, string? baseUrl, HttpClient? http = null)
    {
        this.apiKey = apiKey;
        endpoint = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/') + "/chat/completions";
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
        ownsHttp = http is null;
    }
    #endregion

    #region Properties
    /// <summary>Room for the reasoning the model does before it answers; a 93-page 10-K's ten questions used about 6k.</summary>
    public const int MaxOutputTokens = 32000;

    /// <summary>Only stops a hang: ADK gives a whole script 60 s, and the 10-K answered in 20.</summary>
    public const int RequestTimeoutSeconds = 120;

    /// <summary>The service's own limit, found by asking with 93.</summary>
    public const int MaxImagesPerRequest = 8;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey) && endpoint is not null;
    #endregion

    #region Methods
    /// <summary>Sends one user message and returns the answer, the tokens billed and the finish reason.</summary>
    /// <param name="content">The message's parts: <c>text</c>, <c>image_url</c>, <c>video_url</c>.</param>
    /// <param name="schema">JSON Schema text for the answer, or null for prose.</param>
    /// <exception cref="InvalidOperationException">A status other than success; the code is in the message.</exception>
    /// <exception cref="TimeoutException">No answer within <see cref="RequestTimeoutSeconds"/>.</exception>
    public async Task<(string? Content, long Tokens, string? Finish)> Ask(
        string model, JsonArray content, string? schema, CancellationToken ct)
    {
        if (!IsAvailable) throw new InvalidOperationException("Token Factory is not configured.");

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxOutputTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
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
                // Not an HttpRequestException, which reads as "unreachable": the status code is in the message so
                // 429, 401, 404 and 400 are told apart. The body is the service's and carries no key.
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

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }
    #endregion

    #region Fields
    readonly string? apiKey;
    readonly string? endpoint;
    readonly HttpClient http;
    readonly bool ownsHttp;
    #endregion
}
