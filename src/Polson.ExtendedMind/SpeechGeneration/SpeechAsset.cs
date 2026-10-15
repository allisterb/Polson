namespace Polson.ExtendedMind.SpeechGeneration;

using System.Collections.Generic;
using Polson.ExtendedMind.ImageGeneration;

/// <summary>
/// Options for <c>Assets.speak(text, options)</c>: <c>{ voice?, style?, path?, model? }</c>.
/// </summary>
/// <remarks>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto
/// them, so a script passing <c>{ voice: 'Puck' }</c> reaches <see cref="Voice"/>. The camelCase form is the one
/// documented in <c>docs/Polson.core.md</c> and the studio manuals.
/// </remarks>
public sealed record SpeechOptions
{
    /// <summary>One of the prebuilt voices (<see cref="SpeechVoices.Names"/>). Default <c>Kore</c>.</summary>
    public string Voice { get; init; } = "Kore";

    /// <summary>
    /// How to deliver the whole line, in a few words: <c>shouting over the wind</c>, <c>out of breath</c>, <c>whispered
    /// urgently</c>. Sent as <c>speechMetadata.style</c>, not spoken. Never a person to sound like, and not age, gender or
    /// accent, which belong to the voice; a moment within the line goes in the text as a tag, <c>&lt;short pause&gt;</c>.
    /// </summary>
    public string? Style { get; init; }

    /// <summary>Where in the project to write the WAV. <c>audio/&lt;id&gt;.wav</c> when unset.</summary>
    public string? Path { get; init; }

    /// <summary>The model, when not the default <c>gemini-3.8-flash-lite-tts</c>.</summary>
    public string? Model { get; init; }
}

/// <summary>The prebuilt voices the Gemini speech models offer, by name, each with what it sounds like.</summary>
public static class SpeechVoices
{
    /// <summary>
    /// Every voice and Google's one-word description of it, as its speech-generation page gives them (read
    /// 2026-10-14, ai.google.dev/gemini-api/docs/speech-generation). The descriptions are how a voice is chosen for a
    /// character; the names alone say nothing. <c>Kore</c> and <c>Puck</c> were heard on this key, 2026-10-14.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["Achernar"] = "Soft", ["Achird"] = "Friendly", ["Algenib"] = "Gravelly", ["Algieba"] = "Smooth",
        ["Alnilam"] = "Firm", ["Aoede"] = "Breezy", ["Autonoe"] = "Bright", ["Callirrhoe"] = "Easy-going",
        ["Charon"] = "Informative", ["Despina"] = "Smooth", ["Enceladus"] = "Breathy", ["Erinome"] = "Clear",
        ["Fenrir"] = "Excitable", ["Gacrux"] = "Mature", ["Iapetus"] = "Clear", ["Kore"] = "Firm",
        ["Laomedeia"] = "Upbeat", ["Leda"] = "Youthful", ["Orus"] = "Firm", ["Pulcherrima"] = "Forward",
        ["Puck"] = "Upbeat", ["Rasalgethi"] = "Informative", ["Sadachbia"] = "Lively", ["Sadaltager"] = "Knowledgeable",
        ["Schedar"] = "Even", ["Sulafat"] = "Warm", ["Umbriel"] = "Easy-going", ["Vindemiatrix"] = "Gentle",
        ["Zephyr"] = "Bright", ["Zubenelgenubi"] = "Casual"
    };

    /// <summary>Every name, in order, so a misspelling is refused with the nearest rather than sent to fail.</summary>
    public static readonly string[] Names = [.. Descriptions.Keys.Order(StringComparer.Ordinal)];

    /// <summary><c>Algenib (gravelly)</c>: a voice as a reader picks it.</summary>
    public static string Describe(string name) =>
        Descriptions.TryGetValue(name, out var d) ? $"{name} ({d.ToLowerInvariant()})" : name;
}

/// <summary>
/// A spoken line, written into the project as a WAV: what <c>Assets.speak</c> returns. Its <c>path</c> is what
/// <c>Motion.speech(text, { audio })</c> times the mouths against and what a video plays.
/// </summary>
/// <remarks>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto
/// them, so a script reading <c>line.duration</c> reaches <see cref="Duration"/>. The camelCase form is the one
/// documented in <c>docs/Polson.core.md</c> and the studio manuals.
/// </remarks>
public sealed record SpeechAsset : RequisitionResult
{
    /// <summary>The WAV's path in the project, as given or defaulted. Empty when the line was not spoken.</summary>
    public string Path { get; init; } = "";

    /// <summary>The line as asked for.</summary>
    public string Text { get; init; } = "";

    public string Voice { get; init; } = "";

    public string? Style { get; init; }

    /// <summary>Length in seconds.</summary>
    public double Duration { get; init; }

    public int SampleRate { get; init; }

    public int ByteLength { get; init; }

    /// <summary>What looked wrong with a line that was spoken: a length the words do not account for.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Remedies phrased for a line rather than a picture, where the two differ.</summary>
    public override string Remedy => Failure switch
    {
        ImageGenerationFailure.SafetyBlocked => "The line or its direction was blocked. Reword it; a direction describes the delivery ('out of breath'), never a person.",
        ImageGenerationFailure.NotConfigured => "This studio has speech disabled: no credentials are configured. Go without a voice; Motion.speech(text) times a line by the studio's rules.",
        ImageGenerationFailure.BudgetExhausted => "No generation budget remains this session. Time the line without a voice: Motion.speech(text) needs none. Check Assets.budget.remaining first.",
        ImageGenerationFailure.ModelNotFound => "That model is not available on this key. Leave model unset to use the default.",
        ImageGenerationFailure.InvalidRequest => "The request was refused. Read error: check the voice, the path and the line's length, then try once more.",
        ImageGenerationFailure.RefusedLikeness => "A voice cannot be made to sound like a real person. Pick the voice by its description (Assets.voices) and describe only the delivery in style ('shouting over the wind').",
        _ => base.Remedy
    };

    /// <inheritdoc/>
    public override Dictionary<string, object?> ToJSON(string? key = null)
    {
        var json = base.ToJSON(key);
        json["path"] = Path;
        json["text"] = Text;
        json["voice"] = Voice;
        json["style"] = Style;
        json["duration"] = Duration;
        json["sampleRate"] = SampleRate;
        json["byteLength"] = ByteLength;
        json["warnings"] = Warnings;
        return json;
    }
}
