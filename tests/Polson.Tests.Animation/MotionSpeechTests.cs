namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Polson.Animation;

using Xunit;

/// <summary>
/// Lip sync from text: CMUdict for the sounds, Papagayo-NG's tables for the mouths, the studio's rules for the timing.
/// </summary>
public class MotionSpeechTests : TestsRuntime
{
    static MotionSpeech Say(string text, params (string Key, object? Value)[] options) =>
        new MotionToolkit().Speech(text, options.Length == 0 ? null : Opts(options));

    static string[] Field(MotionSpeech s, string key) => [.. s.Phonemes.Select(p => p[key]!.ToString()!)];

    [Fact]
    public void ASentenceHasTheDictionarysSoundsAndBlairsMouths()
    {
        var s = Say("The cat in the hat has a rat.");
        Assert.Equal("DH AH0 K AE1 T IH0 N DH AH0 HH AE1 T HH AE1 Z AH0 R AE1 T", string.Join(" ", Field(s, "phoneme")));
        Assert.Equal("L E E AI E AI etc L E E AI E E AI etc E L AI E", string.Join(" ", Field(s, "mouth")));
        Assert.Equal(["The", "cat", "in", "the", "hat", "has", "a", "rat"], s.Words.Select(w => (string)w["word"]!));
        Assert.Equal("blair", s.Set);
    }

    [Fact]
    public void TheBeforeAVowelIsThee()
    {
        Assert.Equal("DH IY0", string.Join(" ", Field(Say("the apple"), "phoneme").Take(2)));
        Assert.Equal("DH AH0", string.Join(" ", Field(Say("the pear"), "phoneme").Take(2)));
    }

    [Fact]
    public void StressPhraseEndsPausesAndRateShapeTheTiming()
    {
        double Length(MotionSpeech s, int i) => (double)s.Phonemes[i]["end"]! - (double)s.Phonemes[i]["start"]!;

        // "a cat sat": AH0 unstressed, AE1 stressed, and the last word's vowel drawn out.
        var s = Say("a cat sat");
        Assert.True(Length(s, 2) > 2 * Length(s, 0));
        Assert.True(Length(s, 5) > Length(s, 2) * 1.2);

        // A comma pauses less than a full stop, and both pause.
        double Gap(string text) => (double)Say(text).Words[1]["start"]! - (double)Say(text).Words[0]["end"]!;
        Assert.Equal(0d, Gap("cat sat"), 9);
        Assert.InRange(Gap("cat, sat"), 0.2, 0.3);
        Assert.InRange(Gap("cat. Sat"), 0.4, 0.5);

        var slow = Say("The cat in the hat");
        Assert.Equal(slow.Duration / 2, Say("The cat in the hat", ("rate", 2d)).Duration, 9);
        Assert.Equal(1.5, Say("cat", ("start", 1.5d)).Start, 9);
    }

    /// <summary>The track rests before and after, leads its sounds, never repeats a mouth, and keeps every closure.</summary>
    [Fact]
    public void TheMouthTrackLeadsMergesAndKeepsClosures()
    {
        var s = Say("Mom, bob a pop", ("start", 1d));
        var keys = s.Mouths.Select(k => ((double)k[0]!, (string)k[1]!)).ToList();
        Assert.Equal("rest", keys[0].Item2);
        Assert.Equal("rest", keys[^1].Item2);
        Assert.True(keys.Zip(keys.Skip(1)).All(p => p.First.Item1 < p.Second.Item1 && p.First.Item2 != p.Second.Item2));
        Assert.Equal(1 - 0.04, keys[1].Item1, 9);
        // M-M, B-B and P-P: six closures, each its own key, none folded away.
        Assert.Equal(6, keys.Count(k => k.Item2 == "MBP"));
        Assert.Equal("MBP", s.MouthAt(1.01));
        Assert.Equal("rest", s.MouthAt(0));
        Assert.Equal("rest", s.MouthAt(s.End + 1));
        Assert.Equal("Mom", s.WordAt(1.05));
        Assert.Equal("", s.WordAt(0.5));
    }

    /// <summary>At every time one mouth or a crossfade of two: the weights sum to 1.</summary>
    [Fact]
    public void TheWeightsCrossfadeAndSumToOne()
    {
        var s = Say("The cat in the hat has a rat.");
        var w = s.Weights();
        Assert.Equal(s.Names.OrderBy(n => n), w.Keys.OrderBy(n => n));
        for (var t = -0.5; t < s.End + 0.5; t += 0.013)
        {
            var values = w.Values.Select(n => (double)((MotionNode)n!).At(t)).ToArray();
            Assert.All(values, v => Assert.InRange(v, -1e-9, 1 + 1e-9));
            Assert.Equal(1d, values.Sum(), 6);
        }
        Assert.Equal(1d, (double)((MotionNode)w["rest"]!).At(-1), 9);
        Assert.Equal(["AI", "E"], s.Weights(Opts(("names", new object[] { "AI", "E" }))).Keys.Order());
    }

    /// <summary>With guessing off (or no backend), a word the dictionary lacks and a number are refused by name.</summary>
    [Fact]
    public void UnknownWordsAreRefusedAndCanBePronounced()
    {
        var e = Assert.Throws<ArgumentException>(() => Say("Tomas met Zorblax", ("guess", false)));
        Assert.Contains("'Zorblax'", e.Message);
        Assert.Contains("pronounce", e.Message);
        var s = Say("Tomas met Zorblax", ("pronounce", Opts(("zorblax", "Z AO1 R B L AE0 K S"))));
        Assert.Equal("Z", Field(s, "phoneme")[^8]);
        Assert.Equal("pronounce", Field(s, "source")[^1]);
        Assert.Throws<ArgumentException>(() => Say("hi", ("pronounce", Opts(("hi", "HH AY")))));
        Assert.Throws<ArgumentException>(() => Say("hi", ("pronounce", Opts(("hi", "HH QQ1")))));
        Assert.Contains("'42'", Assert.Throws<ArgumentException>(() => Say("I am 42", ("guess", false))).Message);
        Assert.Throws<ArgumentException>(() => Say("hi", ("guess", "yes")));
        Assert.Throws<ArgumentException>(() => Say("  "));
        Assert.Throws<ArgumentException>(() => Say("hi", ("speed", 2d)));
    }

    /// <summary>Without the backend, the refusal names what is missing and how to install it.</summary>
    [Fact]
    public void WithoutTheBackendTheRefusalSaysHowToGetIt()
    {
        var was = SpeechBackend.PythonOverride;
        try
        {
            SpeechBackend.PythonOverride = Path.Combine(Path.GetTempPath(), "no-such-python.exe");
            Assert.False(SpeechBackend.Available);
            var e = Assert.Throws<ArgumentException>(() => Say("Tomas met Qwyzzle at 9"));
            Assert.Contains("'Qwyzzle'", e.Message);
            Assert.Contains("'9'", e.Message);
            Assert.Contains("--only speech-venv", e.Message);
            Assert.Contains("python-speech", e.Message);
        }
        finally
        {
            SpeechBackend.PythonOverride = was;
        }
    }

    // The speech backend (python-speech/, see src/speech): skipped where it is not installed, as the Synfig oracles are.

    [Fact]
    public void TheBackendGuessesAWordNoDictionaryHas()
    {
        if (!SpeechBackend.Available) return;
        var s = Say("Tomas met Zorblax");
        Assert.Equal("dictionary", s.Words[0]["source"]);
        Assert.Equal("model", s.Words[2]["source"]);
        Assert.Equal("zorblax", Assert.Single(s.Guessed)["word"]);
        Assert.StartsWith("Z ", (string)s.Guessed[0]["phonemes"]!);
    }

    /// <summary>Only the part no one knows is guessed: a hyphenated word is its parts.</summary>
    [Fact]
    public void AHyphenatedWordIsItsParts()
    {
        if (!SpeechBackend.Available) return;
        var s = Say("a zorblax-cat");
        Assert.Equal("zorblax", Assert.Single(s.Guessed)["word"]);
        Assert.Equal("K AE1 T", string.Join(" ", Field(s, "phoneme").TakeLast(3)));
        Assert.Equal("model", s.Words[1]["source"]);
    }

    [Fact]
    public void NumbersAreSpelledOutAndSaid()
    {
        if (!SpeechBackend.Available) return;
        var s = Say("I paid $250 for 4.5 cats, 42% of them.");
        string Said(string word) => (string)s.Words.First(w => (string)w["word"]! == word)["said"]!;
        Assert.Equal("two hundred fifty dollars", Said("$250"));
        Assert.Equal("four point five", Said("4.5"));
        Assert.Equal("forty-two", Said("42"));
        Assert.Equal("percent", Said("%"));
        Assert.Equal("number", s.Words.First(w => (string)w["word"]! == "4.5")["source"]);
        Assert.Empty(s.Guessed);

        // A number is one word on the track, spanning everything it says; "the" before "eighth" is "thee".
        var eighth = Say("the 8th cat");
        Assert.Equal(3, eighth.Words.Count);
        Assert.Equal("DH IY0", string.Join(" ", Field(eighth, "phoneme").Take(2)));
    }

    [Fact]
    public void OtherMouthSetsAndYourOwnMap()
    {
        Assert.Equal("D", Field(Say("cat", ("set", "rhubarb")), "mouth")[1]);
        Assert.Equal("GK", Field(Say("cat", ("set", "flemingDobbs")), "mouth")[0]);
        Assert.Equal("AE", Field(Say("cat", ("set", "cmu")), "mouth")[1]);
        var all = MotionSetsAll();
        Assert.Equal("open", Field(Say("cat", ("set", all)), "mouth")[1]);
        all.Remove("AE");
        Assert.Contains("AE", Assert.Throws<ArgumentException>(() => Say("cat", ("set", all))).Message);
        Assert.Throws<ArgumentException>(() => Say("cat", ("set", "disney")));

        static Dictionary<string, object?> MotionSetsAll() =>
            "AA AE AH AO AW AY B CH D DH EH ER EY F G HH IH IY JH K L M N NG OW OY P R S SH T TH UH UW V W Y Z ZH"
                .Split(' ').ToDictionary(p => p, p => (object?)(p is "AA" or "AE" or "AY" ? "open" : "closed"));
    }

    /// <summary>The track keys a switch of drawn mouths, and the weights a face's poses, with nothing in between.</summary>
    [Fact]
    public void ItDrivesASwitchAndAFace()
    {
        var s = Say("The cat in the hat");
        var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 300d), ("duration", 2d)));
        var mouths = comp.Switch(Opts(("active", s.Mouths)));
        foreach (var name in s.Names) mouths.Circle(Opts(("origin", new[] { 150d, 150d }), ("radius", 10d), ("color", "#000000"), ("desc", name)));
        using (comp.Render(0.3)) { }

        var poses = s.Names.ToDictionary(n => n, n => (object?)Opts(("AU25", n == "rest" ? 0d : 0.6d), ("AU26", n == "AI" ? 0.5d : 0d)));
        var face = comp.Face(Opts(("origin", new[] { 150d, 150d }), ("height", 260d), ("poses", poses), ("channels", s.Weights())));
        Assert.Equal(s.Names.Count, face.Poses.Count);
    }

    /// <summary>The fetched dictionary sits beside the assembly with its licence, which must travel with it.</summary>
    /// <summary>
    /// The Gemini TTS recording of the face-speech line, from the speech probe (docs/internal/spikes/tts-probe, gitignored):
    /// found by walking up from the test, or null where it is not, and the tests that need it skip.
    /// </summary>
    static string? Recording()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Path.Combine(dir.FullName, "docs", "internal", "spikes", "tts-probe", "gemini-2.5-flash-tts.wav") is var p && File.Exists(p)) return p;
        return null;
    }

    const string ProbeLine = "Who knew? Bob moved the moon so far, for fun!";

    /// <summary>
    /// Timed against a recording, every word sits where the voice says it, not where the rules would put it. Measured by
    /// the aligner on this recording: "Bob" at 1.44 s and "for" at 3.99 s, which the rules put at 1.30 s and 3.30 s.
    /// </summary>
    [Fact]
    public void AudioTimesTheWordsToTheRecording()
    {
        if (!SpeechBackend.AlignAvailable || Recording() is not { } wav) return;
        var s = Say(ProbeLine, ("audio", wav));
        Assert.True(s.Aligned);
        Assert.Equal(wav, s.Audio);
        double At(string word) => (double)s.Words.First(w => (string)w["word"]! == word)["start"]!;
        Assert.InRange(At("Bob"), 1.38, 1.46);
        Assert.InRange(At("for"), 3.93, 4.01);
        Assert.All(s.Words, w => Assert.InRange((double)w["confidence"]!, 0.3, 1.0));

        // The recording's pauses are the line's: "far," is followed by a long one the rules would not give.
        var far = s.Words.First(w => (string)w["word"]! == "far");
        Assert.True(At("for") - (double)far["end"]! > 0.3);
        Assert.Equal("rest", s.MouthAt(((double)far["end"]! + At("for")) / 2));

        // Its sounds fill each word, end to end, and a start moves the whole line.
        var p = s.Phonemes;
        for (var i = 1; i < p.Count; i++)
            if ((string)p[i]["word"]! == (string)p[i - 1]["word"]!) Assert.Equal((double)p[i - 1]["end"]!, (double)p[i]["start"]!, 9);
        var later = Say(ProbeLine, ("audio", wav), ("start", 2d));
        Assert.Equal(At("Bob") + 2, (double)later.Words.First(w => (string)w["word"]! == "Bob")["start"]!, 9);
    }

    [Fact]
    public void BadAudioIsRefused()
    {
        Assert.Contains("no recording", Assert.Throws<ArgumentException>(() => Say("cat", ("audio", Path.Combine(Path.GetTempPath(), "no-such-line.wav")))).Message);
        Assert.Throws<ArgumentException>(() => Say("cat", ("audio", 3d)));
        if (Recording() is { } wav)
            Assert.Contains("rate", Assert.Throws<ArgumentException>(() => Say("cat", ("audio", wav), ("rate", 1.2d))).Message);
        Assert.False(Say("cat").Aligned);
        Assert.Null(Say("cat").Words[0]["confidence"]);
    }

    /// <summary>Blair's ten mouths as unit poses: a face speaks with its own mouth, and a scale opens it more or less.</summary>
    [Fact]
    public void BlairsMouthsAsFacePoses()
    {
        var s = Say("Who knew? Bob moved the moon so far, for fun!");
        var poses = s.Poses();
        Assert.Equal(["AI", "E", "FV", "L", "MBP", "O", "U", "WQ", "etc", "rest"], poses.Keys.Order(StringComparer.Ordinal));
        Assert.All(s.Names, n => Assert.Contains(n, poses.Keys));

        // Louder opens the opening units only: a shouted "m" still closes.
        var loud = s.Poses(Opts(("scale", 1.5d)));
        double Unit(Dictionary<string, object?> p, string pose, string unit) => (double)((Dictionary<string, object?>)p[pose]!)[unit]!;
        Assert.Equal(Unit(poses, "AI", "AU26") * 1.5, Unit(loud, "AI", "AU26"), 9);
        Assert.Equal(Unit(poses, "MBP", "AU24"), Unit(loud, "MBP", "AU24"), 9);

        var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 300d), ("duration", 4d)));
        var face = comp.Face(Opts(("origin", new[] { 150d, 150d }), ("height", 260d), ("poses", poses), ("channels", s.Weights())));
        var at = s.Phonemes.First(p => (string)p["mouth"]! == "MBP");
        var mid = ((double)at["start"]! + (double)at["end"]!) / 2 - 0.04;
        var mouth = (IDictionary<string, object?>)face.HeadAt(mid)["mouthGuides"]!;
        Assert.True(Convert.ToSingle(mouth["press"]) > 0.5f, "the face presses its lips for the M");

        Assert.Throws<ArgumentException>(() => Say("cat", ("set", "rhubarb")).Poses());
        Assert.Throws<ArgumentException>(() => s.Poses(Opts(("loud", 1d))));
    }

    [Fact]
    public void TheDictionaryIsCmudictWithItsLicenceBesideIt()
    {
        Assert.True(File.Exists(CmuDictionary.Path), $"no dictionary at {CmuDictionary.Path}: run `py tools/bootstrap.py --only speech`");
        var licence = File.ReadAllText(Path.Combine(Path.GetDirectoryName(CmuDictionary.Path)!, "cmudict.LICENSE"));
        Assert.Contains("Copyright (C) 1993-2015 Carnegie Mellon University", licence);
        Assert.Contains("Redistribution and use in source and binary forms", licence);
        // 135,166 lines at the pinned commit, of which 126,052 are words and the rest their further pronunciations.
        Assert.Equal(126_052, CmuDictionary.Count);
    }

    /// <summary>Without the file, it says what is missing and how to fetch it, rather than failing on a lookup.</summary>
    [Fact]
    public void AMissingDictionaryIsNamed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-cmudict.dict");
        var e = Assert.Throws<InvalidOperationException>(() => CmuDictionary.Load(missing));
        Assert.Contains(missing, e.Message);
        Assert.Contains("bootstrap.py --only speech", e.Message);
    }

    private static Dictionary<string, object?> Opts(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
