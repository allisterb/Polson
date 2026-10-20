namespace Polson.Tests.ExtendedMind;

using System.Collections.Generic;
using System.Linq;

using global::Polson.ExtendedMind.Taste;
using global::Polson.Tests;
using Xunit;

/// <summary>
/// <c>result.pace()</c> and <c>Taste.comparePace</c>: Qloo's pace tags counted over an audience's films and shows, and two
/// audiences compared by Fisher's exact test. The counts and p-values are the Tokyo and Mumbai 45–54 measurement of
/// 2026-10-17 (<c>docs/internal/audience-tempo-measurement.md</c>); the reference p-values were computed independently.
/// </summary>
public class TastePaceTests : TestsRuntime
{
    #region Methods
    [Fact]
    public void PaceCountsFilmsAndShowsByQloosOwnTags()
    {
        var result = Profile("tokyo",
            Film("Fast One", "Fast paced"),
            Show("Slow One", "slow burn"),                       // Qloo's casing varies; the match does not care
            Film("Both", "Kinetic", "Slow burn pacing"),
            Film("Neither", "Suspenseful"),
            Artist("An Artist", "Energetic"));                   // not a film or show: not counted

        var pace = result.Pace();

        Assert.Equal(1, pace["fast"]);
        Assert.Equal(1, pace["slow"]);
        Assert.Equal(1, pace["both"]);
        Assert.Equal(1, pace["untagged"]);
        Assert.Equal(4, pace["n"]);
        Assert.Equal(0.5, pace["fastShare"]);
        Assert.Equal(["Fast One"], (string[])pace["fastTitles"]!);
        Assert.Equal(["Slow One"], (string[])pace["slowTitles"]!);
    }

    [Fact]
    public void NoPaceTagsIsNoShareRatherThanZero()
    {
        var pace = Profile("none", Film("Plain", "Drama")).Pace();
        Assert.Null(pace["fastShare"]);
    }

    /// <summary>The measured audiences: Tokyo 14 fast / 12 slow, Mumbai 23 / 5, p = 0.0397; the narrower count 0.0171.</summary>
    [Theory]
    [InlineData(14, 12, 23, 5, 0.039740, true)]
    [InlineData(10, 14, 19, 5, 0.017128, true)]
    [InlineData(5, 5, 5, 5, 1.0, false)]
    public void ComparePaceTestsTheDifference(int tf, int ts, int mf, int ms, double p, bool differ)
    {
        var tokyo = Profile("tokyo", [.. Films("t-fast", tf, "Fast paced"), .. Films("t-slow", ts, "Slow burn")]);
        var mumbai = Profile("mumbai", [.. Films("m-fast", mf, "Action packed"), .. Films("m-slow", ms, "Deliberate pacing")]);

        var compared = new TasteToolkit(new TasteRegistry()).ComparePace(tokyo, mumbai);

        Assert.Equal(p, (double)compared["p"]!, 3);
        Assert.Equal(differ, compared["differ"]);
        Assert.Contains(differ ? "a measurable difference" : "no measurable difference", (string)compared["summary"]!);
    }

    [Fact]
    public void ComparePaceAllWithTwoAudiencesIsComparePace()
    {
        var (tokyo, mumbai) = (Lean("tokyo", 14, 12), Lean("mumbai", 23, 5));
        var taste = new TasteToolkit(new TasteRegistry());

        var all = taste.ComparePaceAll([tokyo, mumbai]);
        var pair = Assert.Single((List<Dictionary<string, object?>>)all["pairs"]!);

        Assert.Equal((double)taste.ComparePace(tokyo, mumbai)["p"]!, (double)pair["pAdjusted"]!, 4);
        Assert.True((bool)all["differ"]!);
    }

    [Fact]
    public void ComparePaceAllFindsTheCampsInASet()
    {
        // Two slow-leaning audiences and two fast-leaning ones: the four pairs across the camps differ, the two within do not.
        var set = new[] { Lean("berlin", 5, 15), Lean("seoul", 5, 15), Lean("lagos", 15, 5), Lean("dubai", 15, 5) };

        var all = new TasteToolkit(new TasteRegistry()).ComparePaceAll(set);
        var pairs = (List<Dictionary<string, object?>>)all["pairs"]!;

        Assert.Equal(6, pairs.Count);
        foreach (var pair in pairs)
        {
            var crosses = ((string)pair["a"]!).Contains("berlin") || ((string)pair["a"]!).Contains("seoul")
                ? ((string)pair["b"]!).Contains("lagos") || ((string)pair["b"]!).Contains("dubai")
                : false;
            Assert.Equal(crosses, pair["differ"]);
            Assert.True((double)pair["pAdjusted"]! >= (double)pair["p"]!, "an adjusted p is never smaller");
        }
        Assert.Contains("4 of 6 pairs", (string)all["summary"]!);
    }

    [Fact]
    public void ComparePaceAllDoesNotCountAChanceDifferenceInALargerSet()
    {
        // Tokyo against Mumbai alone is p = 0.040, a difference. Tested as one of three pairs, it is not one.
        var set = new[] { Lean("tokyo", 14, 12), Lean("mumbai", 23, 5), Lean("osaka", 14, 12) };

        var all = new TasteToolkit(new TasteRegistry()).ComparePaceAll(set);
        var pairs = (List<Dictionary<string, object?>>)all["pairs"]!;

        Assert.Contains(pairs, p => (double)p["p"]! < 0.05);
        Assert.False((bool)all["differ"]!);
        Assert.Contains("No pair differs measurably", (string)all["summary"]!);
    }

    [Fact]
    public void ComparePaceAllNeedsTwoOrMore()
    {
        var taste = new TasteToolkit(new TasteRegistry());
        Assert.Throws<System.ArgumentException>(() => taste.ComparePaceAll([Lean("alone", 3, 3)]));
    }

    /// <summary>
    /// productad1: 'audience A places' came after the profile 'audience A', <c>find</c> returned the places, and
    /// <c>pace()</c> read 0 / 0 for every city — a false "no measurable difference". The exact label wins now.
    /// </summary>
    [Fact]
    public void FindPrefersTheExactLabelOverALaterOneThatContainsIt()
    {
        var registry = new TasteRegistry();
        registry.Add(id => new TasteResult
        {
            Id = id, Op = "profile", Label = "audience A", Ok = true,
            Entities = [.. Films("fast", 15, "Fast paced"), .. Films("slow", 6, "Slow burn")],
        });
        registry.Add(id => new TasteResult { Id = id, Op = "insights", Label = "audience A places", Ok = true, Entities = [] });
        var taste = new TasteToolkit(registry);

        Assert.Equal(15, taste.Find("audience a")!.Pace()["fast"]);
        Assert.Equal(15, taste.Find("  audience A ")!.Pace()["fast"]);
        Assert.Equal("audience A places", taste.Find("places")!.Label);   // containment still finds by purpose
        Assert.Null(taste.Find("audience B"));
    }

    private static TasteResult Lean(string label, int fast, int slow) =>
        Profile(label, [.. Films($"{label}-fast", fast, "Fast paced"), .. Films($"{label}-slow", slow, "Slow burn")]);

    private static TasteResult Profile(string label, params object?[] entities) =>
        new() { Id = "t1", Op = "profile", Label = label, Ok = true, Entities = entities };

    private static IEnumerable<object?> Films(string prefix, int count, string tag) =>
        Enumerable.Range(0, count).Select(i => Film($"{prefix}{i}", tag));

    private static object? Film(string name, params string[] tags) => Entity(name, "urn:entity:movie", tags);

    private static object? Show(string name, params string[] tags) => Entity(name, "urn:entity:tv_show", tags);

    private static object? Artist(string name, params string[] tags) => Entity(name, "urn:entity:artist", tags);

    private static object? Entity(string name, string type, string[] tags) => new Dictionary<string, object?>
    {
        ["name"] = name,
        ["type"] = type,
        ["types"] = new[] { type, "urn:entity" },
        ["tags"] = tags.Select(t => (object?)new Dictionary<string, object?> { ["name"] = t, ["type"] = "urn:tag:plot:qloo" }).ToArray(),
    };
    #endregion
}
