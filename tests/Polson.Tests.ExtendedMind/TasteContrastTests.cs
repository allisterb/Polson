namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.Linq;

using global::Polson.ExtendedMind.Taste;
using global::Polson.Tests;
using Xunit;

/// <summary>
/// <c>Taste.contrast</c>: two audiences' profiles split into shared and one-sided titles, the tags that lean each way,
/// and their pace. The profiles are built here; no Qloo response is a fixture (design doc §6.3).
/// </summary>
public class TasteContrastTests : TestsRuntime
{
    #region Methods
    [Fact]
    public void TestTitlesSplitIntoSharedAndEachSidesOwnByIdInRankOrder()
    {
        var a = Profile("t1", "Tokyo", Movies(("m1", "Shared One", 2), ("m2", "Tokyo Only", 1), ("m3", "Shared Two", 4)));
        var b = Profile("t2", "Mumbai", Movies(("m3", "Shared Two", 1), ("m4", "Mumbai Only", 2), ("m1", "Shared One", 3)));

        var film = Domain(TasteContrast.Compare(a, b), "urn:entity:movie");

        Assert.Equal(["Shared One", "Shared Two"], Names(film["shared"]));    // by rank in both: 2+3, then 4+1
        Assert.Equal(["Tokyo Only"], Names(film["onlyA"]));
        Assert.Equal(["Mumbai Only"], Names(film["onlyB"]));
        var shared = (Dictionary<string, object?>)((object?[])film["shared"]!)[0]!;
        Assert.Equal((2, 3), ((int)shared["rankA"]!, (int)shared["rankB"]!));
        Assert.Equal(0.5, film["overlap"]);                                   // 2 shared of 4 titles in all
        Assert.Equal((3, 3), ((int)film["nA"]!, (int)film["nB"]!));
    }

    [Fact]
    public void TestADomainOnlyOneProfileReadIsAllThatSides()
    {
        var a = Profile("t1", "A", Movies(("m1", "Film", 1)), ("urn:entity:artist", [Entity("x1", "An Artist", 1)]));
        var b = Profile("t2", "B", Movies(("m1", "Film", 1)));

        var artist = Domain(TasteContrast.Compare(a, b), "urn:entity:artist");

        Assert.Equal(["An Artist"], Names(artist["onlyA"]));
        Assert.Empty((object?[])artist["onlyB"]!);
        Assert.Equal(0.0, artist["overlap"]);
    }

    [Fact]
    public void TestATagLeansToASideOnlyWhenTwoOrMoreOfItsTitlesCarryItMoreOftenThanTheOthers()
    {
        var a = Profile("t1", "A", Movies(
            ("a1", "Quiet One", 1, ["Slow burn", "Melancholy"]), ("a2", "Quiet Two", 2, ["Slow burn", "Melancholy"]),
            ("a3", "Quiet Three", 3, ["Slow burn", "Comedy"]), ("a4", "Odd One", 4, ["Rare tag"])));
        var b = Profile("t2", "B", Movies(
            ("b1", "Loud One", 1, ["Fast paced", "Comedy"]), ("b2", "Loud Two", 2, ["Fast paced", "Comedy"])));

        var film = Domain(TasteContrast.Compare(a, b), "urn:entity:movie");
        var tagsA = ((object?[])film["tagsA"]!).Cast<Dictionary<string, object?>>().ToArray();
        var tagsB = ((object?[])film["tagsB"]!).Cast<Dictionary<string, object?>>().ToArray();

        Assert.Equal(["Slow burn", "Melancholy"], tagsA.Select(t => (string)t["tag"]!));    // 3 of 4, then 2 of 4; "Rare tag" is one title
        Assert.Equal((3, 0), ((int)tagsA[0]["a"]!, (int)tagsA[0]["b"]!));
        Assert.Equal(["Quiet One", "Quiet Two", "Quiet Three"], (string[])tagsA[0]["titles"]!);
        Assert.Equal(["Comedy", "Fast paced"], tagsB.Select(t => (string)t["tag"]!).OrderBy(t => t));   // Comedy: 2 of 2 against 1 of 4
    }

    [Fact]
    public void TestTheSummaryLeadsWithPaceThenNamesEachDomainsTitlesAndWhereTheListsAre()
    {
        // poster5 found c.pace, its most decision-changing number, only by dumping Object.keys, and looked for c.onlyA
        // at the top level, where it is undefined.
        var a = Profile("t1", "A", Movies(("a1", "Slow", 1, ["Slow burn"]), ("s1", "Both", 2, []), ("a2", "Two", 3, []),
            ("a3", "Three", 4, []), ("a4", "Four", 5, [])));
        var b = Profile("t2", "B", Movies(("b1", "Fast", 1, ["Fast paced"]), ("s1", "Both", 2, [])));

        var contrast = TasteContrast.Compare(a, b);
        var summary = (string)contrast["summary"]!;

        Assert.StartsWith("Pace — t1 (A): 0% fast (0 fast, 1 slow); t2 (B): 100% fast (1 fast, 0 slow); Fisher's exact p = ", summary);
        Assert.Contains("movie: 1 shared (Both), 4 only t1 (Slow, Two, Three, …), 1 only t2 (Fast)", summary);
        Assert.Contains("c.domains[i].shared", summary);
        Assert.Contains("c.pace.a", summary);
        Assert.NotNull(((Dictionary<string, object?>)contrast["pace"]!)["p"]);
    }

    [Fact]
    public void TestOnlyTwoGoodProfilesCanBeContrasted()
    {
        var good = Profile("t1", "A", Movies(("m1", "Film", 1)));
        var insights = new TasteResult { Id = "t2", Op = "insights", Ok = true };
        var failed = new TasteResult { Id = "t3", Op = "profile", Ok = false, Error = "Qloo answered 429." };

        Assert.Contains("t2 is a 'insights'", Assert.Throws<ArgumentException>(() => TasteContrast.Compare(good, insights)).Message);
        Assert.Contains("t3 failed", Assert.Throws<ArgumentException>(() => TasteContrast.Compare(good, failed)).Message);
    }

    [Fact]
    public void TestTheToolkitRecordsTheContrastForTheStudio()
    {
        var recorded = new List<string>();
        var taste = new TasteToolkit(new TasteRegistry(), record: (type, _) => recorded.Add(type));

        taste.Contrast(Profile("t1", "A", Movies(("m1", "Film", 1))), Profile("t2", "B", Movies(("m1", "Film", 1))));

        Assert.Equal(["taste.contrast"], recorded);
    }

    private static Dictionary<string, object?> Domain(Dictionary<string, object?> contrast, string type) =>
        ((object?[])contrast["domains"]!).Cast<Dictionary<string, object?>>().Single(d => (string)d["type"]! == type);

    private static string[] Names(object? list) =>
        [.. ((object?[])list!).Cast<Dictionary<string, object?>>().Select(e => (string)e["name"]!)];

    private static TasteResult Profile(string id, string label, params (string Type, object?[] Distinctive)[] domains) => new()
    {
        Id = id, Op = "profile", Label = label, Ok = true,
        Entities = [.. domains.SelectMany(d => d.Distinctive)],
        Profile = new()
        {
            ["domains"] = domains.Select(d => (object?)new Dictionary<string, object?>
            {
                ["type"] = d.Type, ["distinctive"] = d.Distinctive, ["shared"] = Array.Empty<object?>(),
            }).ToArray(),
        },
    };

    private static (string, object?[]) Movies(params (string Id, string Name, int Rank)[] films) =>
        ("urn:entity:movie", [.. films.Select(f => Entity(f.Id, f.Name, f.Rank))]);

    private static (string, object?[]) Movies(params (string Id, string Name, int Rank, string[] Tags)[] films) =>
        ("urn:entity:movie", [.. films.Select(f => Entity(f.Id, f.Name, f.Rank, f.Tags))]);

    private static object? Entity(string id, string name, int rank, params string[] tags) => new Dictionary<string, object?>
    {
        ["id"] = id, ["name"] = name, ["type"] = "urn:entity:movie", ["types"] = new[] { "urn:entity:movie", "urn:entity" },
        ["audienceRank"] = rank,
        ["tags"] = tags.Select(t => (object?)new Dictionary<string, object?> { ["name"] = t, ["type"] = "urn:tag:plot:qloo" }).ToArray(),
    };
    #endregion
}
