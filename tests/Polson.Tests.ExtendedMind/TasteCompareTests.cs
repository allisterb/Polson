namespace Polson.Tests.ExtendedMind;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using global::Polson.ExtendedMind.Taste;
using global::Polson.Tests;
using Xunit;

/// <summary>
/// The compact form of Qloo's compare answer. Its <c>tags</c> come one row per tag per pairing of signal entities, so
/// seven artists against seven were 404 rows for 74 tags and 358 KB, which the host refused to deliver (poster5,
/// 2026-10-24). Every answer here is <b>synthetic</b>, in Qloo's shape: its terms forbid committing its responses.
/// </summary>
public class TasteCompareTests : TestsRuntime
{
    #region Methods
    [Fact]
    public void ASharedTagIsOneRowWithHowManyEntitiesOnEachSideCarryIt()
    {
        var rows = new[]
        {
            Row("urn:tag:genre:music:sample_jazz", "Sample Jazz", "urn:tag:genre:music", ["A1", "A2"], ["B1"], 0.73),
            Row("urn:tag:genre:music:sample_jazz", "Sample Jazz", "urn:tag:genre:music", ["A2"], ["B2"], 0.51),
            Row("urn:tag:genre:music:sample_jazz", "Sample Jazz", "urn:tag:genre:music", ["A3"], ["B1"], 0.50),
            Row("urn:tag:style:qloo:sample_moody", "Sample Moody", "urn:tag:style:qloo", ["A1"], ["B3"], 0.72),
        };

        var summary = Summarise(Results(rows, [Side("urn:tag:style:qloo:sample_moody", "Sample Moody", 1)], []), 10);

        Assert.Equal(4, (int)summary["rows"]!);
        Assert.Equal(2, (int)summary["tags"]!);
        var jazz = Assert.IsType<Dictionary<string, object?>>(((object?[])summary["shared"]!)[0]);
        Assert.Equal("Sample Jazz", jazz["name"]);
        Assert.Equal("genre:music", jazz["kind"]);
        Assert.Equal(3, jazz["a"]);                     // A1, A2, A3 — each counted once across its three rows
        Assert.Equal(2, jazz["b"]);                     // B1, B2
        Assert.Equal("style", ((Dictionary<string, object?>)((object?[])summary["shared"]!)[1]!)["kind"]);
        Assert.Equal(new object?[] { 0.5, 0.73 }, (object?[])summary["scoreRange"]!);
    }

    [Fact]
    public void WhatSeparatesTheSetsIsTheDifferenceInHowManyOnEachSideCarryATag()
    {
        // "Hip - Hop" in poster5's second answer: in the shared rows, on all five of one side and one of the other.
        var rows = new[] { Row("T-H", "Leans A", "urn:tag:genre:music", ["A1", "A2", "A3", "A4", "A5"], ["B1"], 0.73) };
        var a = new[] { Side("T-1", "Even", 3), Side("T-2", "Only A", 2), Side("T-3", "Mostly A", 4), Side("T-5", "Once A", 1) };
        var b = new[] { Side("T-1", "Even", 3), Side("T-3", "Mostly A", 1), Side("T-4", "Only B", 2) };

        var summary = TasteCompare.Summary(Results(rows, a, b), 10, "t9", 5, 5);

        Assert.Equal(["Leans A", "Mostly A", "Only A"], Names(summary["tagsA"]));   // 4/5, 3/5, 2/5 more of side A
        Assert.Equal(["Only B"], Names(summary["tagsB"]));
        Assert.DoesNotContain("Once A", Names(summary["tagsA"]));                   // one entity is a lead, not a lean
        Assert.DoesNotContain("Even", Names(summary["tagsA"]).Concat(Names(summary["tagsB"])));
        Assert.Equal(["Even", "Leans A", "Mostly A"], Names(summary["shared"]));
        var leans = (Dictionary<string, object?>)((object?[])summary["tagsA"]!)[0]!;
        Assert.Equal((5, 1), ((int)leans["a"]!, (int)leans["b"]!));
        Assert.Equal(5, ((Dictionary<string, object?>)summary["entities"]!)["a"]);
        Assert.Contains("SEPARATES", (string)summary["note"]!);
    }

    [Fact]
    public void WhereQloosCountsDisagreeTheLargerIsTaken()
    {
        // poster5: "Electronic" listed for 2 of side A in `a`, and carried by 4 of side A in the shared rows.
        var rows = new[] { Row("T-E", "Electronic", "urn:tag:genre:music", ["A1", "A2", "A3", "A4"], ["B1"], 0.73) };

        var summary = Summarise(Results(rows, [Side("T-E", "Electronic", 2)], []), 10);

        Assert.Equal(4, ((Dictionary<string, object?>)((object?[])summary["shared"]!)[0]!)["a"]);
    }

    [Fact]
    public void AnAnswerAtPoster5sScaleComesDownToAFewKilobytesAndSaysWhatItLeftOut()
    {
        var (raw, rows, tags) = Poster5Scale();

        var summary = Summarise(raw, 10);
        var compact = JsonSerializer.Serialize(summary);

        Assert.Equal(rows, (int)summary["rows"]!);
        Assert.True((int)summary["tags"]! >= tags);
        Assert.Equal(10, ((object?[])summary["shared"]!).Length);
        Assert.Equal(10, ((object?[])summary["tagsA"]!).Length);
        Assert.True((int)((Dictionary<string, object?>)summary["omitted"]!)["shared"]! >= tags - 10);
        Assert.Equal(10, ((object?[])summary["shared"]!).Cast<Dictionary<string, object?>>().Select(t => t["id"]).Distinct().Count());
        Assert.Contains("Lists stop at 10", (string)summary["note"]!);
        Assert.Contains("Taste.get('t9').comparison", (string)summary["note"]!);
        Assert.True(raw.GetRawText().Length > 150_000, $"the fixture should be at the scale that failed: {raw.GetRawText().Length:N0}");
        Assert.True(compact.Length < 6_000, $"{compact.Length:N0} characters from {raw.GetRawText().Length:N0}");
    }

    [Fact]
    public void AnUnfamiliarShapeIsDescribedNeverPasted()
    {
        var raw = JsonDocument.Parse("""{ "entities": [ {"name": "x"}, {"name": "y"} ], "duration": 4 }""").RootElement;

        var summary = TasteCompare.Summary(raw, 10, "t2");

        Assert.Equal("2 items", ((Dictionary<string, object?>)summary["shape"]!)["entities"]);
        Assert.Contains("Taste.get('t2').comparison", (string)summary["note"]!);
        Assert.DoesNotContain("\"x\"", JsonSerializer.Serialize(summary));
    }

    private static Dictionary<string, object?> Summarise(JsonElement raw, int take) => TasteCompare.Summary(raw, take, "t9");

    private static string[] Names(object? list) =>
        [.. ((object?[])list!).Cast<Dictionary<string, object?>>().Select(t => (string)t["name"]!)];

    /// <summary>
    /// Seven entities a side and 74 shared tags, each carried by a few entities on each side and sent once per pairing,
    /// with the signal entities written out in every row as Qloo writes them.
    /// </summary>
    internal static (JsonElement Raw, int Rows, int Tags) Poster5Scale()
    {
        const int Tags = 74;
        var rows = new List<object>();
        for (var i = 0; i < Tags; i++)
        {
            var (nA, nB) = (i % 5 + 1, i % 4 + 1);
            for (var x = 0; x < nA; x++)
                for (var y = 0; y < nB; y++)
                    if ((x + y + i) % 3 != 0 || (x == 0 && y == 0))
                        rows.Add(Row($"urn:tag:genre:music:sample_{i}", $"Sample Tag {i}", "urn:tag:genre:music",
                            [.. Enumerable.Range(0, x + 1).Select(k => $"A{(i + k) % 7}")], [$"B{(i + y) % 7}"], 0.5 + i % 23 / 100.0));
        }
        var a = Enumerable.Range(0, 60).Select(i => Side($"urn:tag:style:qloo:a_{i % 45}", $"Side Tag {i % 45}", i % 5 + 1)).ToArray();
        var b = Enumerable.Range(0, 70).Select(i => Side($"urn:tag:style:qloo:a_{i + 30}", $"Side Tag {i + 30}", i % 6 + 1)).ToArray();
        return (Results([.. rows], a, b), rows.Count, Tags);
    }

    internal static JsonElement Results(object[] tags, object[] a, object[] b) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["tags"] = tags, ["a"] = a, ["b"] = b, ["matchEntities"] = new object[0] });

    internal static object Row(string id, string name, string subtype, string[] a, string[] b, double score) => new Dictionary<string, object>
    {
        ["tag_id"] = id, ["name"] = name, ["type"] = "urn:tag", ["subtype"] = subtype, ["entity_id"] = "E-" + id.Length,
        ["popularity"] = 0.9,
        ["query"] = new Dictionary<string, object>
        {
            ["score"] = score,
            ["a.signal.interests.entities"] = a.Select(Signal).ToArray(),
            ["b.signal.interests.entities"] = b.Select(Signal).ToArray(),
        },
    };

    internal static object Side(string id, string name, int count) => new Dictionary<string, object>
    {
        ["tag_id"] = id, ["name"] = name, ["type"] = "urn:tag", ["subtype"] = "urn:tag:style:qloo", ["popularity"] = 0.9,
        ["query"] = new Dictionary<string, object> { ["count"] = count.ToString() },
    };

    private static object Signal(string id) => new Dictionary<string, object>
    {
        ["entity_id"] = $"{id}-0000-4000-8000-000000000000", ["name"] = $"Sample Artist {id}", ["subtype"] = "urn:entity:artist",
        ["disambiguation"] = $"Sample Artist {id}",
    };
    #endregion
}
