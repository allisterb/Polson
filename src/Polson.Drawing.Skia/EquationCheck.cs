namespace Polson.Drawing.Skia;

using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

using AngouriMath;
using CSharpMath.Atom;

using NComplex = System.Numerics.Complex;

/// <summary>Whether two expressions are the same mathematics: the engine behind <c>Equation.equivalent</c>.</summary>
/// <remarks>
/// <para>
/// <b>Two passes, and the definite one wins.</b> AngouriMath simplifies <c>a − b</c>: 0 proves the identity, and a
/// nonzero constant disproves it. Then both sides are evaluated at random points: one point where they differ is a
/// counterexample, which outranks a symbolic "0" (a simplifier can be wrong; a counterexample can be re-run). Agreement
/// at every point without a proof is evidence, not proof, and is reported as such.
/// </para>
/// <para>
/// <b>Bounded, because the input is an agent's or a watcher's.</b> The parser and AngouriMath both recurse, and a stack
/// overflow ends the process with no catch: length, brace and bracket nesting are checked first, and AngouriMath runs on a
/// thread of its own with a large stack, under a cancellation token and a time budget. Only the LaTeX bridge
/// (<see cref="MathEvaluation"/>) builds expressions: AngouriMath's own string parser is never used.
/// </para>
/// </remarks>
internal static partial class EquationCheck
{
    #region Types
    /// <summary>What a check may be asked: the domain of each variable, which are integers, how many points, how long.</summary>
    internal sealed record Options(IReadOnlyDictionary<string, (double Lo, double Hi)> Domain, IReadOnlySet<string> Integers, int Samples, int BudgetMs);

    /// <summary>
    /// The outcome. <c>Kind</c> is <c>'proved'</c>, <c>'agrees'</c>, <c>'differs'</c>, <c>'unsettled'</c> or
    /// <c>'unread'</c> (the LaTeX was refused or did not convert).
    /// </summary>
    internal sealed record Verdict(string Kind, string? A, string? B, string Detail)
    {
        public string? Provided { get; init; }
        public IReadOnlyDictionary<string, double>? At { get; init; }
        public NComplex? ValueA { get; init; }
        public NComplex? ValueB { get; init; }
        public int Points { get; init; }
        public bool Approximate { get; init; }
        public string? Side { get; init; }
    }
    #endregion

    #region Methods
    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are equal; with <paramref name="b"/> null, <paramref name="a"/> is an equation, <c>lhs = rhs</c>.</summary>
    public static Verdict Check(string a, string? b, Options options)
    {
        var (listA, linearA, refusedA) = Read(a);
        if (refusedA is not null) return new("unread", null, null, refusedA) { Side = "a" };
        MathList? listB = null;
        string? linearB = null;
        if (b is not null)
        {
            (listB, linearB, var refusedB) = Read(b);
            if (refusedB is not null) return new("unread", linearA, null, refusedB) { Side = "b" };
        }

        // The bridge, then each pass, on a thread of their own: a deep tree recurses, and a slow one must be stoppable.
        var converted = OnOwnStack(_ => Convert(listA!, listB), ConvertMs, out var convertTimedOut);
        if (convertTimedOut || converted.Error is not null || converted.Left is null || converted.Right is null)
            return new("unread", linearA, linearB, convertTimedOut ? "converting the LaTeX took too long." : converted.Error ?? "the LaTeX is not an expression.")
                { Side = converted.Side };
        var (left, right) = (converted.Left, converted.Right);
        if (b is null) (linearA, linearB) = Halves(linearA!);

        var symbolicMs = Math.Max(100, options.BudgetMs * 2 / 5);
        var symbolic = OnOwnStack(_ => Symbolic(left, right), symbolicMs, out var symbolicTimedOut);
        var numeric = OnOwnStack(token => Numeric(left, right, options, Seed(a, b), token), options.BudgetMs - symbolicMs, out var numericTimedOut);

        var points = numeric?.Agreed ?? 0;
        var approximate = numeric?.Approximate ?? false;
        if (numeric?.Counterexample is { } c)
            return new("differs", linearA, linearB, symbolic?.Kind == "zero"
                    ? "AngouriMath simplified the difference to 0, but the sides differ at a point: the counterexample stands."
                    : "the sides differ at a point.")
                { At = c.At, ValueA = c.A, ValueB = c.B, Points = points, Approximate = approximate };
        if (symbolic?.Kind == "constant")
            return new("differs", linearA, linearB, "the difference simplifies to a constant that is not 0.")
                { ValueA = symbolic.A, ValueB = symbolic.B, Points = points, Approximate = approximate };
        if (symbolic?.Kind == "zero")
            return new("proved", linearA, linearB, symbolic.Provided is null
                    ? "AngouriMath simplified the difference to 0."
                    : $"AngouriMath simplified the difference to 0, provided {symbolic.Provided}.")
                { Provided = symbolic.Provided, Points = points, Approximate = approximate };
        if (numeric is { Constant: true } && points == 1)
            return new("agrees", linearA, linearB, approximate
                    ? "no proof, but the two numbers agree to the precision of a numerical integral."
                    : "no proof, but the two numbers agree to nine significant figures.")
                { Points = points, Approximate = approximate };
        if (points >= MinPoints)
            return new("agrees", linearA, linearB, $"no proof, but the sides agree at all {points} points tried.")
                { Points = points, Approximate = approximate };

        var why = new List<string>();
        if (symbolicTimedOut) why.Add("simplifying took longer than its budget");
        else why.Add("the difference did not simplify to a number");
        if (numericTimedOut) why.Add("evaluating took longer than its budget");
        else if (numeric?.Note is { } note) why.Add(note);
        else why.Add($"only {points} point{(points == 1 ? "" : "s")} could be evaluated, of the {MinPoints} needed");
        return new("unsettled", linearA, linearB, string.Join("; ", why) + ".") { Points = points, Approximate = approximate };
    }

    /// <summary>Reads checker options: <c>{ domain?, integers?, samples?, budget? }</c>.</summary>
    public static Options ReadOptions(object? options, string method)
    {
        var o = JsInterop.AsDict(options);
        var domain = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        var integers = new HashSet<string>(StringComparer.Ordinal);
        int samples = DefaultSamples, budget = DefaultBudgetMs;
        if (o is null) return new(domain, integers, samples, budget);

        foreach (var key in o.Keys.Cast<object>().Select(k => k.ToString()!))
            if (key is not ("domain" or "integers" or "samples" or "budget"))
                throw new ArgumentException($"{method}'s options are {{ domain, integers, samples, budget }}; '{key}' is not one.");
        if (JsInterop.AsDict(o["domain"]) is { } d)
            foreach (var name in d.Keys.Cast<object>().Select(k => k.ToString()!))
            {
                var range = Numbers(d[name]);
                if (range is not [var lo, var hi] || !(lo < hi) || !double.IsFinite(lo) || !double.IsFinite(hi))
                    throw new ArgumentException($"{method}'s domain.{name} is [low, high], with low below high, such as {{ x: [0, 5] }}.");
                domain[name] = (lo, hi);
            }
        if (o["integers"] is IEnumerable names and not string)
            foreach (var n in names) integers.Add(n?.ToString() ?? "");
        if (o["samples"] is { } s) samples = (int)Math.Clamp(System.Convert.ToDouble(s, CultureInfo.InvariantCulture), 4, MaxSamples);
        if (o["budget"] is { } bu) budget = (int)Math.Clamp(System.Convert.ToDouble(bu, CultureInfo.InvariantCulture), 200, MaxBudgetMs);
        return new(domain, integers, samples, budget);

        static double[]? Numbers(object? v) => v is IEnumerable e and not string
            ? [.. e.Cast<object?>().Select(x => x is null ? double.NaN : System.Convert.ToDouble(x, CultureInfo.InvariantCulture))]
            : null;
    }

    /// <summary>
    /// The LaTeX as the bridge needs it: calculus written with an italic d, as agents and readers write it
    /// (<c>\frac{d}{dx}</c>, <c>\int … dx</c>), given the upright d the bridge requires.
    /// </summary>
    internal static string Normalise(string latex)
    {
        var s = DerivativeFraction().Replace(latex, m => $@"\frac{{\mathrm{{d}}{m.Groups[1].Value}{m.Groups[2].Value}}}{{\mathrm{{d}}{m.Groups[3].Value}{m.Groups[4].Value}}}");
        // An integral's own d is the last one before its end: one per \int, taken from the right.
        var integrals = Regex.Count(s, @"\\int(?![a-zA-Z])");
        if (integrals == 0) return s;
        var ds = IntegralD().Matches(s).Cast<Match>().TakeLast(integrals).Select(m => m.Index).ToHashSet();
        return IntegralD().Replace(s, m => ds.Contains(m.Index) ? $@"\mathrm{{d}}{m.Groups[1].Value}" : m.Value);
    }

    /// <summary>The parsed MathList and its linear form, or why the LaTeX was refused.</summary>
    private static (MathList? List, string? Linear, string? Refused) Read(string latex)
    {
        if (EquationLayout.Guard(latex) is { } refusal) return (null, null, refusal);
        if (latex.Length > MaxLength) return (null, null, $"the LaTeX is {latex.Length} characters; at most {MaxLength} are checked.");
        int depth = 0, deepest = 0;
        foreach (var ch in latex)
        {
            if (ch is '(' or '[') deepest = Math.Max(deepest, ++depth);
            else if (ch is ')' or ']') depth--;
        }
        if (deepest > EquationLayout.MaxNesting) return (null, null, $"the brackets nest {deepest} deep; at most {EquationLayout.MaxNesting} are checked.");

        var normal = Normalise(latex);
        lock (EquationLayout.Typesetting)
        {
            // Parsed twice: the bridge rewrites the list it reads, and the linear form is of the LaTeX as written.
            var (shown, error) = LaTeXParser.MathListFromLaTeX(latex);
            if (error is not null) return (null, null, error.Trim());
            var (list, normalError) = LaTeXParser.MathListFromLaTeX(normal);
            return normalError is null ? (list, MathLinear.Of(shown), null) : (null, null, normalError.Trim());
        }
    }

    private static (Entity? Left, Entity? Right, string? Error, string? Side) Convert(MathList a, MathList? b)
    {
        var (itemA, errorA) = MathEvaluation.Evaluate(a);
        if (errorA is not null) return (null, null, errorA, "a");
        if (itemA is not MathEvaluation.MathItem.Entity { Content: var left }) return (null, null, "the LaTeX is not a single expression.", "a");
        if (b is null)
            return left is Entity.Equalsf eq ? (eq.Left, eq.Right, null, null) : (null, null, "with one argument, the LaTeX is an equation: lhs = rhs.", "a");
        var (itemB, errorB) = MathEvaluation.Evaluate(b);
        if (errorB is not null) return (null, null, errorB, "b");
        return itemB is MathEvaluation.MathItem.Entity { Content: var right } ? (left, right, null, null) : (null, null, "the LaTeX is not a single expression.", "b");
    }

    /// <summary>
    /// The two sides of an equation's linear form, split at its <c>=</c> outside every bracket: a sum's
    /// <c>∑_(k = 1)ⁿ</c> has one of its own.
    /// </summary>
    private static (string, string?) Halves(string linear)
    {
        var depth = 0;
        for (var i = 0; i + 2 < linear.Length; i++)
        {
            if (linear[i] is '(' or '[' or '{') depth++;
            else if (linear[i] is ')' or ']' or '}') depth--;
            else if (depth == 0 && string.CompareOrdinal(linear, i, " = ", 0, 3) == 0) return (linear[..i], linear[(i + 3)..]);
        }
        return (linear, null);
    }

    private sealed record SymbolicResult(string Kind, string? Provided = null, NComplex? A = null, NComplex? B = null);

    private static SymbolicResult Symbolic(Entity a, Entity b)
    {
        var difference = (a - b).Simplify();
        if (IsZero(difference)) return new("zero");
        if (difference is Entity.Providedf { Expression: var e, Predicate: var p } && IsZero(e)) return new("zero", p.ToString());
        if (difference.Vars.Count == 0 && difference.EvaluableNumerical && Value(a) is { } va && Value(b) is { } vb)
            return new("constant", A: va, B: vb);
        return new("open");

        static bool IsZero(Entity e) => e is Entity.Number.Complex c && c.IsZero;
    }

    private sealed record Counter(IReadOnlyDictionary<string, double> At, NComplex A, NComplex B);

    private sealed record NumericResult(int Agreed, Counter? Counterexample, bool Approximate, string? Note, bool Constant = false);

    /// <summary>
    /// Both sides at random points, drawn reproducibly from the inputs: free variables from <c>domain</c>, else either sign
    /// of 0.25 to 2.5; a sum's bounds and <c>integers</c> from 1 to 12.
    /// </summary>
    private static NumericResult Numeric(Entity a, Entity b, Options options, int seed, CancellationToken token)
    {
        // Calculus first: substituting into an unevaluated derivative rewrites its own variable.
        var (ea, eb) = (a.Evaled, b.Evaled);
        if (new[] { ea, eb }.Any(e => e.Nodes.OfType<Entity.Derivativef>().Any() || e.Nodes.OfType<Entity.Limitf>().Any()))
            return new(0, null, false, "a derivative or limit that AngouriMath could not work out cannot be evaluated at a point");

        var bound = new[] { ea, eb }.SelectMany(e => e.Nodes.OfType<Entity.CalculusOperator>()).Select(c => c.Var).OfType<Entity.Variable>().ToHashSet();
        var free = ea.Vars.Concat(eb.Vars).Where(v => !bound.Contains(v)).Distinct().OrderBy(v => v.Name, StringComparer.Ordinal).ToArray();
        // A sum's bounds are whole numbers, read before evaluation works the sum out (to n(n + 1)/2 for n ≥ 0, and 0 below).
        var integer = new[] { a, b, ea, eb }.SelectMany(e => e.Nodes.OfType<Entity.Summationf>())
            .SelectMany(s => s.From.Vars.Concat(s.To.Vars)).Select(v => v.Name).Concat(options.Integers).ToHashSet(StringComparer.Ordinal);

        var random = new Random(seed);
        int agreed = 0, tried = 0;
        var approximate = false;
        // A constant needs one evaluation; anything else, points until enough agree or three times as many were drawn.
        for (var k = 0; k < (free.Length == 0 ? 1 : options.Samples * 3) && agreed < options.Samples; k++)
        {
            token.ThrowIfCancellationRequested();
            var at = free.ToDictionary(v => v, v => Draw(v.Name));
            var (va, approxA) = At(ea, at);
            var (vb, approxB) = At(eb, at);
            if (va is not { } x || vb is not { } y) continue;
            tried++;
            approximate |= approxA || approxB;
            var scale = Math.Max(1, Math.Max(x.Magnitude, y.Magnitude));
            if ((x - y).Magnitude <= (approxA || approxB ? LooseTolerance : Tolerance) * scale) agreed++;
            else return new(agreed, new(at.ToDictionary(p => p.Key.Name, p => p.Value), x, y), approximate, null);
        }
        return new(agreed, null, approximate, tried == 0 ? "neither side could be evaluated as a number at any point" : null, free.Length == 0);

        double Draw(string name)
        {
            if (options.Domain.TryGetValue(name, out var d))
                return integer.Contains(name) ? random.Next((int)Math.Ceiling(d.Lo), (int)Math.Floor(d.Hi) + 1) : Math.Round(d.Lo + random.NextDouble() * (d.Hi - d.Lo), 6);
            if (integer.Contains(name)) return random.Next(1, 13);
            return Math.Round((random.Next(2) == 0 ? -1 : 1) * (0.25 + random.NextDouble() * 2.25), 6);
        }
    }

    /// <summary>An expression's value at a point: exact where AngouriMath evaluates it, else with integrals and sums done numerically.</summary>
    private static (NComplex? Value, bool Approximate) At(Entity e, IReadOnlyDictionary<Entity.Variable, double> at)
    {
        try
        {
            var s = e;
            foreach (var (variable, value) in at) s = s.Substitute(variable, value);
            if (Value(s) is { } exact) return (exact, false);
            var approximate = false;
            var n = s.Replace(node => node switch
            {
                Entity.Integralf { Range: ({ } from, { } to) } i when Real(from) is { } lo && Real(to) is { } hi
                    && Quadrature(i.Expression, i.Var, lo, hi) is { } area => Mark(area),
                Entity.Summationf { From: var from, To: var to } sum when Real(from) is { } lo && Real(to) is { } hi
                    && lo == Math.Floor(lo) && hi == Math.Floor(hi) && hi - lo < MaxSumTerms && Sum(sum.Expression, sum.Var, (long)lo, (long)hi) is { } total => total,
                _ => node,
            });
            return (Value(n), approximate);

            Entity Mark(double area) { approximate = true; return area; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, false);
        }
    }

    private static double? Quadrature(Entity body, Entity variable, double lo, double hi)
    {
        double F(double t) => Value(body.Substitute(variable, t)) is { } v && Math.Abs(v.Imaginary) < 1e-12 ? v.Real : double.NaN;
        var area = MathNet.Numerics.Integrate.OnClosedInterval(F, lo, hi, 1e-10);
        return double.IsFinite(area) ? area : null;
    }

    private static Entity? Sum(Entity body, Entity variable, long lo, long hi)
    {
        var total = NComplex.Zero;
        for (var k = lo; k <= hi; k++)
            if (Value(body.Substitute(variable, k)) is { } v) total += v;
            else return null;
        return total.Imaginary == 0 ? total.Real : null;
    }

    private static NComplex? Value(Entity e)
    {
        if (!e.EvaluableNumerical) return null;
        var v = e.EvalNumerical().ToNumerics();
        return double.IsFinite(v.Real) && double.IsFinite(v.Imaginary) ? v : null;
    }

    private static double? Real(Entity e) => Value(e) is { } v && Math.Abs(v.Imaginary) < 1e-12 ? v.Real : null;

    /// <summary>
    /// Runs <paramref name="work"/> on a thread with a large stack and a cancellation token, waiting at most
    /// <paramref name="budgetMs"/>; on overrun it cancels, which AngouriMath honours at its next check, and returns default.
    /// </summary>
    private static T? OnOwnStack<T>(Func<CancellationToken, T> work, int budgetMs, out bool timedOut)
    {
        using var cancel = new CancellationTokenSource();
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                MathS.Multithreading.SetLocalCancellationToken(cancel.Token);
                result = work(cancel.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; }
        }, StackBytes) { IsBackground = true, Name = "Equation.equivalent" };
        thread.Start();
        timedOut = !thread.Join(budgetMs);
        if (timedOut)
        {
            cancel.Cancel();
            thread.Join(CancelGraceMs);
            return default;
        }
        return failure is null ? result : default;
    }

    /// <summary>A seed from the inputs, so the same check draws the same points every run.</summary>
    private static int Seed(string a, string? b)
    {
        var h = 2166136261u;
        foreach (var ch in a + "\u0001" + b) h = (h ^ ch) * 16777619u;
        return (int)h;
    }

    [GeneratedRegex(@"\\frac\{\s*d(\^\{?\d+\}?)?\s*([A-Za-z]?)\s*\}\{\s*d\s*([A-Za-z])\s*(\^\{?\d+\}?)?\s*\}")]
    private static partial Regex DerivativeFraction();

    [GeneratedRegex(@"(?<![\\A-Za-z])d\s*([A-Za-z])(?![A-Za-z])")]
    private static partial Regex IntegralD();
    #endregion

    #region Fields
    internal const int DefaultSamples = 24, MaxSamples = 200, DefaultBudgetMs = 4000, MaxBudgetMs = 15000, MinPoints = 8, MaxLength = 600;
    private const int ConvertMs = 2000, CancelGraceMs = 1000, StackBytes = 64 << 20, MaxSumTerms = 2000;
    private const double Tolerance = 1e-9, LooseTolerance = 1e-6;
    #endregion
}
