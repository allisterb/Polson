namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Which tag kinds a <c>describe</c> asks Qloo for. Not exposed to the sandbox.
/// </summary>
/// <remarks>
/// <para>
/// Tag insights over a set of films span <b>every</b> domain Qloo tags. Measured over trailer5's six films
/// (2026-10-18), the unfiltered top tags were "JCB" (a credit card), "Five Star" (a hotel rating), "Identifies as
/// women-owned" and "Cultural landmark", all at affinity 1.0, ahead of anything about the films; over the other six,
/// "Parties", "Park" and "Professional". Three to six of twenty-five — too few for a "most tags are off-domain" warning
/// to fire, and exactly the ones an agent reads first. So the default is a filter, not a warning.
/// </para>
/// <para>
/// <see cref="FilmKinds"/> are kinds Qloo attaches to films and shows themselves, counted over the 238 films and 199
/// shows in the local cache: style, theme, archetype, plot, subgenre and genre all occur on both. Left out are the
/// plot keywords (<c>keyword:media</c>, <c>keyword:qloo</c>: "Butcher", "Medicine"), who watches (<c>audience</c>,
/// <c>interests</c>) and <c>character</c>. <c>aesthetic_property</c> and <c>motif</c> are on none of them: asked for
/// over films they return fashion and product words ("Fluid Silhouettes", "beachy").
/// </para>
/// </remarks>
public static class TasteDescribe
{
    #region Properties
    /// <summary>The tag kinds that describe a film or show, as Qloo URNs.</summary>
    public static string[] FilmKinds { get; } =
        [.. new[] { "style", "theme", "archetype", "plot", "subgenre", "genre" }.Select(k => $"urn:tag:{k}:qloo")];
    #endregion

    #region Methods
    /// <summary>
    /// The kinds to ask for, and a note for the reply when the choice was not the caller's. An empty
    /// <c>Kinds</c> asks for the overview across every kind.
    /// </summary>
    /// <param name="type">The <c>type</c> as given: kinds, <c>'film'</c> for <see cref="FilmKinds"/>, or <c>'all'</c> for the overview.</param>
    /// <param name="parsed">The kinds <paramref name="type"/> names, as URNs.</param>
    /// <param name="entityTypes">Each signal entity's Qloo type, or null where this session never saw it.</param>
    public static (string[] Kinds, string? Note) Kinds(string? type, IReadOnlyList<string> parsed, IReadOnlyList<string?> entityTypes)
    {
        switch (type?.Trim().ToLowerInvariant())
        {
            case "all":
                return ([], null);
            case "film" or "films" or "movie" or "tv_show":
                return (FilmKinds, null);
        }
        if (parsed.Count > 0) return ([.. parsed], null);

        if (entityTypes.Count > 0 && entityTypes.All(IsScreen))
        {
            return (FilmKinds, "No type was given and every entity is a film or show, so only the kinds that describe one "
                + "were asked for: style, theme, archetype, plot, subgenre, genre. Unfiltered, Qloo's top tags over films are "
                + "places' and brands' — a credit card, a hotel rating. type 'all' asks for that overview anyway.");
        }
        return ([], "An overview across every kind Qloo tags, places and brands included: over films its top tags can be "
            + "a credit card or a hotel rating. Name kinds in type, or 'film' for the ones that describe a film.");
    }

    /// <summary>A film or a show.</summary>
    public static bool IsScreen(string? type) => type is "urn:entity:movie" or "urn:entity:tv_show";
    #endregion
}
