namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

/// <summary>
/// The optional speech backend Motion.speech falls back on: g2p-en in <c>python-speech/</c>, run through
/// <c>src/speech/g2p.py</c> with NLTK's pinned data in <c>models/nltk_data/</c>. It guesses words no dictionary has and
/// spells numbers out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked only for the gaps.</b> Every word the CMU Pronouncing Dictionary has stays an in-process lookup; only the
/// words it lacks and the numbers in a line cross the boundary, all in one call, and the answers are remembered for the
/// life of the process, because starting the interpreter costs about three seconds.
/// </para>
/// <para>
/// <b>Nothing crosses but JSON.</b> The request goes in over stdin as UTF-8 bytes and the answer comes back over stdout;
/// the script's arguments are the data folder, which comes from the host, not from a script. It runs offline: the
/// script replaces <c>nltk.download</c> before anything can call it (scanned and run under an audit hook 2026-10-11; see
/// reference/README.md).
/// </para>
/// <para>
/// Found by walking up from the assembly, as the vision detectors are, or set by the host from <c>Tools:SpeechPython</c>,
/// <c>Tools:SpeechScript</c> and <c>Tools:SpeechData</c>. The engine reads no environment variables.
/// </para>
/// </remarks>
public static class SpeechBackend
{
    #region Fields
    static string? _python, _script, _data, _alignScript, _models;
    static string? _rPython, _rScript, _rData, _rAlignScript, _rModels;
    static bool _looked;
    static readonly Dictionary<string, (string[] Phonemes, string Source)> Guessed = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string> Spelled = new(StringComparer.Ordinal);
    static readonly Dictionary<string, Alignment> Aligned = new(StringComparer.Ordinal);
    static readonly object Gate = new();

    /// <summary>The aligner's weights, which <c>tools/bootstrap.py</c> puts in <c>models/</c>.</summary>
    const string Weights = "wav2vec2_fairseq_base_ls960_asr_ls960.pth";
    #endregion

    #region Properties
    /// <summary>An explicit path to the interpreter, or null to discover one.</summary>
    public static string? PythonOverride
    {
        get => _python;
        set { _python = value; _looked = false; }
    }

    /// <summary>An explicit path to <c>g2p.py</c>, or null to discover one.</summary>
    public static string? ScriptOverride
    {
        get => _script;
        set { _script = value; _looked = false; }
    }

    /// <summary>An explicit path to NLTK's data folder, or null to discover one.</summary>
    public static string? DataOverride
    {
        get => _data;
        set { _data = value; _looked = false; }
    }

    /// <summary>Whether the backend can be used here.</summary>
    public static bool Available { get { Look(); return _rPython is not null && _rScript is not null && _rData is not null; } }

    /// <summary>What is missing, for saying so rather than only failing.</summary>
    public static string? Missing
    {
        get
        {
            Look();
            List<string> gaps = [];
            if (_rPython is null) gaps.Add("the python-speech interpreter");
            if (_rScript is null) gaps.Add("src/speech/g2p.py");
            if (_rData is null) gaps.Add("models/nltk_data");
            return gaps.Count == 0 ? null : string.Join(", ", gaps);
        }
    }

    /// <summary>An explicit path to <c>align.py</c>, or null to discover one.</summary>
    public static string? AlignScriptOverride
    {
        get => _alignScript;
        set { _alignScript = value; _looked = false; }
    }

    /// <summary>An explicit path to the folder holding the aligner's weights, or null to discover one.</summary>
    public static string? ModelsOverride
    {
        get => _models;
        set { _models = value; _looked = false; }
    }

    /// <summary>Whether a recording can be aligned here: the interpreter, <c>align.py</c> and the weights.</summary>
    public static bool AlignAvailable { get { Look(); return _rPython is not null && _rAlignScript is not null && _rModels is not null; } }

    /// <summary>What alignment is missing, for saying so rather than only failing.</summary>
    public static string? AlignMissing
    {
        get
        {
            Look();
            List<string> gaps = [];
            if (_rPython is null) gaps.Add("the python-speech interpreter");
            if (_rAlignScript is null) gaps.Add("src/speech/align.py");
            if (_rModels is null) gaps.Add($"models/{Weights}");
            return gaps.Count == 0 ? null : string.Join(", ", gaps);
        }
    }

    /// <summary>How to get it, for a refusal to end with.</summary>
    internal const string Install = "install the speech backend (py -3.13 tools/bootstrap.py --only speech-venv)";
    #endregion

    #region Methods
    /// <summary>
    /// Guesses each word's sounds and spells each number out, asking only for what has not been asked before. Words come
    /// back as CMU phonemes with stress, and the source the script gave (<c>model</c>, or <c>dictionary</c> when NLTK's
    /// own copy happened to know it).
    /// </summary>
    internal static (IReadOnlyDictionary<string, (string[] Phonemes, string Source)> Words, IReadOnlyDictionary<string, string> Numbers)
        Fill(IEnumerable<string> words, IEnumerable<string> numbers, int timeoutMs = 60000)
    {
        lock (Gate)
        {
            var askWords = words.Distinct().Where(w => !Guessed.ContainsKey(w)).ToList();
            var askNumbers = numbers.Distinct().Where(n => !Spelled.ContainsKey(n)).ToList();
            if (askWords.Count + askNumbers.Count > 0) Ask(askWords, askNumbers, timeoutMs);
            return (Guessed, Spelled);
        }
    }

    /// <summary>
    /// When each word of a known line is said in a recording: one span per word, in order, null where the aligner could
    /// not place it, and the recording's length. Remembered per file and line, keyed on the file's size and time.
    /// </summary>
    /// <remarks>
    /// The spans are the aligner's, pulled <see cref="AlignerLag"/> earlier: against the moment sound starts, it was
    /// measured a little late.
    /// </remarks>
    internal static Alignment Align(string wav, IReadOnlyList<string> words, int timeoutMs = 180000)
    {
        lock (Gate)
        {
            var file = new FileInfo(wav);
            var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{string.Join(' ', words)}";
            if (Aligned.TryGetValue(key, out var known)) return known;
            if (!AlignAvailable)
                throw new InvalidOperationException($"Aligning a recording needs {AlignMissing}: {Install}.");

            using var doc = Run(_rAlignScript!, _rModels!, new { audio = file.FullName, words }, timeoutMs, "The aligner");
            double? Seconds(JsonElement w, string name) =>
                w.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            var spans = doc.RootElement.GetProperty("words").EnumerateArray().Select(w =>
                (Start: Seconds(w, "start") - AlignerLag, End: Seconds(w, "end") - AlignerLag, Score: Seconds(w, "score"))).ToList();
            if (spans.Count != words.Count)
                throw new InvalidOperationException($"The aligner returned {spans.Count} words for {words.Count}.");
            return Aligned[key] = new Alignment(doc.RootElement.GetProperty("duration").GetDouble(), spans);
        }
    }

    /// <summary>
    /// How late the aligner places a word's start against the moment its sound begins, in seconds: about one 20 ms model
    /// frame, measured 2026-10-13 on ten first words of Windows SAPI recordings (−30 to +71 ms, mean +22 ms).
    /// </summary>
    internal const double AlignerLag = 0.02;

    static void Ask(List<string> words, List<string> numbers, int timeoutMs)
    {
        if (!Available)
            throw new InvalidOperationException($"The speech backend needs {Missing}: {Install}.");

        using var doc = Run(_rScript!, _rData!, new { words, numbers }, timeoutMs, "The speech backend");
        foreach (var w in doc.RootElement.GetProperty("words").EnumerateObject())
            Guessed[w.Name] = ([.. w.Value.GetProperty("phonemes").EnumerateArray().Select(p => p.GetString() ?? "")],
                w.Value.GetProperty("source").GetString() ?? "model");
        foreach (var n in doc.RootElement.GetProperty("numbers").EnumerateObject())
            Spelled[n.Name] = n.Value.GetString() ?? "";
    }

    /// <summary>Runs one of the backend's scripts: the request as UTF-8 JSON on stdin, the answer parsed from stdout.</summary>
    static JsonDocument Run(string script, string argument, object request, int timeoutMs, string who)
    {
        var info = new ProcessStartInfo(_rPython!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{who} would not start.");
        using (var stdin = process.StandardInput.BaseStream) stdin.Write(JsonSerializer.SerializeToUtf8Bytes(request));

        // Read both streams at once, so a chatty stderr cannot fill its pipe and stall the process.
        var stderr = process.StandardError.ReadToEndAsync();
        var json = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{who} did not finish within {timeoutMs} ms.");
        }
        if (process.ExitCode != 0 || json.Length == 0)
            throw new InvalidOperationException($"{who} failed (exit {process.ExitCode}). {Trim(stderr.Result)}");
        return JsonDocument.Parse(json);
    }

    static string Trim(string s) => s.Length <= 400 ? s.Trim() : string.Concat(s.AsSpan(0, 400).Trim(), "…");

    static void Look()
    {
        if (_looked) return;
        _looked = true;
        _rPython = Pick(_python, Candidates("python-speech", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"), File.Exists);
        _rScript = Pick(_script, Candidates("src", "speech/g2p.py"), File.Exists);
        // The folder counts only with both of its packages unpacked in it.
        _rData = Pick(_data, Candidates("models", "nltk_data"), d =>
            Directory.Exists(Path.Combine(d, "taggers", "averaged_perceptron_tagger_eng")) && Directory.Exists(Path.Combine(d, "corpora", "cmudict")));
        _rAlignScript = Pick(_alignScript, Candidates("src", "speech/align.py"), File.Exists);
        _rModels = Pick(_models, Candidates("models", ""), d => File.Exists(Path.Combine(d, Weights)));
    }

    static string? Pick(string? overridden, IEnumerable<string> candidates, Func<string, bool> exists) =>
        !string.IsNullOrWhiteSpace(overridden) ? (exists(overridden) ? overridden : null) : candidates.FirstOrDefault(exists);

    // Walks up from the assembly, as the vision detectors do: a test runs from its own output folder and the CLI from
    // bin/cli, so neither can assume the repository root is the working directory.
    static IEnumerable<string> Candidates(string folder, string tail)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, folder, tail.Replace('/', Path.DirectorySeparatorChar));
    }
    #endregion

    #region Types
    /// <summary>A recording's length and, per word in order, where it was said (null where it could not be placed).</summary>
    internal sealed record Alignment(double Duration, IReadOnlyList<(double? Start, double? End, double? Score)> Words);
    #endregion
}
