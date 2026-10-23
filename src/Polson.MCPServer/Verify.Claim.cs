namespace Polson.MCPServer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Jint;
using Jint.Native;

using Polson.Drawing.Skia;
using Polson.ExtendedMind.DocumentProcessing;

/// <remarks>
/// <para>
/// <b>A claim is checked by code, and the code is the model's.</b> Nemotron is given the claim and an API sheet and writes
/// JavaScript that checks it with <c>Equation.equivalent</c>, the derivation check and arithmetic; the verdict comes from
/// running that code, and the code is recorded with it, so what was checked can be read. A model asked only "is this
/// true?" gives a verdict nobody can verify (two of five Nemotrons accepted a false identity); asked for the check, Super-VL
/// wrote 22 of 22 right (2026-10-23).
/// </para>
/// <para>
/// <b>The code runs with nothing to misuse.</b> A separate engine, holding only <c>Equation</c>, the derivation check,
/// <c>check</c>, <c>rounds</c> and <c>close</c>: no canvas, files, network, <c>Assets</c> or the rest of <c>Verify</c>, no
/// string compilation, and capped statements, recursion, memory and time. A claim taken from a document can steer the
/// code, and at worst it checks the wrong thing, in the open.
/// </para>
/// </remarks>
public sealed partial class VerifyToolkit
{
    #region Methods
    /// <summary>
    /// A claim checked by code Nemotron writes and the engine runs: <c>{ ok, passed, verdict, claim, checks, code, error,
    /// summary, model, endpoint }</c>, or <c>{ ok: false, failureName, error, remedy }</c> when the model could not be asked
    /// (<c>Pending</c>: run it again unchanged). <c>verdict</c> is <c>'holds'</c>, <c>'fails'</c>, <c>'unchecked'</c> (the
    /// code checked nothing), <c>'error'</c> (it did not run) or <c>'refused'</c> (a check passed by a literal
    /// <c>true</c>). <paramref name="options"/>: <c>{ name?, context?, budget? }</c>; <c>context</c> is the figures the claim
    /// rests on, as text, and <c>budget</c> the milliseconds the code may run (15,000).
    /// </summary>
    public async Task<Dictionary<string, object?>> Claim(string claim, object? options = null)
    {
        if (string.IsNullOrWhiteSpace(claim)) throw new ArgumentException("Verify.claim needs the claim, as the piece states it: Verify.claim('Revenue grew 46%').");
        if (claim.Length > MaxClaimLength) throw new ArgumentException($"Verify.claim takes a claim of at most {MaxClaimLength} characters; split a longer passage into its claims.");
        var (name, context, budget) = ClaimOptions(options, claim);
        if (video is null || !video.IsAvailable)
            return Failed("NotConfigured", "No Nemotron endpoint is configured, so nothing can write the check.", "Configure ApiKeys:NvidiaNIM or Token Factory.");

        var asked = "Claim: " + claim.Trim() + (context is null ? "" : "\nContext, the figures the claim rests on: " + context);
        var answer = await video.Ask(Encoding.UTF8.GetBytes(asked), ClaimSheet, new DocumentOptions { MimeType = VideoReader.TextType });
        if (!answer.Success)
            return Failed(answer.FailureName, answer.Error, answer.FailureName == "Pending"
                ? "The check is still being written; it finishes into the cache. Run the same call again, unchanged."
                : answer.Remedy);

        var code = CodeOf(answer.Text);
        var (verdict, checks, error) = code switch
        {
            null => ("unchecked", new List<Dictionary<string, object?>>(), "the answer held no ```javascript block."),
            _ when LiteralPass().IsMatch(code) => ("refused", [], "a check passes by a literal true: a check must compute its verdict."),
            _ => RunClaimCode(code, budget),
        };
        var passed = verdict == "holds";
        var failing = checks.Where(c => c["passed"] is false).ToArray();
        var summary = verdict switch
        {
            "holds" => $"Holds: {checks.Count} check{(checks.Count == 1 ? "" : "s")} passed ({string.Join("; ", checks.Take(4).Select(c => c["what"]))}).",
            "fails" => $"Fails: {string.Join("; ", failing.Take(4).Select(c => $"{c["what"]}" + (c["detail"] is string d ? $" ({Clip(d, 160)})" : "")))}.",
            _ => $"Not checked: {error}",
        } + " The check is Nemotron's code, run in the engine; read it in `code`.";

        var result = new Dictionary<string, object?>
        {
            ["ok"] = true, ["passed"] = passed, ["verdict"] = verdict, ["name"] = name, ["claim"] = claim.Trim(),
            ["checks"] = checks.Cast<object?>().ToArray(), ["code"] = code, ["error"] = error, ["summary"] = summary,
            ["model"] = answer.Provenance?.Model, ["endpoint"] = answer.Provenance?.Endpoint,
        };
        record?.Invoke("claim.verify", new Dictionary<string, object?>
        {
            ["name"] = name, ["claim"] = claim.Trim(), ["context"] = context, ["verdict"] = verdict, ["passed"] = passed,
            ["checks"] = result["checks"], ["code"] = code, ["error"] = error, ["model"] = result["model"], ["endpoint"] = result["endpoint"],
        });
        record?.Invoke("check", new Dictionary<string, object?>
        {
            ["claim"] = ClaimClaim(name), ["passed"] = passed, ["detail"] = summary, ["by"] = "verify",
        });
        return result;
    }
    #endregion

    #region Internal methods
    /// <summary>The claim a claim check records: reserved for <c>Verify</c>; <c>Stage.check</c> refuses it.</summary>
    internal static string ClaimClaim(string name) => $"{ClaimPrefix}{name}";

    internal const string ClaimPrefix = "Claim checked: ";

    /// <summary>
    /// The model's code run in its own engine: <c>holds</c> when it recorded checks and every one passed, <c>fails</c> when
    /// one did not, <c>unchecked</c> when it recorded none, <c>error</c> when it did not run to the end.
    /// </summary>
    internal (string Verdict, List<Dictionary<string, object?>> Checks, string? Error) RunClaimCode(string code, int budgetMs)
    {
        var checks = new List<Dictionary<string, object?>>();
        try
        {
            var engine = new Engine(o =>
            {
                o.Host.StringCompilationAllowed = false;
                o.Strict = true;
                o.TimeoutInterval(TimeSpan.FromMilliseconds(budgetMs));
                o.MaxStatements(ClaimStatements);
                o.LimitRecursion(64);
                o.LimitMemory(ClaimMemoryBytes);
                o.Interop.ThrowOnUnresolvedMember = true;
                // JSON.stringify asks every value for toJSON: absent means undefined, not an error.
                o.SetMemberAccessor((_, _, member) => member is "toJSON" or "then" ? JsValue.Undefined : null);
            });
            engine.SetValue("Equation", new EquationToolkit());
            engine.SetValue("Verify", new ClaimVerify(this));
            engine.SetValue("__check", new Action<string, bool, string?>((what, passed, detail) =>
            {
                if (checks.Count >= MaxClaimChecks) throw new InvalidOperationException($"a claim's code may record at most {MaxClaimChecks} checks.");
                checks.Add(new() { ["what"] = Clip(what, 200), ["passed"] = passed, ["detail"] = detail is null ? null : Clip(detail, 600) });
            }));
            engine.SetValue("rounds", new Func<double, double, JsValue?, bool>(Rounds));
            engine.SetValue("close", new Func<double, double, JsValue?, bool>(Close));
            engine.Execute(ClaimPrelude);
            engine.Execute("(function () {\n" + code + "\n})();");
        }
        catch (Exception ex)
        {
            return ("error", checks, Clip(ex.Message, 400));
        }
        return checks.Count == 0 ? ("unchecked", checks, "the code recorded no check.")
            : checks.All(c => c["passed"] is true) ? ("holds", checks, (string?)null) : ("fails", checks, (string?)null);
    }

    /// <summary>
    /// Whether <paramref name="exact"/> rounds to <paramref name="stated"/> at the stated precision: the number of decimals
    /// <paramref name="stated"/> is written with, or <paramref name="decimals"/>. 45.94 rounds to 46; 4.407 to 4.4.
    /// </summary>
    internal static bool Rounds(double exact, double stated, JsValue? decimals)
    {
        if (!double.IsFinite(exact) || !double.IsFinite(stated)) return false;
        var d = decimals is not null && decimals.IsNumber() ? (int)Math.Clamp(decimals.AsNumber(), 0, 12)
            : stated.ToString("R", CultureInfo.InvariantCulture) is var s && s.IndexOf('.') is var dot and >= 0 && !s.Contains('E') ? s.Length - dot - 1 : 0;
        return Math.Abs(Math.Round(exact, d, MidpointRounding.AwayFromZero) - stated) <= 1e-9 * Math.Max(1, Math.Abs(stated));
    }

    /// <summary>Whether two numbers agree to a relative <paramref name="tolerance"/> (1e-9).</summary>
    internal static bool Close(double a, double b, JsValue? tolerance)
    {
        var t = tolerance is not null && tolerance.IsNumber() ? Math.Abs(tolerance.AsNumber()) : 1e-9;
        return double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= t * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
    }

    /// <summary>The last <c>```javascript</c> block of an answer, or null.</summary>
    internal static string? CodeOf(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var text = Regex.Replace(answer, "<think>.*?</think>", "", RegexOptions.Singleline);
        var blocks = CodeBlock().Matches(text);
        return blocks.Count == 0 ? null : blocks[^1].Groups[1].Value.Trim();
    }
    #endregion

    #region Private methods
    private static (string Name, string? Context, int BudgetMs) ClaimOptions(object? options, string claim)
    {
        var o = JsInterop.AsDict(options);
        var name = Clip(claim.Trim().Replace('\n', ' '), 80);
        string? context = null;
        var budget = ClaimBudgetMs;
        if (o is not null)
            foreach (var key in o.Keys.Cast<object>().Select(k => k.ToString()!))
                switch (key)
                {
                    case "name": name = o[key]?.ToString()?.Trim() is { Length: > 0 } n ? n : name; break;
                    case "context":
                        context = o[key]?.ToString();
                        if (context?.Length > MaxClaimLength * 4) throw new ArgumentException($"Verify.claim's context is at most {MaxClaimLength * 4} characters: the figures the claim rests on, not the document.");
                        break;
                    case "budget": budget = (int)Math.Clamp(Convert.ToDouble(o[key], CultureInfo.InvariantCulture), 1000, MaxDerivationBudgetMs); break;
                    default: throw new ArgumentException($"Verify.claim's options are {{ name, context, budget }}; '{key}' is not one.");
                }
        return (name, context, budget);
    }

    private static string Clip(object? text, int max)
    {
        var s = text?.ToString() ?? "";
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }

    [GeneratedRegex(@"```(?:javascript|js)?[ \t]*\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    /// <summary><c>check('…', true</c>: a verdict written rather than computed.</summary>
    [GeneratedRegex(@"\bcheck\s*\(\s*(?:""[^""]*""|'[^']*'|`[^`]*`)\s*,\s*true\s*[,)]")]
    private static partial Regex LiteralPass();
    #endregion

    #region Fields
    /// <summary>What Nemotron is told: the routines, the rules, and how to answer. Measured on 22 claims before it was used.</summary>
    internal const string ClaimSheet = """
        You write a short JavaScript check, run in a sandbox, that decides whether a claim is true. You may use only:

        - Equation.equivalent(a, b?, options?) -> { verdict, at, values, detail }
          Whether two LaTeX expressions are the same mathematics, or with one argument whether a LaTeX equation lhs = rhs holds.
          verdict is 'proved' or 'agrees' (true), 'differs' (false; at is the counterexample), 'unsettled' or 'unread'.
          Understands \frac, \sqrt, powers, \sin \cos \ln e^x, \frac{d}{dx}, \int_a^b ... dx, \sum_{k=1}^{n}, \left[F\right]_a^b, |x|.
          Write LaTeX commands with their backslash (\sin, not sin) and multiply with \cdot, never *.
          options: { domain: { x: [low, high] } } when an identity holds only on part of the line.
        - Verify.derivation(steps, options?) -> { passed, pairs, summary }
          Each LaTeX step equals the one before it; two equations in a row are equivalent equations (same solutions).
        - check(what, passed, detail) records one verdict: what you checked, whether it passed, and the numbers that decided it.
        - rounds(exact, stated, decimals?) -> whether the exact value rounds to the stated figure at the precision it is written
          with (rounds(45.94, 46) and rounds(4.407, 4.4) are true).
        - close(a, b, tolerance?) -> whether two numbers agree to a relative tolerance (1e-9).
        - Plain JavaScript arithmetic and Math.*. Nothing else: no files, network, canvas or other objects.

        Rules:
        - Check the claim exactly as stated, even if it is false. Do not correct it.
        - A figure stated rounded is checked with rounds(exact, stated), never with === or a tolerance you choose.
        - Call check() once for each thing the claim asserts and nothing else: no checks of corrected or alternative values.
        - passed must be computed by your code, never written as true.
        - For a worked derivation, check every step (Verify.derivation), not only the first and last lines.
        - JavaScript strings need doubled backslashes.
        - Answer with one ```javascript code block and nothing after it.
        """;

    /// <summary>The sandbox's own code: <c>check</c> serialises what it is given before the engine records it.</summary>
    private const string ClaimPrelude = """
        function check(what, passed, detail) {
          let d = detail;
          if (d !== undefined && d !== null && typeof d !== 'string') { try { d = JSON.stringify(d); } catch (e) { d = String(d); } }
          __check(String(what), passed === true, d === undefined || d === null ? null : d);
        }
        """;

    private const int MaxClaimLength = 1000, MaxClaimChecks = 50, ClaimBudgetMs = 15000, ClaimStatements = 2_000_000;
    private const long ClaimMemoryBytes = 64L << 20;
    #endregion

    #region Types
    /// <summary>
    /// <c>Verify</c> as a claim's code sees it: the derivation check alone, unrecorded, since the claim is what is recorded.
    /// </summary>
    /// <remarks>
    /// Exposed to the claim sandbox's JavaScript. Members follow .NET naming here; Jint resolves the JS camelCase spelling
    /// onto them, so <c>Verify.derivation(...)</c> reaches <c>Derivation(...)</c>.
    /// </remarks>
    internal sealed class ClaimVerify(VerifyToolkit verify)
    {
        public Dictionary<string, object?> Derivation(object steps, object? options = null) => verify.DerivationCheck(steps, options, readBack: false);
    }
    #endregion
}
