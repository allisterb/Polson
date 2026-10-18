namespace Polson.Tests.ExtendedMind;

using System.Collections.Generic;

using global::Polson.ExtendedMind.Taste;
using global::Polson.Tests;
using Xunit;

/// <summary>
/// Which tag kinds <c>describe</c> asks for. Unfiltered, Qloo's top tags over films were a credit card and a hotel
/// rating (trailer5, 2026-10-18), so a set of films defaults to the kinds that describe one. No live calls: entities
/// here are invented, in the shape <see cref="TasteObjects.Entity"/> builds.
/// </summary>
public class TasteDescribeTests : TestsRuntime
{
    #region Methods
    [Fact]
    public void FilmsAndShowsWithNoTypeGetTheFilmKindsAndSaySo()
    {
        var (kinds, note) = TasteDescribe.Kinds(null, [], ["urn:entity:movie", "urn:entity:tv_show", "urn:entity:movie"]);

        Assert.Equal(TasteDescribe.FilmKinds, kinds);
        Assert.Contains("urn:tag:style:qloo", kinds);
        Assert.DoesNotContain(kinds, k => k.EndsWith(":place") || k.EndsWith(":brand"));
        Assert.DoesNotContain("urn:tag:aesthetic_property:qloo", kinds);    // on none of 238 films; fashion words over them
        Assert.Contains("type 'all'", note);
    }

    [Theory]
    [InlineData("urn:entity:artist")]
    [InlineData(null)]                                                      // never seen this session
    public void AnythingButFilmsGetsTheOverviewWithAWarningNote(string? other)
    {
        var (kinds, note) = TasteDescribe.Kinds(null, [], ["urn:entity:movie", other]);

        Assert.Empty(kinds);
        Assert.Contains("places and brands", note);
        Assert.Contains("'film'", note);
    }

    [Fact]
    public void NamedKindsAreTakenAsAskedWithoutANote()
    {
        var (kinds, note) = TasteDescribe.Kinds("theme", ["urn:tag:theme:qloo"], ["urn:entity:movie"]);

        Assert.Equal(["urn:tag:theme:qloo"], kinds);
        Assert.Null(note);
    }

    [Theory]
    [InlineData("all", 0)]
    [InlineData(" ALL ", 0)]
    [InlineData("film", 6)]
    [InlineData("movie", 6)]                                                // insights' word, meaning the same here
    public void AllAndFilmAreWordsNotKinds(string type, int expected)
    {
        // TasteTagTypes would read these as urn:tag:all:qloo and urn:tag:film:qloo, kinds Qloo does not have.
        var (kinds, note) = TasteDescribe.Kinds(type, [$"urn:tag:{type.Trim().ToLowerInvariant()}:qloo"], ["urn:entity:movie"]);

        Assert.Equal(expected, kinds.Length);
        Assert.Null(note);
    }

    [Fact]
    public void TheRegistryKnowsTypesFromAnswersAndFromAProfilesSharedLists()
    {
        var registry = new TasteRegistry();
        registry.Add(id => new TasteResult
        {
            Id = id, Op = "profile",
            Entities = [Entity("D-1", "urn:entity:movie")],
            Profile = new()
            {
                ["domains"] = new object?[]
                {
                    new Dictionary<string, object?> { ["shared"] = new object?[] { Entity("S-1", "urn:entity:tv_show") } },
                },
            },
        });
        registry.Add(id => new TasteResult { Id = id, Op = "insights", Entities = [Entity("A-1", "urn:entity:artist")] });

        Assert.Equal("urn:entity:movie", registry.TypeOf("D-1"));
        Assert.Equal("urn:entity:movie", registry.TypeOf("d-1"));          // Qloo ids are GUIDs; case is not identity
        Assert.Equal("urn:entity:tv_show", registry.TypeOf("S-1"));
        Assert.Equal("urn:entity:artist", registry.TypeOf("A-1"));
        Assert.Null(registry.TypeOf("never-seen"));
    }

    private static Dictionary<string, object?> Entity(string id, string type) =>
        new() { ["id"] = id, ["name"] = id, ["type"] = type, ["types"] = new[] { type } };
    #endregion
}
