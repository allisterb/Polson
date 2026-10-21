namespace Polson.ExtendedMind.DocumentProcessing;

using System;
using System.Collections.Generic;
using System.Net.Http;
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
/// service differs: Nemotron 3.5 Super VL on Nebius Token Factory (<see cref="TokenFactoryChat"/>), which takes the
/// clip as a <c>video_url</c> data URI. It is chosen whatever model the agent runs on.
/// </para>
/// <para>
/// <b>What it is good for, measured 2026-10-20 on our own renders.</b> It describes what happens and in
/// what order, reads large on-screen text exactly, and fills a JSON Schema properly. It samples about two
/// frames a second, so times are good to about a second and a glitch lasting a frame or two is missed;
/// it now and then reports a shot or a title card that is not there; and it judged a clip with empty
/// frames clean. A breakdown, then, not a verdict.
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
        chat = new TokenFactoryChat(apiKey, baseUrl, http);
    }
    #endregion

    #region Properties
    /// <summary>Default model: Nemotron 3.5 Super VL, Token Factory's vision model.</summary>
    public const string DefaultVideoModel = "nvidia/Nemotron-3.5-Super-VL";

    /// <summary>What a clip may be. MP4 is the one verified against the service; it is what <c>Motion</c> saves.</summary>
    public static readonly IReadOnlyDictionary<string, string> VideoTypes = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
    };

    /// <summary>Largest clip sent, in bytes. A data URI grows by a third on the way, and minutes of video are not the use.</summary>
    public const int MaxVideoBytes = 16 * 1024 * 1024;

    public override bool IsAvailable => chat.IsAvailable;

    protected override string Kind => "video";

    protected override IReadOnlyDictionary<string, string> Types => VideoTypes;

    protected override int MaxBytes => MaxVideoBytes;
    #endregion

    #region Methods
    /// <summary>One chat completion with the clip attached as a <c>video_url</c> data URI.</summary>
    protected override async Task<Generation> Generate(
        byte[] bytes, string mime, string query, string model, string? schema, bool images, CancellationToken ct)
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = query },
            new JsonObject
            {
                ["type"] = "video_url",
                ["video_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" },
            });
        var (text, tokens, finish) = await chat.Ask(model, content, schema, ct);
        return new Generation(text, tokens, finish) { Model = model };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) chat.Dispose();
        base.Dispose(disposing);
    }
    #endregion

    #region Fields
    readonly TokenFactoryChat chat;
    #endregion
}
