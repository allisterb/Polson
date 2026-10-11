namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// A line of dialogue broken into phonemes and mouth shapes over time, from its text alone: what
/// <c>Motion.speech(text, options)</c> returns. Its <c>mouths</c> key a switch of drawn mouths; its <c>weights()</c> drive a
/// face's pose channels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which sounds is the dictionary's; when is ours.</b> Words are looked up in the CMU Pronouncing Dictionary (Carnegie Mellon, as CMU Sphinx
/// maintains it), keeping its stress marks. Phonemes become mouths by Papagayo-NG's tables (<c>phonemes/*.json</c>; Papagayo-NG, GPL-2-or-later,
/// after Mike Clifton's Papagayo). The durations are studio rules, not measurements: a stressed vowel 0.15 s, an
/// unstressed one 0.065 s, a stop 0.07 s and so on, vowels at the end of a phrase longer by 1.3, pauses at punctuation,
/// all divided by <c>rate</c>. With no recording there is nothing to be out of sync with, so plausible timing is the
/// whole job; with one, time the line by ear in Papagayo instead.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase form is the one documented in
/// <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public sealed class MotionSpeech
{
    #region Constructors
    internal MotionSpeech(string text, object? options)
    {
        const string who = "Motion.speech";
        var o = options is null ? new Hashtable()
            : JsInterop.AsDict(options) ?? throw new ArgumentException($"{who}(text, options) takes an object of settings.");
        foreach (var key in o.Keys.Cast<object>().Select(k => k.ToString()!))
            if (!Keys.Contains(key)) throw new ArgumentException($"{who} has no '{key}'. It takes: {string.Join(", ", Keys)}.");
        double Get(string key, double fallback, double min)
        {
            if (o[key] is not { } v) return fallback;
            var x = MotionTypes.Read(v, MotionType.Real, $"{who}'s {key}")[0];
            return x >= min ? x : throw new ArgumentException($"{who}'s {key} is {MotionTypes.Format(x)}; it cannot be below {MotionTypes.Format(min)}.");
        }

        Text = text;
        Start = Get("start", 0, double.MinValue);
        var rate = Get("rate", 1, 0.1);
        lead = Get("lead", 0.04, 0);
        var minimum = Get("minimum", 0.05, 0);
        (Set, table) = MouthSets.Read(o["set"], who);
        var pronounce = Pronunciations(o["pronounce"], who);

        var guess = o["guess"] switch
        {
            null => true,
            bool b => b,
            var v => throw new ArgumentException($"{who}'s guess is true or false, not {v}.")
        };

        // Words, numbers and pauses; then what each says; then when, in order.
        var tokens = Tokenize(text, who);
        var said = Resolve(tokens, pronounce, guess, who, out guessed);

        var t = Start;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Pause > 0)
            {
                t += tokens[i].Pause / rate;
                continue;
            }

            var spoken = said[i]!;
            var final = i + 1 == tokens.Count || tokens[i + 1].Pause > 0;
            var wordStart = t;
            for (var k = 0; k < spoken.Count; k++)
            {
                var sounds = spoken[k].Phonemes;
                // "The" before a vowel is "thee", the one weak form a dictionary's first choice gets wrong in ordinary speech.
                var next = k + 1 < spoken.Count ? spoken[k + 1] : i + 1 < tokens.Count && said[i + 1] is [var after, ..] ? after : null;
                if (spoken[k].Word == "THE" && next is { Phonemes: [var first, ..] } && IsVowel(Base(first)))
                    sounds = ["DH", "IY0"];
                // The end of a phrase draws out its last word's vowels: a number's last spoken word, not all of it.
                var drawn = final && k == spoken.Count - 1;
                foreach (var s in sounds)
                {
                    var d = Length(s) * (drawn && IsVowel(Base(s)) ? 1.3 : 1) / rate;
                    phonemes.Add(new Sound(s, tokens[i].Raw, t, t + d, table.TryGetValue(Base(s), out var m) ? m
                        : throw new ArgumentException($"{who}: the mouth set has no mouth for '{Base(s)}'."), spoken[k].Source));
                    t += d;
                }
            }
            var source = spoken.Select(s => s.Source).Distinct().ToList() is [var one] ? one : "model";
            words.Add((tokens[i].Raw, wordStart, t, string.Join(" ", spoken.Select(s => s.Word.ToLowerInvariant())), source));
        }

        End = phonemes.Count > 0 ? phonemes[^1].End : Start;
        segments = Segments(minimum);
    }
    #endregion

    #region Fields
    static readonly string[] Keys = ["start", "rate", "set", "pronounce", "guess", "lead", "minimum"];

    private readonly double lead;
    private readonly IReadOnlyDictionary<string, string> table;
    private readonly List<Sound> phonemes = [];
    private readonly List<(string Word, double Start, double End, string Said, string Source)> words = [];
    private readonly List<Spoken> guessed;
    /// <summary>The mouths, led and merged: each from its start to the next one's, rest before the first and after the last.</summary>
    private readonly List<(double Start, double End, string Mouth)> segments;
    #endregion

    #region Properties
    /// <summary>The line as given.</summary>
    public string Text { get; }

    /// <summary>The mouth set: <c>blair</c>, <c>flemingDobbs</c>, <c>rhubarb</c>, <c>cmu</c> or <c>custom</c>.</summary>
    public string Set { get; }

    /// <summary>When the first sound starts, in seconds.</summary>
    public double Start { get; }

    /// <summary>When the last sound ends, in seconds.</summary>
    public double End { get; }

    /// <summary>How long the line takes, in seconds.</summary>
    public double Duration => End - Start;

    /// <summary>
    /// Every sound: <c>{ phoneme, base, stress, word, start, end, mouth, source }</c>, stress −1 for a consonant; source
    /// <c>dictionary</c>, <c>pronounce</c>, <c>model</c> (guessed by the speech backend) or <c>number</c> (a number spelled out).
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Phonemes => [.. phonemes.Select(p => new Dictionary<string, object?>
    {
        ["phoneme"] = p.Phoneme, ["base"] = Base(p.Phoneme), ["stress"] = Stress(p.Phoneme),
        ["word"] = p.Word, ["start"] = p.Start, ["end"] = p.End, ["mouth"] = p.Mouth, ["source"] = p.Source
    })];

    /// <summary>
    /// Every word as written: <c>{ word, start, end, said, source }</c>, <c>said</c> what was pronounced (a number's
    /// words, <c>'four point five'</c>) and <c>source</c> where its sounds came from, as for a phoneme.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Words => [.. words.Select(w => new Dictionary<string, object?>
    {
        ["word"] = w.Word, ["start"] = w.Start, ["end"] = w.End, ["said"] = w.Said, ["source"] = w.Source
    })];

    /// <summary>
    /// The words the speech backend guessed, <c>[{ word, phonemes }]</c>, lower case: worth a look, since its guesses are
    /// plausible for invented words and weaker for names. Override any with <c>pronounce</c>.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Guessed => [.. guessed.Select(g => new Dictionary<string, object?>
    {
        ["word"] = g.Word.ToLowerInvariant(), ["phonemes"] = string.Join(" ", g.Phonemes)
    })];

    /// <summary>Keys for <c>comp.switch</c>: <c>[[time, mouth], ...]</c>, each shape a little before its sound (<c>lead</c>).</summary>
    // The first is rest from the beginning of time; it is keyed a tenth of a second before the line, and holds before that.
    public IReadOnlyList<object?[]> Mouths => segments.Select((s, i) => new object?[] { i == 0 ? s.End - 0.1 : s.Start, s.Mouth }).ToList();

    /// <summary>The mouths this line uses, <c>rest</c> among them, in the order they first appear.</summary>
    public IReadOnlyList<string> Names => segments.Select(s => s.Mouth).Distinct().ToList();
    #endregion

    #region Methods
    /// <summary>The mouth showing at a time.</summary>
    public string MouthAt(double time) => segments.Last(s => s.Start <= time).Mouth;

    /// <summary>The word being spoken at a time, as written, or <c>''</c> between words.</summary>
    public string WordAt(double time) => words.FirstOrDefault(w => time >= w.Start && time < w.End).Word ?? "";

    /// <summary>
    /// What each token says. A word is what <c>pronounce</c> gives, else the dictionary's, else its hyphenated parts in
    /// turn; a number is its words spelled out. Everything the dictionary lacks, and every number, goes to the speech
    /// backend in one call: refused, naming them, when it is not installed or <c>guess</c> is off.
    /// </summary>
    static List<List<Spoken>?> Resolve(List<Token> tokens, Dictionary<string, string[]> pronounce, bool guess, string who, out List<Spoken> guesses)
    {
        bool Known(string piece) => pronounce.ContainsKey(piece) || CmuDictionary.Has(piece);
        // A word the dictionary lacks, hyphenated, is its parts, so only a part no one knows is guessed ("x-ray", sent
        // whole, comes back K R EY1 S).
        IEnumerable<string> Pieces(string word) =>
            Known(word) || !word.Contains('-') ? [word] : word.Split('-', StringSplitOptions.RemoveEmptyEntries);

        var unknown = tokens.Where(t => t.Word is not null && !t.Number)
            .SelectMany(t => Pieces(t.Word!).Where(p => !Known(p)).Select(p => (Piece: p, Shown: p == t.Word ? t.Raw : p.ToLowerInvariant())))
            .DistinctBy(u => u.Piece).ToList();
        var numbers = tokens.Where(t => t.Number).Select(t => t.Raw).Distinct().ToList();

        IReadOnlyDictionary<string, (string[] Phonemes, string Source)> guessed = new Dictionary<string, (string[], string)>();
        IReadOnlyDictionary<string, string> spelled = new Dictionary<string, string>();
        if (unknown.Count + numbers.Count > 0)
        {
            if (!guess || !SpeechBackend.Available)
            {
                List<string> gaps = [];
                if (unknown.Count > 0) gaps.Add($"the CMU Pronouncing Dictionary does not know {string.Join(", ", unknown.Select(u => $"'{u.Shown}'"))}");
                if (numbers.Count > 0) gaps.Add($"{string.Join(", ", numbers.Select(n => $"'{n}'"))} must be spelled out as said");
                var example = unknown.Count > 0 ? unknown[0].Shown.ToLowerInvariant() : "zorblax";
                throw new ArgumentException($"{who}: {string.Join(", and ", gaps)}. Give a word as phonemes, pronounce: {{ {example}: 'K AE1 T' }} "
                    + "(CMU's 39, a vowel's stress 0, 1 or 2), and write a number in words"
                    + (guess ? $"; or {SpeechBackend.Install}, which guesses words and spells numbers out ({SpeechBackend.Missing} is missing)." : "; guess is off."));
            }

            (guessed, spelled) = SpeechBackend.Fill(unknown.Select(u => u.Piece), numbers);
            // A spelled number's words are almost always the dictionary's; any that are not are guessed in a second call.
            var more = numbers.SelectMany(n => Words(spelled[n])).SelectMany(Pieces).Where(p => !Known(p) && !guessed.ContainsKey(p)).Distinct().ToList();
            if (more.Count > 0) (guessed, _) = SpeechBackend.Fill(more, []);
        }

        string[] Sounds(string piece)
        {
            if (pronounce.TryGetValue(piece, out var given)) return given;
            if (CmuDictionary.Has(piece)) return CmuDictionary.Lookup(piece);
            var g = guessed[piece].Phonemes;
            if (g.Length == 0 || g.Any(s => !MouthSets.Cmu.Contains(Base(s)) || IsVowel(Base(s)) != (Stress(s) >= 0)))
                throw new InvalidOperationException($"{who}: the speech backend's guess for '{piece.ToLowerInvariant()}' was not CMU phonemes "
                    + $"({string.Join(" ", g)}). Give it with pronounce.");
            return g;
        }

        Spoken Say(string word)
        {
            var parts = Pieces(word).ToList();
            var source = pronounce.ContainsKey(word) ? "pronounce" : parts.Any(p => !Known(p)) ? "model" : "dictionary";
            return new Spoken(word, [.. parts.SelectMany(Sounds)], source);
        }

        // What was guessed is the pieces, not the words they sit in: "zorblax", not "zorblax-cat".
        var asked = unknown.Select(u => u.Piece).ToList();
        if (numbers.Count > 0) asked.AddRange(numbers.SelectMany(n => Words(spelled[n])).SelectMany(Pieces).Where(p => !Known(p)));
        guesses = [.. asked.Distinct().Select(p => new Spoken(p, Sounds(p), "model"))];

        return [.. tokens.Select(t => t.Pause > 0 ? null
            : t.Number ? Spell(t.Raw).Select(Say).Select(s => s.Source == "dictionary" ? s with { Source = "number" } : s).ToList()
            : new List<Spoken> { Say(t.Word!) })];

        List<string> Spell(string number) => Words(spelled[number]).ToList() is { Count: > 0 } said ? said
            : throw new InvalidOperationException($"{who}: the speech backend spelled '{number}' as '{spelled[number]}', which has no words. Write it in words.");

        // A spelled-out number's words, as tokens are: letters, apostrophes and hyphens, upper case.
        static IEnumerable<string> Words(string spelledOut) =>
            Regex.Matches(spelledOut, @"[A-Za-z]+(?:['-][A-Za-z]+)*").Select(m => m.Value.ToUpperInvariant());
    }

    /// <summary>
    /// One node per mouth, 0 to 1, crossfading over <c>blend</c> seconds (0.08) at every change, the weights summing to 1 at
    /// every time: <c>{ AI: node, E: node, ... }</c>, for a face's pose channels. <c>names</c> limits it to those mouths.
    /// </summary>
    /// <remarks>Each is the share of a <c>blend</c>-wide window its mouth fills, so it is exactly linear between changes.</remarks>
    public Dictionary<string, object?> Weights(object? options = null)
    {
        const string who = "speech.weights";
        var o = options is null ? new Hashtable()
            : JsInterop.AsDict(options) ?? throw new ArgumentException($"{who}(options) takes {{ blend?, names? }}.");
        foreach (var key in o.Keys.Cast<object>().Select(k => k.ToString()!))
            if (key is not ("blend" or "names")) throw new ArgumentException($"{who} has no '{key}'. It takes: blend, names.");
        var blend = o["blend"] is { } b ? MotionTypes.Read(b, MotionType.Real, $"{who}'s blend")[0] : 0.08;
        if (blend <= 0) throw new ArgumentException($"{who}'s blend is seconds, more than 0.");
        var names = o["names"] is IList list ? list.Cast<object?>().Select(n => n?.ToString() ?? "").ToList() : Names.ToList();
        foreach (var n in names.Where(n => !Names.Contains(n)))
            throw new ArgumentException($"{who}: this line never shows '{n}'. Its mouths are {string.Join(", ", Names)}.");
        return names.ToDictionary(n => n, n => (object?)Weight(n, blend / 2));
    }

    /// <summary>A mouth's share of the window <c>[t − h, t + h]</c>, keyed linearly where that changes slope.</summary>
    private MotionAnimated Weight(string mouth, double h)
    {
        double Share(double t)
        {
            var covered = 0d;
            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].Mouth != mouth) continue;
                // The first and last mouths run on beyond the line, as rest does.
                var s = i == 0 ? double.NegativeInfinity : segments[i].Start;
                var e = i == segments.Count - 1 ? double.PositiveInfinity : segments[i].End;
                covered += Math.Max(0, Math.Min(e, t + h) - Math.Max(s, t - h));
            }
            return Math.Clamp(covered / (2 * h), 0, 1);
        }

        var times = segments.Skip(1).SelectMany(s => new[] { s.Start - h, s.Start + h }).Order().ToList();
        var keys = new List<MotionWaypoint>();
        foreach (var t in times)
            if (keys.Count == 0 || t - keys[^1].Time >= MotionAnimated.TimeEpsilon)
                keys.Add(new MotionWaypoint(t, [Share(t)], MotionEase.Linear, MotionEase.Linear, 0, 0, 0, 0));
        return new MotionAnimated(MotionType.Real, keys.Count > 0 ? keys
            : [new MotionWaypoint(0, [Share(0)], MotionEase.Linear, MotionEase.Linear, 0, 0, 0, 0)]);
    }

    /// <summary>
    /// The mouths as a track: each sound's mouth from its start, led by <c>lead</c>; a run of one mouth merged; a shape
    /// shorter than <c>minimum</c> folded into the one before, unless it shuts the lips, which must show to read.
    /// </summary>
    private List<(double Start, double End, string Mouth)> Segments(double minimum)
    {
        var raw = new List<(double Start, double End, string Mouth)> { (double.NegativeInfinity, Start - lead, "rest") };
        foreach (var p in phonemes)
        {
            var (s, e) = (p.Start - lead, p.End - lead);
            if (s - raw[^1].End > 1e-9) raw.Add((raw[^1].End, s, "rest"));
            raw.Add((s, e, p.Mouth));
        }
        raw.Add((raw[^1].End, double.PositiveInfinity, "rest"));

        var merged = new List<(double Start, double End, string Mouth)>();
        foreach (var seg in raw)
        {
            var closes = Closes.Contains(seg.Mouth);
            if (merged.Count > 0 && (merged[^1].Mouth == seg.Mouth || (seg.End - seg.Start < minimum && !closes && seg.End != double.PositiveInfinity)))
                merged[^1] = (merged[^1].Start, seg.End, merged[^1].Mouth);
            else
                merged.Add(seg);
        }
        return merged;
    }

    /// <summary>Mouths that shut the lips, kept however short: a closure that is not seen is not heard either.</summary>
    static readonly HashSet<string> Closes = ["MBP", "A", "M", "B", "P"];

    /// <summary>A sound's length at rate 1, in seconds: the studio's rules.</summary>
    static double Length(string phoneme)
    {
        var b = Base(phoneme);
        if (IsVowel(b))
        {
            var d = Stress(phoneme) switch { 1 => 0.15, 2 => 0.11, _ => 0.065 };
            return b is "AW" or "AY" or "EY" or "OW" or "OY" ? d * 1.25 : d;
        }
        return b switch
        {
            "M" or "B" or "P" => 0.085,
            "T" or "D" or "K" or "G" => 0.07,
            "HH" => 0.05,
            "CH" or "JH" => 0.09,
            "F" or "V" or "TH" or "DH" or "S" or "Z" or "SH" or "ZH" => 0.08,
            _ => 0.06
        };
    }

    static string Base(string phoneme) => phoneme.TrimEnd('0', '1', '2');

    static int Stress(string phoneme) => phoneme[^1] is var c && char.IsDigit(c) ? c - '0' : -1;

    static bool IsVowel(string b) => b is "AA" or "AE" or "AH" or "AO" or "AW" or "AY" or "EH" or "ER" or "EY" or "IH" or "IY" or "OW" or "OY" or "UH" or "UW";

    /// <summary>
    /// Words, upper-cased as the dictionary is keyed, hyphenated ones whole; numbers (<c>4.5</c>, <c>$250</c>, <c>3rd</c>,
    /// <c>1,000</c>), for the speech backend to spell out; <c>%</c> as "percent"; and pauses: 0.25 s at a comma or dash,
    /// 0.45 s at the end of a sentence.
    /// </summary>
    static List<Token> Tokenize(string text, string who)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{who} needs a line to speak.");
        var tokens = new List<Token>();
        foreach (Match m in Regex.Matches(text,
            @"[$£]?\d[\d,]*(?:\.\d+)?(?:st|nd|rd|th)?|%|[A-Za-z]+(?:['’-][A-Za-z]+)*|[.!?]+|[,;:]|—|--|\s-\s"))
        {
            var v = m.Value.Trim();
            if (v == "%") tokens.Add(new Token(v, "PERCENT", 0));
            else if (v[0] is '$' or '£' || char.IsDigit(v[0])) tokens.Add(new Token(v.TrimEnd(','), null, 0, Number: true));
            else if (char.IsLetter(v[0])) tokens.Add(new Token(v, v.Replace('’', '\'').ToUpperInvariant(), 0));
            else tokens.Add(new Token(v, null, v[0] is '.' or '!' or '?' ? 0.45 : 0.25));
        }
        // A pause at the very end changes nothing, and two in a row are one.
        while (tokens.Count > 0 && tokens[^1].Pause > 0) tokens.RemoveAt(tokens.Count - 1);
        return [.. tokens.Where((t, i) => t.Pause == 0 || i == 0 || tokens[i - 1].Pause == 0)];
    }

    static Dictionary<string, string[]> Pronunciations(object? value, string who)
    {
        var result = new Dictionary<string, string[]>();
        if (value is null) return result;
        var d = JsInterop.AsDict(value) ?? throw new ArgumentException($"{who}'s pronounce is {{ word: 'PH ON EH1 M Z' }}.");
        foreach (DictionaryEntry e in d)
        {
            var sounds = (e.Value?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in sounds)
            {
                if (!MouthSets.Cmu.Contains(Base(s)) || (IsVowel(Base(s)) != (Stress(s) >= 0)))
                    throw new ArgumentException($"{who}'s pronounce '{e.Key}': '{s}' is not a CMU phoneme. Use the 39 ({string.Join(" ", MouthSets.Cmu)}), "
                        + "each vowel with its stress, 0, 1 or 2 (AE1).");
            }
            if (sounds.Length == 0) throw new ArgumentException($"{who}'s pronounce '{e.Key}' has no phonemes.");
            result[e.Key.ToString()!.ToUpperInvariant()] = sounds;
        }
        return result;
    }
    #endregion

    #region Types
    private sealed record Sound(string Phoneme, string Word, double Start, double End, string Mouth, string Source);

    /// <summary>A token: a word (upper case), a number to spell out, or a pause.</summary>
    private sealed record Token(string Raw, string? Word, double Pause, bool Number = false);

    /// <summary>One word as said: its sounds and where they came from.</summary>
    private sealed record Spoken(string Word, string[] Phonemes, string Source);
    #endregion
}

/// <summary>
/// The CMU Pronouncing Dictionary that <c>Motion.speech</c> reads: <c>Speech/cmudict.dict</c> beside the assembly, or
/// where the host says (<c>Tools:Cmudict</c>). Word → its first pronunciation, stress marks kept, words upper-cased.
/// </summary>
/// <remarks>
/// Fetched by <c>tools/bootstrap.py</c> at a pinned CMU Sphinx commit with its licence, not committed. Read once, on first
/// use; not remembered when missing, so fetching it mid-session is enough. The engine reads no environment variables.
/// </remarks>
public static class CmuDictionary
{
    #region Fields
    static string? _path;
    static Dictionary<string, string[]>? _words;
    static readonly object Gate = new();
    #endregion

    #region Properties
    /// <summary>An explicit path to the dictionary, or null for the copy beside the assembly.</summary>
    public static string? PathOverride
    {
        get => _path;
        set { lock (Gate) { _path = value; _words = null; } }
    }

    /// <summary>Where the dictionary is read from.</summary>
    public static string Path => _path is { Length: > 0 } given ? given : System.IO.Path.Combine(AppContext.BaseDirectory, "Speech", "cmudict.dict");

    /// <summary>Whether it is there to read.</summary>
    public static bool Available => File.Exists(Path);

    /// <summary>How many words it holds.</summary>
    public static int Count => Words.Count;

    static Dictionary<string, string[]> Words
    {
        get { lock (Gate) return _words ??= Load(Path); }
    }
    #endregion

    #region Methods
    /// <summary>Whether it has this word, exactly (upper case).</summary>
    internal static bool Has(string word) => Words.ContainsKey(word);

    /// <summary>A word's first pronunciation.</summary>
    internal static string[] Lookup(string word) => Words.TryGetValue(word, out var s) ? s : throw new KeyNotFoundException(word);

    /// <summary>
    /// <c>word PH ON EH1 M Z</c> per line, lower case; <c>word(2)</c> a further pronunciation, which the first wins over;
    /// <c># place, danish</c> a note after some, dropped. A line that is not plain ASCII is skipped.
    /// </summary>
    internal static Dictionary<string, string[]> Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Motion.speech needs the CMU Pronouncing Dictionary at {path}, and it is not there. It is fetched rather than "
                + "committed: run `py tools/bootstrap.py --only speech` from the checkout, then build again.");
        var words = new Dictionary<string, string[]>(140_000);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.IndexOf('#') is var hash and >= 0 ? raw[..hash] : raw;
            if (line.Length == 0 || line.Any(c => c > 127)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].EndsWith(')')) continue;
            words.TryAdd(parts[0].ToUpperInvariant(), parts[1..]);
        }
        return words;
    }
    #endregion
}

/// <summary>
/// CMU's 39 phonemes to mouths: Preston Blair's ten, Fleming &amp; Dobbs's eleven and Rhubarb's nine, as Papagayo-NG maps
/// them (<c>phonemes/preston_blair.json</c>, <c>fleming_dobbs.json</c>, <c>rhubarb.json</c>; GPL-2-or-later), or the phonemes
/// themselves, or a map given.
/// </summary>
internal static class MouthSets
{
    #region Fields
    public static readonly string[] Cmu =
        ["AA", "AE", "AH", "AO", "AW", "AY", "B", "CH", "D", "DH", "EH", "ER", "EY", "F", "G", "HH", "IH", "IY", "JH", "K",
         "L", "M", "N", "NG", "OW", "OY", "P", "R", "S", "SH", "T", "TH", "UH", "UW", "V", "W", "Y", "Z", "ZH"];

    static readonly Dictionary<string, string> Blair = Table(
        "AA AI AE AI AH E AO O AW U AY AI B MBP CH WQ D E DH L EH E ER WQ EY E F FV G E HH E IH AI IY E JH WQ K E L L M MBP " +
        "N etc NG E OW WQ OY WQ P MBP R L S etc SH WQ T E TH E UH U UW U V FV W WQ Y E Z etc ZH WQ");

    static readonly Dictionary<string, string> FlemingDobbs = Table(
        "AA AA AE EHSZ AH AA AO O AW O AY EHSZ B MBP CH SH D MBP DH NLTDR EH EHSZ ER NLTDR EY EHSZ F FV G GK HH EHSZ IH IY " +
        "IY IY JH SH K GK L NLTDR M MBP N NLTDR NG NLTDR OW O OY O P MBP R NLTDR S SH SH SH T TH TH TH UH O UW O V FV W O " +
        "Y IY Z SH ZH SH");

    static readonly Dictionary<string, string> Rhubarb = Table(
        "AA D AE D AH C AO E AW E AY C B A CH G D C DH C EH C ER E EY C F G G C HH C IH C IY C JH F K C L C M A N B NG C " +
        "OW F OY F P A R H S G SH F T C TH H UH F UW F V G W F Y C Z B ZH B");
    #endregion

    #region Methods
    /// <summary>The set a script names, or a map it gives: <c>{ AE: 'wide', ... }</c> covering all 39.</summary>
    public static (string Name, IReadOnlyDictionary<string, string> Table) Read(object? value, string who) => value switch
    {
        null or "blair" or "prestonBlair" => ("blair", Blair),
        "flemingDobbs" => ("flemingDobbs", FlemingDobbs),
        "rhubarb" => ("rhubarb", Rhubarb),
        "cmu" => ("cmu", Cmu.ToDictionary(p => p, p => p)),
        string s => throw new ArgumentException($"{who}'s set '{s}' is not one of blair, flemingDobbs, rhubarb, cmu; or give a map {{ AE: 'wide', ... }}."),
        _ when JsInterop.AsDict(value) is { } d => ("custom", Custom(d, who)),
        _ => throw new ArgumentException($"{who}'s set is a name or a map from CMU phonemes to mouth names.")
    };

    static Dictionary<string, string> Custom(IDictionary d, string who)
    {
        // Through the keys: a generic dictionary enumerates as KeyValuePair, not DictionaryEntry.
        var map = d.Keys.Cast<object>().ToDictionary(k => k.ToString()!.ToUpperInvariant(), k => d[k]?.ToString() ?? "");
        if (map.Keys.FirstOrDefault(k => !Cmu.Contains(k)) is { } extra)
            throw new ArgumentException($"{who}'s set maps '{extra}', which is not one of CMU's 39 phonemes.");
        if (Cmu.Where(p => !map.ContainsKey(p)).ToList() is { Count: > 0 } missing)
            throw new ArgumentException($"{who}'s set has no mouth for {string.Join(", ", missing)}; map all 39.");
        if (map.Values.FirstOrDefault(v => v.Length == 0 || v == "rest") is { } bad)
            throw new ArgumentException($"{who}'s set maps a phoneme to '{bad}'; 'rest' is the mouth between sounds and names cannot be empty.");
        return map;
    }

    static Dictionary<string, string> Table(string pairs)
    {
        var p = pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Enumerable.Range(0, p.Length / 2).ToDictionary(i => p[2 * i], i => p[2 * i + 1]);
    }
    #endregion
}
