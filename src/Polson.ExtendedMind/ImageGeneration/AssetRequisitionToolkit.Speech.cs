namespace Polson.ExtendedMind.ImageGeneration;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Polson.ExtendedMind.SpeechGeneration;

public partial class AssetRequisitionToolkit
{
    #region Fields
    /// <summary>The longest line one call speaks. A scene's dialogue is several lines, each placed with its own start.</summary>
    public const int MaxSpeechCharacters = 600;
    #endregion

    #region Properties
    /// <summary>
    /// Every voice <see cref="Speak"/> takes, with Google's word for what it sounds like: <c>[{ name, description }]</c>,
    /// by name. Free and offline: choosing a voice for a character should not cost a generation per guess.
    /// </summary>
    public Dictionary<string, object?>[] Voices =>
        [.. SpeechVoices.Names.Select(n => new Dictionary<string, object?> { ["name"] = n, ["description"] = SpeechVoices.Descriptions[n] })];
    #endregion

    #region Methods
    /// <summary>
    /// Speaks a line in a prebuilt voice and writes it into the project as a WAV. <c>await Assets.speak('Who knew?', { voice: 'Puck' })</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A voice is material, as a swatch is.</b> It is not the animation: the mouths are still drawn, and timed to it by
    /// <c>Motion.speech(text, { audio: line.path })</c>, so what the model made is something code then has to use.
    /// </para>
    /// <para>
    /// Refused before the network, spending nothing: an empty or over-long line, a voice that is not one of the prebuilt
    /// ones (with the nearest named), a direction that names a person to sound like, and a path outside the project or
    /// not ending <c>.wav</c>. Then the cache, by model, voice and line, so a re-run speaks for nothing; then the budget,
    /// one generation per line, shared with the images.
    /// </para>
    /// <para>
    /// The WAV is written as the model sent it, so a provenance chunk the service attached stays in the file.
    /// </para>
    /// </remarks>
    public async Task<SpeechAsset> Speak(string text, SpeechOptions? options = null)
    {
        var opts = options ?? new SpeechOptions();
        var model = opts.Model ?? speech?.Model ?? SpeechGenerator.DefaultModel;
        text = text?.Trim() ?? "";

        SpeechAsset Refuse(ImageGenerationFailure failure, string reason)
        {
            RequisitionScope.Record(new RequisitionRecord("speech", text, Success: false, Failure: failure.ToString(),
                Reason: reason, Model: null, FromCache: false, Refused: true));
            RecordBudgetState();
            return new SpeechAsset { Success = false, Failure = failure, Error = reason, Refusal = failure == ImageGenerationFailure.InvalidRequest ? reason : null, Text = text, Voice = opts.Voice ?? "", Style = opts.Style };
        }

        if (text.Length == 0)
            return Refuse(ImageGenerationFailure.InvalidRequest, "Assets.speak needs the line to say.");
        if (text.Length > MaxSpeechCharacters)
            return Refuse(ImageGenerationFailure.InvalidRequest,
                $"The line is {text.Length} characters; one call speaks at most {MaxSpeechCharacters}. Split it into lines, "
                + "speak each, and place them with Motion.speech's start.");

        if (SpeechVoices.Names.FirstOrDefault(v => string.Equals(v, opts.Voice?.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } voice)
            return Refuse(ImageGenerationFailure.InvalidRequest,
                $"'{opts.Voice}' is not a voice. Did you mean {string.Join(", ", NearestVoices(opts.Voice ?? ""))}? "
                + $"The voices are: {string.Join(", ", SpeechVoices.Names.Select(SpeechVoices.Describe))}. Assets.voices lists them.");

        var style = opts.Style?.Trim().TrimEnd(':').Trim();
        // A name alone is enough, as for a redraw's description: a direction has no other reason to hold one.
        if (style is { Length: > 0 } && PersonalName().Match(style) is { Success: true } who)
            return Refuse(ImageGenerationFailure.RefusedLikeness,
                $"'{who.Value}' reads as a real person, and a voice cannot be made to sound like one: a generated likeness "
                + "of a voice carries none of the consent a real one would need. Choose the voice by its description (Assets.voices) and "
                + "describe only the delivery: 'shouting over the wind', 'out of breath'. If it is not a name, write it in lower case.");

        // The text is spoken verbatim; the direction travels beside it as speechMetadata.style, which is not spoken.
        style = style is { Length: > 0 } ? style : null;
        var prompt = SpeechGenerator.Describe(text, style);
        var hash = SpeechGenerator.HashOf(model, voice, text, style);

        var path = string.IsNullOrWhiteSpace(opts.Path) ? $"audio/{hash[..12].ToLowerInvariant()}.wav" : opts.Path.Trim();
        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            return Refuse(ImageGenerationFailure.InvalidRequest, $"Assets.speak writes a WAV, so its path ends .wav, not '{path}'.");
        string full;
        try { full = ProjectPath.Resolve(RequisitionScope.Current?.ProjectRoot, path, "path", "Write to"); }
        catch (ArgumentException ex) { return Refuse(ImageGenerationFailure.InvalidRequest, ex.Message); }

        var hitsBefore = Budget.CacheHits;
        var result = await AcquireSpeech(text, style, prompt, model, voice, hash);
        var fromCache = Budget.CacheHits > hitsBefore;

        if (result.Success)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, result.Wav);
        }

        RequisitionScope.Record(new RequisitionRecord("speech", text, result.Success,
            Failure: result.Success ? null : result.Failure.ToString(), Reason: result.Success ? null : result.Error,
            Model: model, FromCache: fromCache, Refused: false, Artifact: result.Success ? full : null));
        RecordBudgetState();

        if (!result.Success)
            return new SpeechAsset { Success = false, Failure = result.Failure, Error = result.Error, Text = text, Voice = voice, Style = style };

        var seconds = WavFile.Seconds(result.Wav);
        return new SpeechAsset
        {
            Success = true,
            Id = hash,
            Path = path,
            Text = text,
            Voice = voice,
            Style = style,
            Duration = seconds,
            SampleRate = WavFile.SampleRate(result.Wav),
            ByteLength = result.Wav.Length,
            Warnings = SpeechWarnings(text, seconds, style),
            Provenance = new Provenance
            {
                Model = result.Model, Hash = hash, Requester = requester, GeneratedUtc = result.GeneratedUtc,
                FromCache = fromCache, Prompt = prompt
            }
        };
    }

    /// <summary>The cache, then the budget, then the service: the only path in <see cref="Speak"/> that can spend.</summary>
    async Task<SpeechGenerationResult> AcquireSpeech(string text, string? style, string prompt, string model, string voice, string hash)
    {
        if (speech is null)
            return SpeechGenerationResult.Failed(ImageGenerationFailure.NotConfigured,
                "No speech credentials are configured for this studio.", prompt, model);

        speechCache ??= new SpeechCache();
        if (await speechCache.Get(hash) is { } cached)
        {
            Budget.CacheHits++;
            return cached;
        }

        if (!Budget.CanAfford())
            return SpeechGenerationResult.Failed(ImageGenerationFailure.BudgetExhausted,
                $"Budget exhausted ({Budget.Spent}/{Budget.Total} spent).", prompt, model);

        var result = await speech.GenerateSpeech(text, style, voice, model);
        if (result.Charged)
        {
            Budget.Spent++;
            Budget.TokensSpent += result.TotalTokens ?? 0;
            await speechCache.Put(hash, result);
        }
        return result;
    }

    /// <summary>
    /// What the length says the model may have done besides the line. Speech runs about two to four words a second, so a
    /// line far longer than its words allow may carry the direction read aloud, and one far shorter may be cut off.
    /// </summary>
    static List<string> SpeechWarnings(string text, double seconds, string? style)
    {
        var words = WordPattern().Matches(text).Count;
        var warnings = new List<string>();
        if (words == 0) return warnings;
        if (seconds > words / 1.2 + 1.5)
            warnings.Add($"{seconds:0.0} s is long for {words} words"
                + (style is { Length: > 0 } ? ", as if the direction were read aloud" : "")
                + ". Listen, or time it with Motion.speech(text, { audio }) and read each word's confidence.");
        else if (seconds < words / 6.0)
            warnings.Add($"{seconds:0.0} s is short for {words} words, as if the line were cut off. Listen before using it.");
        return warnings;
    }

    static IEnumerable<string> NearestVoices(string wanted) =>
        SpeechVoices.Names.OrderBy(n => EditDistance(wanted.ToLowerInvariant(), n.ToLowerInvariant())).Take(3);

    static int EditDistance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var keep = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = keep;
            }
        }
        return d[b.Length];
    }

    [GeneratedRegex(@"\p{L}[\p{L}'’-]*")]
    private static partial Regex WordPattern();
    #endregion
}
