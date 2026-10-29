namespace Polson.ExtendedMind.ParallelSearch;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A stated fact checked on the web: one claim per Decisions call, so its citations are its own, read as
/// <c>supported</c>, <c>unsettled</c> or <c>not supported</c>.
/// </summary>
/// <remarks>
/// <para>
/// For a fact with no document behind it — a film's credits — which the read-back otherwise records as the agent's own
/// words. Probed on 2026-10-28 with 23 claims: every true one scored 0.995 or more and every false one 0.031 or less,
/// near misses included (a famous editor's surname, a character from the first film, a figure off by $100M). A claim the
/// sources leave open landed in between (0.46 for a runtime they disagree on); one nothing on the web settles scored 0.03,
/// as low as a false one. So a low score says <i>not supported</i>, never <i>false</i>.
/// </para>
/// <para>
/// Checks are cached by claim for the process, so a read-back run again does not pay again, and capped at
/// <see cref="Budget"/> calls. Citations are kept whole, each marked <see cref="FactSource.Recognised"/>: one probe cited a
/// full copy of a novel on a public bucket, which a page judges read should not link to.
/// </para>
/// </remarks>
public sealed class FactChecker : Runtime
{
    #region Constructors
    public FactChecker(ParallelClient client, int budget = DefaultBudget)
    {
        this.client = client;
        Budget = budget;
    }
    #endregion

    #region Properties
    /// <summary>At or above this, the sources support the claim.</summary>
    public const double SupportedAt = 0.9;

    /// <summary>Below this, they do not; between the two, the claim is unsettled.</summary>
    public const double NotSupportedBelow = 0.1;

    /// <summary>Calls a process may make. At $1 per 1,000 this is a guard against a loop, not a spend limit.</summary>
    public const int DefaultBudget = 200;

    public int Budget { get; }

    public int Spent => spent;

    /// <summary>
    /// Sources a reader would accept as a reference: encyclopaedias, film and book databases, publishers, national
    /// newspapers and broadcasters, and any <c>.gov</c> or <c>.edu</c>. Matched on the host and its parents.
    /// </summary>
    public static readonly IReadOnlySet<string> RecognisedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wikipedia.org", "wikidata.org", "britannica.com", "imdb.com", "themoviedb.org", "bfi.org.uk", "afi.com",
        "boxofficemojo.com", "the-numbers.com", "mubi.com", "moma.org", "rottentomatoes.com", "metacritic.com",
        "rogerebert.com", "variety.com", "hollywoodreporter.com", "deadline.com", "publishersweekly.com", "loc.gov",
        "worldcat.org", "nytimes.com", "washingtonpost.com", "latimes.com", "wsj.com", "theguardian.com", "bbc.com",
        "bbc.co.uk", "npr.org", "reuters.com", "apnews.com", "ft.com", "economist.com", "bloomberg.com", "cnbc.com",
        "sec.gov", "timesofindia.indiatimes.com", "thehindu.com", "indianexpress.com", "hindustantimes.com",
        "folha.uol.com.br", "globo.com", "estadao.com.br", "lemonde.fr", "spiegel.de", "elpais.com", "nhk.or.jp",
        "asahi.com", "japantimes.co.jp", "aljazeera.com",
    };
    #endregion

    #region Methods
    /// <summary>The claim checked, from the cache when it has been asked before.</summary>
    /// <param name="about">What the claim is about, in English: <c>"Be Cool (2005 film)"</c>.</param>
    /// <param name="claim">A full sentence; any language, though the subject keeps it on course.</param>
    public async Task<FactCheck> Check(string about, string claim, CancellationToken cancellationToken = default)
    {
        var key = (about.Trim(), claim.Trim());
        if (cache.TryGetValue(key, out var known)) return known;
        if (Interlocked.Increment(ref spent) > Budget)
        {
            Interlocked.Decrement(ref spent);
            return FactCheck.Unchecked(about, claim, $"this session's {Budget} fact checks are spent");
        }

        var response = await client.Decide(key.Item1, key.Item2, cancellationToken);
        if (!response.Success)
        {
            Warn("Fact check failed ({Failure}): {Error}", response.Failure, response.Error);
            return new FactCheck(key.Item1, key.Item2, FactCheck.Failed, null, [], $"{response.Failure}: {response.Error}");
        }

        var answer = response.Answers.GetValueOrDefault(ParallelClient.DecisionQuestion);
        var sources = response.Citations.Where(c => !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new FactSource(c.Url, c.Title, IsRecognised(c.Url))).ToArray();
        var check = answer is { Noul: { } p }
            ? new FactCheck(key.Item1, key.Item2, VerdictOf(p), p, sources, null)
            : new FactCheck(key.Item1, key.Item2, FactCheck.Refused, null, sources, "the service made no decision");
        cache[key] = check;
        return check;
    }

    /// <summary><c>supported</c>, <c>unsettled</c> or <c>not supported</c>, by <see cref="SupportedAt"/> and <see cref="NotSupportedBelow"/>.</summary>
    public static string VerdictOf(double probability) =>
        probability >= SupportedAt ? FactCheck.Supported : probability < NotSupportedBelow ? FactCheck.NotSupported : FactCheck.Unsettled;

    public static bool IsRecognised(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith(".gov", StringComparison.Ordinal) || host.EndsWith(".edu", StringComparison.Ordinal)) return true;
        for (var h = host; h.Contains('.'); h = h[(h.IndexOf('.') + 1)..])
            if (RecognisedDomains.Contains(h)) return true;
        return false;
    }
    #endregion

    #region Fields
    private readonly ParallelClient client;
    private readonly ConcurrentDictionary<(string, string), FactCheck> cache = new();
    private int spent;
    #endregion
}

/// <summary>One claim as checked: its verdict, the probability behind it, and every source the check read.</summary>
public sealed record FactCheck(string About, string Claim, string Verdict, double? Probability, IReadOnlyList<FactSource> Sources, string? Error)
{
    public const string Supported = "supported";
    public const string Unsettled = "unsettled";
    public const string NotSupported = "not supported";
    public const string Refused = "refused";
    public const string Failed = "failed";
    public const string NotChecked = "unchecked";

    public static FactCheck Unchecked(string about, string claim, string why) => new(about, claim, NotChecked, null, [], why);

    /// <summary>The verdict, its probability, and the recognised sources' hosts, as one line.</summary>
    public string Detail()
    {
        var hosts = Sources.Where(s => s.Recognised).Select(s => s.Host).Distinct().Take(3).ToArray();
        return Verdict + (Probability is { } p ? $" ({p:0.000})" : "")
            + (hosts.Length > 0 ? $": {string.Join(", ", hosts)}" : Error is { } e ? $": {e}" : Sources.Count > 0 ? ": no recognised source" : "");
    }
}

/// <summary>A page the check read. <see cref="Recognised"/>: from <see cref="FactChecker.RecognisedDomains"/>, so fit to show.</summary>
public sealed record FactSource(string Url, string? Title, bool Recognised)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host : Url;
}
