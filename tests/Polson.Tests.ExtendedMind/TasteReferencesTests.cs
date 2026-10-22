namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using global::Polson.ExtendedMind.Taste;
using global::Polson.Tests;
using Xunit;

/// <summary>
/// <c>Taste.checkReferences</c>: the titles and tags a direction's Qloo rows cite, checked against the answers the session
/// received. The answers are built here; no Qloo response is a fixture (design doc §6.3).
/// </summary>
public class TasteReferencesTests : TestsRuntime, IDisposable
{
    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-refs-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public TasteReferencesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>t1, a profile with two distinctive films and one shared; t2, a describe asked about one of them.</summary>
    private static TasteResult[] Session() =>
    [
        new()
        {
            Id = "t1", Op = "profile", Label = "audience A", Ok = true,
            Entities = [Entity("e-1", "The Long Quiet"), Entity("e-2", "Amélie's Garden")],
            Profile = new()
            {
                ["domains"] = new object?[]
                {
                    new Dictionary<string, object?> { ["type"] = "urn:entity:movie", ["shared"] = new[] { Entity("e-9", "Everyone's Film") } },
                },
            },
        },
        new()
        {
            Id = "t2", Op = "describe", Label = "A's films", Ok = true,
            Request = "/v2/insights?filter.type=urn:tag&signal.interests.entities=e-1",
            Tags = [Tag("Dreamlike Visuals"), Tag("Flowing")],
        },
        new() { Id = "t3", Op = "insights", Label = "other", Ok = true, Entities = [Entity("e-5", "Somebody Else's Film")] },
        new() { Id = "t4", Op = "insights", Label = "broken", Ok = false, Error = "Qloo answered 429." },
    ];

    private static string Table(params string[] rows) =>
        "## Choices\n\n| Choice | Evidence | Kind |\n| :-- | :-- | :-- |\n" + string.Join("\n", rows) + "\n";

    [Fact]
    public void TestEveryCitedTitleAndTagReturnedByItsQueryPasses()
    {
        var check = TasteReferences.Check(Table(
            "| Slow holds | t1's slow titles: *The Long Quiet*, *Amelie's Garden (2019)*, *Everyone's Film* | Qloo (t1) |",
            "| Soft grade | t2 over *Long Quiet*: **\"Dreamlike Visuals\"** (item 1), \"Flowing\" (t2 item 2) | Qloo |"), Session());

        Assert.True((bool)check["passed"]!, (string)check["summary"]!);
        Assert.Equal(6, check["checked"]);   // three titles returned, one asked about by the describe, two tags
        Assert.Empty((string[])check["unverified"]!);
    }

    [Fact]
    public void TestATitleNoQueryReturnedIsUnverified()
    {
        var check = TasteReferences.Check(Table("| Palette | *The Long Quiet* and *A Film I Remember* | Qloo (t1) |"), Session());

        Assert.False((bool)check["passed"]!);
        Assert.Equal(["*A Film I Remember*"], (string[])check["unverified"]!);
        var titles = Titles(check);
        Assert.Equal([true, false], titles.Select(t => (bool)t["found"]!));
        Assert.Equal("t1", titles[0]["in"]);
    }

    /// <summary>The row cites t1 for a title only t3 returned: real evidence, cited to the wrong query.</summary>
    [Fact]
    public void TestATitleAnotherHandleReturnedSaysWhich()
    {
        var check = TasteReferences.Check(Table("| Palette | *Somebody Else's Film* | Qloo (t1) |"), Session());

        Assert.Equal(["*Somebody Else's Film* (returned by t3, not by t1)"], (string[])check["unverified"]!);
        Assert.Equal("t3", Titles(check)[0]["elsewhere"]);
    }

    [Fact]
    public void TestATagTheCitedQueryDidNotReturnIsUnverified()
    {
        var check = TasteReferences.Check(Table("| Grade | **\"Dreamlike Visuals\"**, **\"Neon Noir\"** | Qloo (t2) |"), Session());

        Assert.Equal(["\"Neon Noir\""], (string[])check["unverified"]!);
    }

    [Fact]
    public void TestAMissingFailedOrAbsentHandleIsAProblem()
    {
        var check = TasteReferences.Check(Table(
            "| One | *The Long Quiet* | Qloo (t9) |",
            "| Two | *The Long Quiet* | Qloo (t4) |",
            "| Three | *The Long Quiet* | Qloo |"), Session());

        var problems = ((object?[])check["rows"]!).Cast<Dictionary<string, object?>>().Select(r => ((string[])r["problems"]!).Single()).ToArray();
        Assert.Contains("t9 is not an answer of this session", problems[0]);
        Assert.Contains("t4 failed: Qloo answered 429.", problems[1]);
        Assert.Contains("cites no Taste handle", problems[2]);
        Assert.False((bool)check["passed"]!);
    }

    /// <summary>A section's first paragraph names the profile its rows rest on, and rows without a handle use it.</summary>
    [Fact]
    public void TestARowWithoutAHandleUsesItsSectionsLead()
    {
        var text = "## Audience A\n\nEverything here rests on **t1**, the profile of audience A.\n\n"
            + "| Choice | Evidence | Kind |\n| :-- | :-- | :-- |\n| Holds | *The Long Quiet* | Qloo |\n";

        var check = TasteReferences.Check(text, Session());

        Assert.True((bool)check["passed"]!, (string)check["summary"]!);
        Assert.Equal(["t1"], (string[])((Dictionary<string, object?>)((object?[])check["rows"]!)[0]!)["handles"]!);
    }

    [Fact]
    public void TestOnlyQlooRowsOfKindTablesAreReadAndItalicQuotesAreNotTitles()
    {
        var text = "| Choice | Evidence | Kind |\n| :-- | :-- | :-- |\n"
            + "| Headline | all capitals slow reading (*Typographic Design*, p. 79) | Research |\n"
            + "| Holds | *\"no measurable difference\"* between *The Long Quiet* and the rest | Qloo (t1) |\n\n"
            + "| Film | Note |\n| :-- | :-- |\n| *Not A Kind Table* | Qloo |\n";

        var check = TasteReferences.Check(text, Session());

        Assert.Single((object?[])check["rows"]!);
        Assert.Equal(["The Long Quiet"], Titles(check).Select(t => (string)t["title"]!));
        Assert.True((bool)check["passed"]!);
    }

    [Fact]
    public void TestNoQlooRowsDoesNotPass()
    {
        var check = TasteReferences.Check("# Direction\n\nNothing tabled yet.\n", Session());

        Assert.False((bool)check["passed"]!);
        Assert.StartsWith("No Qloo rows found", (string)check["summary"]!);
    }

    [Theory]
    [InlineData("The Long Quiet", "long quiet")]
    [InlineData("Amélie’s Garden (2019)", "amelie s garden")]
    [InlineData("  A  Film,  Again ", "film again")]
    public void TestNamesCompareWithoutCaseAccentsArticlesOrYears(string name, string key) =>
        Assert.Equal(key, TasteReferences.Key(name));

    [Fact]
    public void TestCheckReferencesReadsDirectionInsideTheProjectAndRecordsOnlyVerdicts()
    {
        File.WriteAllText(Path.Combine(root, "direction.md"), Table("| Holds | *The Long Quiet* | Qloo (t1) |"));
        var registry = new TasteRegistry();
        foreach (var r in Session()) registry.Add(_ => r);
        var recorded = new List<(string Type, IReadOnlyDictionary<string, object?> Fields)>();
        var taste = new TasteToolkit(registry, root, (type, fields) => recorded.Add((type, fields)));

        var check = taste.CheckReferences();

        Assert.True((bool)check["passed"]!);
        var (type, fields) = Assert.Single(recorded);
        Assert.Equal("taste.check", type);
        Assert.Equal(["path", "passed", "checked", "unverified", "rows"], fields.Keys);
        Assert.Throws<ArgumentException>(() => taste.CheckReferences("../outside.md"));
        Assert.Throws<FileNotFoundException>(() => taste.CheckReferences("missing.md"));
    }

    private static Dictionary<string, object?>[] Titles(Dictionary<string, object?> check) =>
        [.. ((object?[])check["rows"]!).Cast<Dictionary<string, object?>>()
            .SelectMany(r => ((object?[])r["titles"]!).Cast<Dictionary<string, object?>>())];

    private static object? Entity(string id, string name) =>
        new Dictionary<string, object?> { ["id"] = id, ["name"] = name, ["type"] = "urn:entity:movie" };

    private static object? Tag(string name) =>
        new Dictionary<string, object?> { ["name"] = name, ["type"] = "urn:tag:style:qloo" };
    #endregion
}
