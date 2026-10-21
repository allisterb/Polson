namespace Polson.ExtendedMind.DocumentProcessing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One chat completion with Nemotron 3.5 Super VL, from the first of its <see cref="NemotronEndpoint"/>s that answers.
/// Shared by <see cref="VideoReader"/> and <see cref="NemotronDocumentProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An endpoint that fails hands the request to the next</b> — any status, a timeout, a network error — and the
/// last failure is the answer when none succeeds, its message naming every endpoint's. A request never goes out
/// twice to the same endpoint.
/// </para>
/// <para>
/// Measured limits (Token Factory 2026-10-20, NIM 2026-10-21): Token Factory takes <b>at most 8 images</b>, NIM at
/// least 64; neither takes a PDF part; video goes as a <c>video_url</c> data URI; JSON Schema through
/// <c>response_format</c>. Token Factory reports cached input but bills it at the full rate. Reasoning stays on:
/// switching it off quartered the output, but the director judged the reading would suffer.
/// </para>
/// </remarks>
public sealed class NemotronChat : IDisposable
{
    #region Constructors
    /// <param name="endpoints">In order of preference; those without a key or a base URL are left out.</param>
    /// <param name="http">For tests; one is owned otherwise.</param>
    public NemotronChat(IEnumerable<NemotronEndpoint> endpoints, HttpClient? http = null)
    {
        Endpoints = [.. endpoints.Where(e => e.IsConfigured)];
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
        ownsHttp = http is null;
    }
    #endregion

    #region Properties
    /// <summary>
    /// The name Polson gives the model, whichever endpoint serves it: what <c>provenance.model</c> reports and the
    /// cache keys on. Asked for by this name, each endpoint is sent its own id for it.
    /// </summary>
    public const string DefaultModel = "nvidia/Nemotron-3.5-Super-VL";

    /// <summary>Room for the reasoning the model does before it answers; a 93-page 10-K's ten questions used about 6k.</summary>
    public const int MaxOutputTokens = 32000;

    /// <summary>Only stops a hang: ADK gives a whole script 60 s, and the 10-K answered in 20.</summary>
    public const int RequestTimeoutSeconds = 120;

    /// <summary>The fewest images any endpoint takes (Token Factory's), so a request fits whichever serves it.</summary>
    public const int MaxImagesPerRequest = 8;

    /// <summary>The configured endpoints, in the order they are tried.</summary>
    public IReadOnlyList<NemotronEndpoint> Endpoints { get; }

    public bool IsAvailable => Endpoints.Count > 0;
    #endregion

    #region Methods
    /// <summary>Sends one user message and returns the answer, the tokens billed, the finish reason and the endpoint that answered.</summary>
    /// <param name="model"><see cref="DefaultModel"/> for each endpoint's own id; any other name is sent as it is.</param>
    /// <param name="content">The message's parts: <c>text</c>, <c>image_url</c>, <c>video_url</c>.</param>
    /// <param name="schema">JSON Schema text for the answer, or null for prose.</param>
    /// <exception cref="InvalidOperationException">A status other than success; the code is in the message.</exception>
    /// <exception cref="TimeoutException">No answer within <see cref="RequestTimeoutSeconds"/>.</exception>
    public async Task<(string? Content, long Tokens, string? Finish, string Endpoint)> Ask(
        string model, JsonArray content, string? schema, CancellationToken ct)
    {
        if (!IsAvailable) throw new InvalidOperationException("No Nemotron endpoint is configured.");

        var failures = new List<(NemotronEndpoint Endpoint, Exception Error)>();
        foreach (var endpoint in Endpoints)
        {
            try
            {
                var (text, tokens, finish) = await AskOne(endpoint, model, content, schema, ct);
                return (text, tokens, finish, endpoint.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
            {
                failures.Add((endpoint, ex));
                if (endpoint != Endpoints[^1])
                    Runtime.Warn("Nemotron on {0} failed, trying {1}: {2}", endpoint.Name, Endpoints[failures.Count].Name, ex.Message);
            }
        }

        if (failures.Count == 1) throw failures[0].Error;

        // Every endpoint failed: the last failure's kind, so it is classified as itself, with each one's reason.
        var message = string.Join("; ", failures.Select(f => f.Error.Message));
        throw failures[^1].Error switch
        {
            TimeoutException => new TimeoutException(message),
            HttpRequestException => new HttpRequestException(message),
            _ => new InvalidOperationException(message),
        };
    }

    private async Task<(string? Content, long Tokens, string? Finish)> AskOne(
        NemotronEndpoint endpoint, string model, JsonArray content, string? schema, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model == DefaultModel ? endpoint.Model : model,
            ["max_tokens"] = MaxOutputTokens,
            // Deep-cloned: a node belongs to one parent, and the same parts go to the next endpoint if this one fails.
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content.DeepClone() }),
        };
        if (schema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "answer", ["schema"] = JsonNode.Parse(schema) },
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.BaseUrl!.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation, which would read as the caller giving up.
            throw new TimeoutException($"Nemotron on {endpoint.Name} did not answer within {http.Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"Nemotron on {endpoint.Name} could not be reached: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Not an HttpRequestException, which reads as "unreachable": the status code is in the message so
                // 429, 401, 404 and 400 are told apart. The body is the service's and carries no key.
                throw new InvalidOperationException(
                    $"Nemotron on {endpoint.Name} answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 200)]}");
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
    readonly HttpClient http;
    readonly bool ownsHttp;
    #endregion
}
