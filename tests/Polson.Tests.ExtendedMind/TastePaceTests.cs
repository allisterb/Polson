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
