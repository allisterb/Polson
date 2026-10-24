namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Polson.ExtendedMind.ParallelSearch;

/// <summary>
/// One answer from Qloo, as a script reads it: what was asked, what came back, and what it rests on.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a plain read; nothing here touches the network. Answers are fetched by the <c>Taste</c> MCP
/// tool — the agent has to read them to reason, so they arrive in its context — and kept here so a later script
/// can cite the evidence a mark rests on.
/// </para>
/// <para>
/// Values are plain dictionaries, lists and primitives, so <c>result.entities[0].name</c> works in JS. Lists are
/// arrays rather than <c>IReadOnlyList</c>: Jint caches a member's accessor per runtime type, and a property
/// whose type alternated threw (the <c>RequisitionVerdict.Triggers</c> lesson).
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members are PascalCase here; Jint resolves the JS camelCase spelling onto
/// them, so <c>result.fromCache</c> reaches <see cref="FromCache"/>.
/// </para>
/// </remarks>
public sealed class TasteResult
{
    #region Properties
    /// <summary>This session's handle for the answer: <c>t1</c>, <c>t2</c>…</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The operation: <c>resolve</c>, <c>insights</c>, <c>describe</c>, <c>demographics</c>, <c>compare</c>, <c>audiences</c>.</summary>
    public string Op { get; init; } = string.Empty;

    /// <summary>What it was for, in the agent's words.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The Qloo endpoint and canonical query, without the key. The provenance line.</summary>
    public string Request { get; init; } = string.Empty;

    public bool Ok { get; init; }

    public string? Error { get; init; }

    public string? Remedy { get; init; }

    public bool FromCache { get; init; }

    /// <summary>When Qloo gave a cached answer, ISO 8601; null for a fresh one.</summary>
    public string? CachedUtc { get; init; }

    public string? RequestId { get; init; }

    public string[] Warnings { get; init; } = [];

    /// <summary>Where Qloo placed the location, as <c>{ id, name, disambiguation }</c>, or null.</summary>
    public Dictionary<string, object?>? Locality { get; init; }

    /// <summary>Entities, each <c>{ id, name, type, types, disambiguation, popularity, affinity, tags, explainability, properties }</c>.</summary>
    public object?[] Entities { get; init; } = [];

    /// <summary>Tags, each <c>{ id, name, type, popularity, affinity }</c>.</summary>
    public object?[] Tags { get; init; } = [];

    /// <summary>Audiences, each <c>{ id, name, type, parents }</c>.</summary>
    public object?[] Audiences { get; init; } = [];

    /// <summary>For <c>demographics</c>: one block per signal, age bands and gender as affinities, as Qloo sent them.</summary>
    public object?[] Demographics { get; init; } = [];

    /// <summary>For <c>compare</c>: Qloo's result as sent.</summary>
    public object? Comparison { get; init; }

    /// <summary>
    /// For <c>profile</c>: <c>{ audience, locality, floor, domains, placeAesthetics, requests }</c>. Each domain is
    /// <c>{ type, distinctive, shared, audienceRequest, baselineRequest }</c>: <c>distinctive</c> is what this audience
    /// loves that its place's baseline does not list — the evidence a direction cites.
    /// </summary>
    public Dictionary<string, object?>? Profile { get; init; }

    public string RetrievedUtc { get; init; } = DateTime.UtcNow.ToString("O");
    #endregion

    #region Methods
    /// <summary>The caption line for this answer: what was asked of Qloo, and when.</summary>
    public string Cite() =>
        $"Qloo Insights API, {Request} ({(FromCache ? "cached, retrieved " + CachedUtc : "retrieved " + RetrievedUtc)}"
        + (RequestId is null ? ")" : $", request {RequestId})");

    /// <summary>
    /// How the films and shows in this answer pace themselves, in Qloo's own tags — for a <c>profile</c>, the audience's
    /// distinctive ones: <c>{ fast, slow, both, untagged, n, fastShare, fastTitles, slowTitles, vocabulary }</c>. See
    /// <see cref="TastePace"/>.
    /// </summary>
    public Dictionary<string, object?> Pace() => TastePace.Count(Entities);

    public override string ToString() => $"{Id} [{Op}] {Label} ({(Ok ? "ok" : "failed")})";

    #endregion
}

/// <summary>
/// The Qloo answers fetched during one session, and the session's allowance. Held on the session so an answer
/// fetched by one tool call is readable by a later script.
/// </summary>
public sealed class TasteRegistry
{
    #region Constructors
    public TasteRegistry(QlooBudget? budget = null) => Budget = budget ?? new QlooBudget(0);
    #endregion

    #region Properties
    public QlooBudget Budget { get; }

    public TasteResult[] All
    {
        get { lock (results) return [.. results]; }
    }

    /// <summary>Pictures fetched from Qloo this session; cache hits are not counted.</summary>
    public int ImagesFetched { get; private set; }

    /// <summary>Qloo pictures one session may fetch: visual research for several audiences, with room to redo it.</summary>
    public const int ImageAllowance = 240;
    #endregion

    #region Methods
    /// <summary>Records a response under the next id and returns it.</summary>
    public TasteResult Add(string op, string label, QlooResponse response)
    {
        lock (results)
        {
            var result = TasteObjects.Result($"t{results.Count + 1}", op, label, response);
            results.Add(result);
            return result;
        }
    }

    /// <summary>Records a result built elsewhere — a profile — under the next id, which the builder is given.</summary>
    public TasteResult Add(Func<string, TasteResult> build)
    {
        lock (results)
        {
            var result = build($"t{results.Count + 1}");
            results.Add(result);
            return result;
        }
    }

    public TasteResult? Get(string id)
    {
        lock (results) return results.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The Qloo type of an entity this session has seen — in any answer's entities, or a profile's shared lists —
    /// or null.
    /// </summary>
    public string? TypeOf(string entityId) =>
        All.SelectMany(r => r.Entities.Concat(SharedOf(r)))
            .OfType<Dictionary<string, object?>>()
            .Where(e => string.Equals(e.GetValueOrDefault("id") as string, entityId, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.GetValueOrDefault("type") as string)
            .FirstOrDefault(t => t is not null);

    /// <summary>
    /// An entity as a Qloo answer this session received delivered it, with that answer's id; null when no answer
    /// named it. The first answer to carry its properties wins.
    /// </summary>
    public (Dictionary<string, object?> Entity, string Answer)? Entity(string entityId) =>
        All.SelectMany(r => r.Entities.Concat(SharedOf(r)).OfType<Dictionary<string, object?>>().Select(e => (Entity: e, Answer: r.Id)))
            .Where(p => string.Equals(p.Entity.GetValueOrDefault("id") as string, entityId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Entity.GetValueOrDefault("properties") is null ? 1 : 0)
            .Select(p => ((Dictionary<string, object?>, string)?)(p.Entity, p.Answer))
            .FirstOrDefault();

    /// <summary>Takes <paramref name="count"/> pictures from the session's allowance; false, and nothing taken, when it would overrun.</summary>
    public bool TakeImages(int count)
    {
        lock (results)
        {
            if (ImagesFetched + count > ImageAllowance) return false;
            ImagesFetched += count;
            return true;
        }
    }
    #endregion

    #region Private methods
    private static IEnumerable<object?> SharedOf(TasteResult r) =>
        r.Profile?.GetValueOrDefault("domains") is object?[] domains
            ? domains.OfType<Dictionary<string, object?>>().SelectMany(d => d.GetValueOrDefault("shared") as object?[] ?? [])
            : [];
    #endregion

    #region Fields
    private readonly List<TasteResult> results = [];
    #endregion
}

/// <summary>
/// The <c>Taste</c> global: Qloo answers already fetched this session, for a script to read and cite. Fetching
/// is the <c>Taste</c> MCP tool's job.
/// </summary>
/// <remarks>
/// Exposed to the JavaScript sandbox. Members are PascalCase here; Jint resolves the JS camelCase spelling onto
/// them, so <c>Taste.latest</c> reaches <see cref="Latest"/>.
/// </remarks>
public sealed class TasteToolkit
{
    #region Constructors
    /// <param name="registry">This session's answers.</param>
    /// <param name="projectRoot">Where <see cref="CheckReferences"/> reads from; nothing outside it is read.</param>
    /// <param name="record">Records a run event — <c>taste.check</c> — when given.</param>
    /// <param name="images">Where <see cref="Images"/> fetches Qloo's pictures; null and every picture is refused as not configured.</param>
    public TasteToolkit(TasteRegistry registry, string? projectRoot = null,
        Action<string, IReadOnlyDictionary<string, object?>>? record = null, QlooImageSource? images = null)
    {
        this.registry = registry;
        this.projectRoot = projectRoot;
        this.record = record;
        this.images = images;
    }
    #endregion

    #region Properties
    public TasteResult[] Results => registry.All;

    public int Count => registry.All.Length;

    public TasteResult? Latest => registry.All is { Length: > 0 } all ? all[^1] : null;

    public QlooBudget Budget => registry.Budget;
    #endregion

    #region Methods
    /// <summary>The answer with this id (<c>'t3'</c>), or null.</summary>
    public TasteResult? Get(string id) => registry.Get(id);

    /// <summary>
    /// The latest answer labelled exactly <paramref name="text"/>; failing that, the latest whose label contains it; or null.
    /// Case-insensitive.
    /// </summary>
    /// <remarks>
    /// <b>The exact label wins.</b> Containment alone let a later 'audience A places' shadow the profile labelled
    /// 'audience A', and <c>pace()</c> over the places answer read 0 fast, 0 slow for every city: a run reported "no
    /// measurable difference" from the wrong evidence (productad1, 2026-10-19).
    /// </remarks>
    public TasteResult? Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var wanted = text.Trim();
        return registry.All.LastOrDefault(r => string.Equals(r.Label.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            ?? registry.All.LastOrDefault(r => r.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Two answers' pace side by side — two audiences' profiles — with Fisher's exact test on their fast and slow counts:
    /// <c>{ a, b, p, differ, summary }</c>. "No measurable difference" is an answer to report, not a reason to invent one.
    /// </summary>
    public Dictionary<string, object?> ComparePace(TasteResult a, TasteResult b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return TastePace.Compare(a.Pace(), b.Pace(), Named(a), Named(b));
    }

    /// <summary>
    /// Every audience's pace and every pair compared, adjusted for how many pairs there are:
    /// <c>{ audiences, pairs, differ, summary }</c>. For three or more audiences; two can use either call.
    /// </summary>
    public Dictionary<string, object?> ComparePaceAll(TasteResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Length < 2 || results.Any(r => r is null))
            throw new ArgumentException("Taste.comparePaceAll needs two or more profiles, e.g. [t1, t2, t3].", nameof(results));
        return TastePace.CompareAll([.. results.Select(r => (r.Pace(), Named(r)))]);
    }

    /// <summary>
    /// What separates two audiences' profiles — shared titles, each one's own, the tags that lean each way and their pace
    /// — with no further Qloo call: <c>{ a, b, domains, pace, summary }</c>. See <see cref="TasteContrast"/>. Recorded as
    /// <c>taste.contrast</c>, which the studio's Contrast view draws.
    /// </summary>
    public Dictionary<string, object?> Contrast(TasteResult a, TasteResult b)
    {
        var contrast = TasteContrast.Compare(a, b);
        record?.Invoke("taste.contrast", contrast);
        return contrast;
    }

    /// <summary>
    /// Every title and tag a <c>direction.md</c>'s Qloo rows cite, checked against the answers this session received:
    /// <c>{ passed, rows, unverified, checked, summary }</c>. See <see cref="TasteReferences"/>.
    /// </summary>
    /// <param name="path">Project-relative; <c>direction.md</c> by default.</param>
    public Dictionary<string, object?> CheckReferences(string? path = null)
    {
        var name = string.IsNullOrWhiteSpace(path) ? "direction.md" : path;
        var full = ProjectPath.Resolve(projectRoot, name, "path", "Read");
        if (!File.Exists(full))
            throw new FileNotFoundException($"Taste.checkReferences: '{name}' is not in the project. Write direction.md first.", full);

        var check = TasteReferences.Check(File.ReadAllText(full), registry.All);
        check["path"] = name;

        // The agent's own words and the verdicts only: no Qloo response reaches the run record.
        record?.Invoke("taste.check", new Dictionary<string, object?>
        {
            ["path"] = name, ["passed"] = check["passed"], ["checked"] = check["checked"],
            ["unverified"] = check["unverified"], ["unread"] = check["unread"], ["rows"] = check["rows"],
        });
        return check;
    }

    /// <summary>
    /// Qloo's pictures of entities this session's answers named — a brand's logo, a film's poster — each measured
    /// for colour on arrival: <c>await Taste.images(t3, { take: 24, type: 'brand' })</c>.
    /// </summary>
    /// <param name="source">An answer, or an array of its entities, entity ids or earlier pictures.</param>
    /// <param name="options"><c>{ take?: number (default 24, at most 48), type?: 'brand' | 'film' | 'show' | 'artist' | 'place' | a urn }</c>.</param>
    /// <remarks>
    /// The URL is always the one Qloo sent this session for that entity, never one a script supplies, so an entity
    /// no answer named is refused as <c>UnknownEntity</c>. A failure is a value, one picture at a time; the call
    /// itself does not throw for a bad picture. Recorded as <c>taste.images</c>, names and failures only.
    /// </remarks>
    public async Task<TasteImage[]> Images(object source, object? options = null)
    {
        var take = Math.Clamp(Option<int?>(options, "take") ?? 24, 1, MaxImages);
        var type = TypeUrn(Option<string>(options, "type"));
        var wanted = IdsOf(source)
            .Select(id => (Id: id, Found: registry.Entity(id)))
            .Where(p => type is null || p.Found is null
                || (p.Found.Value.Entity.GetValueOrDefault("types") as object?[] ?? []).Any(t => string.Equals(t as string, type, StringComparison.OrdinalIgnoreCase))
                || string.Equals(p.Found.Value.Entity.GetValueOrDefault("type") as string, type, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .ToArray();

        // One deadline for the whole set, inside the script limit: a slow host costs pictures, not the script.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var gate = new SemaphoreSlim(6);
        var pictures = await Task.WhenAll(wanted.Select(async w =>
        {
            await gate.WaitAsync();
            try { return await Picture(w.Id, w.Found, deadline.Token); }
            finally { gate.Release(); }
        }));

        record?.Invoke("taste.images", new Dictionary<string, object?>
        {
            ["requested"] = pictures.Length,
            ["delivered"] = pictures.Count(p => p.Success),
            ["fromCache"] = pictures.Count(p => p.FromCache),
            ["names"] = pictures.Where(p => p.Success).Select(p => p.Name).ToArray(),
            ["failures"] = pictures.Where(p => !p.Success).Select(p => $"{p.Name}: {p.Failure}").ToArray(),
        });
        return pictures;
    }

    /// <summary>
    /// Where a set of pictures' lead hues fall on the wheel — the crowded sectors, the empty ones, the neutral
    /// pictures: <c>{ n, neutral, sectors, crowded, open, light, dark, summary }</c>. See <see cref="TasteVisual.ColourField"/>.
    /// Recorded as <c>taste.field</c>.
    /// </summary>
    public Dictionary<string, object?> ColourField(TasteImage[] pictures)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        var field = TasteVisual.ColourField(pictures);
        record?.Invoke("taste.field", new Dictionary<string, object?>
        {
            ["n"] = field["n"], ["crowded"] = field["crowded"], ["open"] = field["open"], ["summary"] = field["summary"],
        });
        return field;
    }

    /// <summary>
    /// The words recurring in Qloo's style descriptions of a set — pictures, an answer, or its entities:
    /// <c>[{ word, count, share, names }]</c>. See <see cref="TasteVisual.Vocabulary"/>.
    /// </summary>
    /// <param name="options"><c>{ take?: number (default 20), min?: number (default 2) }</c>.</param>
    public object?[] Vocabulary(object source, object? options = null)
    {
        // Pictures carry their descriptions; anything else is looked up by id in this session's answers.
        var pictures = source switch
        {
            TasteImage[] given => given,
            System.Collections.IEnumerable items and not string when items.Cast<object?>().ToArray() is { Length: > 0 } all
                && all.All(i => i is TasteImage) => all.Cast<TasteImage>().ToArray(),
            _ => null,
        };
        var described = pictures?.Select(p => (p.Name, p.StyleDescription))
            ?? IdsOf(source).Select(id => registry.Entity(id)).OfType<(Dictionary<string, object?> Entity, string Answer)>()
                .Select(p => (p.Entity.GetValueOrDefault("name") as string ?? "?", StyleOf(p.Entity)));
        return TasteVisual.Vocabulary(described, Option<int?>(options, "take") ?? 20, Option<int?>(options, "min") ?? 2);
    }

    /// <summary>
    /// A colour in the terms a field is measured in: <c>{ lightness, chroma, hue, neutral, sector }</c>, with
    /// <c>sector</c> spelled as <see cref="ColourField"/> spells it (<c>'blue 240–270°'</c>), or null for a neutral.
    /// For placing a finished piece's colour on the field its research measured. Takes <c>#rgb</c> or <c>#rrggbb</c>.
    /// </summary>
    public Dictionary<string, object?> Oklch(string color)
    {
        var hex = (color ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            throw new ArgumentException($"Taste.oklch takes '#rrggbb' or '#rgb'; '{color}' is neither.", nameof(color));

        var (L, C, H) = TasteColour.Oklch((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var neutral = C < TasteColour.NeutralChroma;
        var from = (int)(H / 30) % 12 * 30;
        return new()
        {
            ["lightness"] = Math.Round(L, 4), ["chroma"] = Math.Round(C, 4), ["hue"] = Math.Round(H, 1), ["neutral"] = neutral,
            ["sector"] = neutral ? null : $"{TasteVisual.SectorNames[from / 30]} {from}–{from + 30}°",
        };
    }

    private static string Named(TasteResult r) => string.IsNullOrWhiteSpace(r.Label) ? r.Id : $"{r.Id} ({r.Label})";

    private async Task<TasteImage> Picture(string id, (Dictionary<string, object?> Entity, string Answer)? found, CancellationToken token)
    {
        if (found is not { } f)
            return new TasteImage { Id = id, Name = id, Failure = TasteImageFailure.UnknownEntity,
                Error = $"No Qloo answer this session named '{id}'. Ask the Taste tool first; a picture comes only with its answer." };

        var name = f.Entity.GetValueOrDefault("name") as string ?? id;
        var properties = Get(f.Entity, "properties") as IDictionary<string, object?>;
        var url = Get(properties, "image") is IDictionary<string, object?> image ? Get(image, "url") as string : null;
        var year = Get(properties, "release_year") is { } y && int.TryParse(Convert.ToString(y, System.Globalization.CultureInfo.InvariantCulture), out var parsed) ? parsed : (int?)null;
        var rank = f.Entity.GetValueOrDefault("audienceRank") is { } r ? Convert.ToInt32(r, System.Globalization.CultureInfo.InvariantCulture)
            : Array.IndexOf(registry.Get(f.Answer)?.Entities ?? [], f.Entity) + 1;
        TasteImage Failed(TasteImageFailure failure, string error) => new()
        {
            Id = id, Name = name, Type = f.Entity.GetValueOrDefault("type") as string, Url = url, Year = year, Rank = rank,
            StyleDescription = StyleOf(f.Entity), Answer = f.Answer, Failure = failure, Error = error,
        };

        if (images is null) return Failed(TasteImageFailure.NotConfigured, "Qloo pictures are not configured on this server.");
        if (string.IsNullOrWhiteSpace(url)) return Failed(TasteImageFailure.NoImage, $"Qloo keeps no picture for {name}.");
        if (!images.IsCached(url) && !registry.TakeImages(1))
            return Failed(TasteImageFailure.AllowanceSpent, $"This session's {TasteRegistry.ImageAllowance} Qloo pictures are spent.");

        var fetched = await images.Fetch(url, token);
        if (!fetched.Success) return Failed(fetched.Failure, fetched.Error ?? fetched.Failure.ToString());
        return new TasteImage
        {
            Success = true, Id = id, Name = name, Type = f.Entity.GetValueOrDefault("type") as string, Url = url, Year = year, Rank = rank,
            StyleDescription = StyleOf(f.Entity), Answer = f.Answer, Width = fetched.Width, Height = fetched.Height,
            MimeType = fetched.MimeType, FromCache = fetched.FromCache, Colours = fetched.Colour?.Colours ?? [],
            Neutral = fetched.Colour?.Neutral ?? 1, Lightness = fetched.Colour?.Lightness ?? 0, Lead = fetched.Colour?.Lead,
            bytes = fetched.Bytes, taken = Taken,
        };
    }

    /// <summary>
    /// Records <c>taste.pictures</c> once per script, the first time it takes a Qloo picture's pixels. Every render
    /// that script makes is then research to the studio, whatever the file is called, and is never served: the
    /// pictures are other people's posters and logos (Manual 29 §2a). Names only reach the record, never pixels.
    /// </summary>
    private void Taken(TasteImage picture)
    {
        if (Interlocked.Exchange(ref picturesTaken, 1) == 1) return;
        record?.Invoke("taste.pictures", new Dictionary<string, object?> { ["first"] = picture.Name });
    }

    private static string? StyleOf(Dictionary<string, object?> entity) =>
        Get(Get(entity, "properties") as IDictionary<string, object?>, "style_description") as string;

    /// <summary>Entity ids from an answer, a single id, or an array of entities, ids or pictures — in order, once each.</summary>
    private static string[] IdsOf(object? source) => source switch
    {
        null => [],
        string id => [id],
        TasteResult r => [.. r.Entities.OfType<Dictionary<string, object?>>().Select(e => e.GetValueOrDefault("id") as string).OfType<string>().Distinct()],
        IDictionary<string, object?> e => Get(e, "id") is string id ? [id] : [],
        System.Collections.IEnumerable items => [.. items.Cast<object?>().Select(i => i switch
        {
            string s => s,
            TasteImage p => p.Id,
            IDictionary<string, object?> e => Get(e, "id") as string,
            _ => null,
        }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
        _ => [],
    };

    private static string? TypeUrn(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "film" or "films" or "movie" => "urn:entity:movie",
        "show" or "tv" or "tv_show" => "urn:entity:tv_show",
        var t when t.StartsWith("urn:", StringComparison.Ordinal) => t,
        var t => "urn:entity:" + t,
    };

    private static T? Option<T>(object? options, string name)
    {
        if (Get(options as IDictionary<string, object?>, name) is not { } value) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? Get(IDictionary<string, object?>? dict, string key) =>
        dict is not null && dict.TryGetValue(key, out var value) ? value : null;

    private const int MaxImages = 48;
    #endregion

    #region Fields
    private readonly TasteRegistry registry;
    private readonly string? projectRoot;
    private readonly Action<string, IReadOnlyDictionary<string, object?>>? record;
    private readonly QlooImageSource? images;
    private int picturesTaken;
    #endregion
}

/// <summary>
/// Qloo entities and tags as the plain dictionaries scripts read. Shared by <see cref="TasteResult"/> and the MCP
/// layer's profile builder; not itself exposed to the sandbox.
/// </summary>
public static class TasteObjects
{
    /// <summary>Builds the record of one response. Never throws: a failed response is recorded as failed.</summary>
    public static TasteResult Result(string id, string op, string label, QlooResponse response) => new TasteResult
    {
        Id = id,
        Op = op,
        Label = label,
        Request = response.Request,
        Ok = response.Success,
        Error = response.Error,
        Remedy = response.Remedy,
        FromCache = response.FromCache,
        CachedUtc = response.CachedUtc?.ToString("O"),
        RequestId = response.RequestId,
        Warnings = [.. response.Warnings],
        Locality = response is QlooInsightsResponse { Locality: { } l }
            ? new() { ["id"] = l.EntityId, ["name"] = l.Name, ["disambiguation"] = l.Disambiguation }
            : null,
        Entities = response switch
        {
            QlooInsightsResponse i => [.. i.Entities.Select(TasteObjects.Entity)],
            QlooEntitiesResponse e => [.. e.Entities.Select(TasteObjects.Entity)],
            _ => [],
        },
        Tags = response switch
        {
            QlooInsightsResponse i => [.. i.Tags.Select(TasteObjects.Tag)],
            QlooTagsResponse t => [.. t.Tags.Select(TasteObjects.Tag)],
            _ => [],
        },
        Audiences = response is QlooAudiencesResponse a
            ? [.. a.Audiences.Select(x => (object?)new Dictionary<string, object?>
              { ["id"] = x.Id, ["name"] = x.Name, ["type"] = x.Type, ["parents"] = x.Parents.ToArray() })]
            : [],
        Demographics = response is QlooInsightsResponse d ? [.. d.Demographics.Select(e => JsonInterop.ToClr(e))] : [],
        Comparison = response is QlooCompareResponse c ? JsonInterop.ToClr(c.Results) : null,
    };

    /// <summary>
    /// The record of a profile, built from several responses. <see cref="Entities"/> holds every distinctive entity
    /// across the domains and <see cref="Tags"/> the place's aesthetic tags, so a script reads either without
    /// walking the profile.
    /// </summary>
    public static TasteResult ProfileResult(
        string id, string label, Dictionary<string, object?> profile, IReadOnlyList<QlooResponse> responses,
        object?[] distinctive, object?[] placeTags, QlooLocality? locality, IEnumerable<string> extraWarnings)
    {
        var failed = responses.FirstOrDefault(r => !r.Success);
        return new TasteResult
        {
            Id = id,
            Op = "profile",
            Label = label,
            Request = string.Join(" ; ", responses.Select(r => r.Request)),
            Ok = failed is null,
            Error = failed?.Error,
            Remedy = failed?.Remedy,
            FromCache = responses.Count > 0 && responses.All(r => r.FromCache),
            CachedUtc = responses.Where(r => r.CachedUtc is not null).Select(r => r.CachedUtc).Min()?.ToString("O"),
            RequestId = responses.Select(r => r.RequestId).FirstOrDefault(r => r is not null),
            Warnings = [.. responses.SelectMany(r => r.Warnings).Concat(extraWarnings).Distinct()],
            Locality = locality is null ? null : new() { ["id"] = locality.EntityId, ["name"] = locality.Name, ["disambiguation"] = locality.Disambiguation },
            Entities = distinctive,
            Tags = placeTags,
            Profile = profile,
        };
    }


    public static object? Entity(QlooEntity e) => new Dictionary<string, object?>
    {
        ["id"] = e.EntityId,
        ["name"] = e.Name,
        ["type"] = e.Types.FirstOrDefault(),
        ["types"] = e.Types.ToArray(),
        ["disambiguation"] = e.Disambiguation,
        ["popularity"] = e.Popularity,
        ["affinity"] = e.Affinity,
        ["tags"] = e.Tags.Select(Tag).ToArray(),
        ["explainability"] = JsonInterop.ToClr(e.Explainability),
        ["properties"] = JsonInterop.ToClr(e.Properties),
    };

    public static object? Tag(QlooTag t) => new Dictionary<string, object?>
    {
        ["id"] = t.Id, ["name"] = t.Name, ["type"] = t.Type, ["popularity"] = t.Popularity, ["affinity"] = t.Affinity,
    };
}
