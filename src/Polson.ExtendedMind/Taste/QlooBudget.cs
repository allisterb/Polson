namespace Polson.ExtendedMind.Taste;

using System;
using System.Threading;

/// <summary>
/// How many Qloo requests this server run may send, and the monthly reserve it will not dip into. Readable,
/// so an agent can plan rather than hit a wall.
/// </summary>
/// <remarks>
/// <para>
/// Counted in <b>requests sent</b>, retries included, because each one draws on the key's quota — measured
/// at five a second and 10,000 a month (2026-10-15), shared by development, the demo and judging. A cache hit
/// sends nothing and is not charged; a query refused by <see cref="QlooParameters"/> sends nothing either.
/// </para>
/// <para>
/// <b>The reserve is the protection that matters on a public URL.</b> The per-run ceiling bounds one run;
/// nothing else bounds the month. So once Qloo's own <c>X-Month-RateLimit-Remaining</c> header reports
/// <see cref="MonthlyReserve"/> or fewer, every request is refused — whatever this run has left — and the
/// remainder stays for judging. Cached answers keep working.
/// </para>
/// </remarks>
public sealed class QlooBudget
{
    #region Constructors
    /// <param name="total">Requests this run may send. Zero turns Qloo off.</param>
    /// <param name="monthlyReserve">Monthly requests never spent. Zero spends to the last request.</param>
    public QlooBudget(int total = DefaultTotal, int monthlyReserve = DefaultMonthlyReserve)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfNegative(monthlyReserve);
        Total = total;
        MonthlyReserve = monthlyReserve;
    }
    #endregion

    #region Properties
    /// <summary>
    /// Requests per server run when <c>Qloo:Budget</c> is not set. A taste profile takes about fifteen; this
    /// allows several profiles and their lookups, and is far below the month.
    /// </summary>
    public const int DefaultTotal = 120;

    /// <summary>Monthly requests held back when <c>Qloo:MonthlyReserve</c> is not set.</summary>
    public const int DefaultMonthlyReserve = 1000;

    public int Total { get; }

    public int MonthlyReserve { get; }

    /// <summary>Requests sent this run, retries included.</summary>
    public int Spent => spent;

    public int Remaining => Math.Max(0, Total - spent);

    /// <summary>Answers served from the cache, which sent nothing.</summary>
    public int CacheHits => cacheHits;

    /// <summary>Requests refused by this budget, by the run ceiling or the reserve.</summary>
    public int Refused => refused;

    /// <summary>The month's remaining quota as Qloo last reported it, or null before the first response.</summary>
    public int? MonthlyRemaining => monthlyRemaining < 0 ? null : monthlyRemaining;

    /// <summary>Whether the reserve has been reached, by Qloo's last report.</summary>
    public bool ReserveReached => monthlyRemaining >= 0 && monthlyRemaining <= MonthlyReserve;
    #endregion

    #region Methods
    public bool CanAfford(int count = 1) => Remaining >= count && !ReserveReached;

    /// <summary>
    /// Charges one request if it may be sent. Returns null when charged, or the reason it may not be — the
    /// sentence an agent will read.
    /// </summary>
    /// <param name="monthlyRemaining">
    /// The month's remaining quota as the shared client last saw it, from any session. A new session's budget
    /// has observed nothing yet, and must not be the gap the reserve leaks through.
    /// </param>
    internal string? TryCharge(int? monthlyRemaining = null)
    {
        if (monthlyRemaining is int seen) Observe(seen);
        if (ReserveReached)
        {
            Interlocked.Increment(ref refused);
            return $"Qloo reports {this.monthlyRemaining} requests left this month, at or below the {MonthlyReserve} held "
                 + "in reserve for the demo and judging. Work from what has already been fetched; cached answers still work.";
        }

        while (true)
        {
            var current = spent;
            if (current >= Total)
            {
                Interlocked.Increment(ref refused);
                return Total == 0
                    ? "Qloo is turned off for this run (Qloo:Budget is 0)."
                    : $"This run's Qloo allowance of {Total} requests is spent. Cached answers still work.";
            }
            if (Interlocked.CompareExchange(ref spent, current + 1, current) == current) return null;
        }
    }

    internal void CountHit() => Interlocked.Increment(ref cacheHits);

    /// <summary>Records the month's remaining quota from a response's headers.</summary>
    internal void Observe(QlooRateLimit? limit)
    {
        if (limit?.MonthlyRemaining is int remaining) Observe(remaining);
    }

    private void Observe(int remaining) => Interlocked.Exchange(ref monthlyRemaining, remaining);
    #endregion

    #region Fields
    private int spent;
    private int cacheHits;
    private int refused;
    private int monthlyRemaining = -1;
    #endregion
}
