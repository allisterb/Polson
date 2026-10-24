namespace Polson.MCPServer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>
/// The <c>Stage</c> global: lets a script declare which stage of work it is in, and add notes to the
/// run's record.
/// </summary>
/// <remarks>
/// Members are PascalCase in C#; Jint resolves the JS camelCase spelling onto them, so this is
/// <c>Stage.begin(...)</c>, <c>Stage.end()</c>, <c>Stage.current</c> and <c>Stage.note(...)</c> from
/// a script. Do not add camelCase aliases.
/// <para>
/// The stage is kept on the <see cref="SessionContext"/>, not here, so it outlives the execution
/// that set it — one stage spans many scripts, which is the entire point of tagging with it.
/// </para>
/// <para>
/// What a stage records is the agent's <em>claim</em> about its own intent, not a fact the server
/// verified. That is what makes the run legible rather than merely logged, but it also means this
/// is not evidence: the machine-recorded fields beside it — script path, duration, artifact, bytes —
/// remain the checkable half of every event.
/// </para>
/// </remarks>
public sealed class StageApi
{
    #region Constants
    /// <summary>Stage names are labels in a viewer, not prose. Long ones are truncated rather than refused.</summary>
    private const int MaxNameLength = 80;

    private const int MaxNoteLength = 2000;

    /// <summary>Continuation pages one book page's text may run onto before <c>Stage.book</c> says it is a file pasted.</summary>
    private const int MaxOverflow = 2;
    #endregion

    #region Fields
    private readonly SessionContext? session;
    private readonly RunEventLog? events;
    private readonly string? executionId;
    private readonly DrawingExecutionResult? result;
    private readonly string? projectRoot;
    #endregion

    #region Constructors
    /// <param name="result">Where this execution's checks are reported back to the caller, as they happen.</param>
    /// <param name="projectRoot">What <c>Stage.book</c> reads from and writes into, contained as <c>outFile</c> is.</param>
    public StageApi(SessionContext? session, RunEventLog? events, string? executionId, DrawingExecutionResult? result = null,
        string? projectRoot = null)
    {
        this.session = session;
        this.events = events;
        this.executionId = executionId;
        this.result = result;
        this.projectRoot = projectRoot;
    }
    #endregion

    #region Properties
    /// <summary>The stage currently in effect, or null if none has been declared.</summary>
    public string? Current => session?.Stage;

    /// <summary>
    /// Minutes since this run began, or null outside a session. Read as <c>Stage.elapsedMinutes</c>.
    /// </summary>
    /// <remarks>
    /// **The sandbox has `Date.now()` and `mina.time()`, and both answer "now" rather than "since
    /// when".** Without this the only way to pace a deadline was for the agent to stamp its own
    /// start — `Session.startedAt ??= Date.now()` — which measures from whenever it first ran a
    /// script, not from when the run began. That understates, silently, and returns a number that
    /// looks like an answer: an agent that spends twelve minutes reading the brief and the manuals
    /// before its first execution stamps minute twelve as zero. Two runs of one brief differed by
    /// four scripts before the first render, so the same recipe gave them very different baselines
    /// for the same true elapsed time.
    /// <para>
    /// Anchored on <see cref="SessionContext.StartedUtc"/>, which the server has known all along and
    /// never told anyone. Null rather than zero when there is no session, matching
    /// <see cref="Current"/>: outside a run there is no elapsed time, and zero would be a claim.
    /// </para>
    /// </remarks>
    public double? ElapsedMinutes => session is null
        ? null
        : (DateTimeOffset.UtcNow - session.StartedUtc).TotalMinutes;
    #endregion

    #region Methods
    /// <summary>
    /// Declares the stage of work. Persists across executions until changed or ended.
    /// </summary>
    /// <remarks>
    /// Beginning a <em>different</em> stage while one is open closes the previous one first, so the
    /// record never shows two stages running at once and a viewer can treat the events between begin
    /// and end as a contiguous group.
    /// <para>
    /// Re-declaring the stage you are already in is an announcement, not a transition: it records a
    /// <c>stage.continue</c> and leaves the stage running. The first agent run declared
    /// <c>Stress test</c> at the top of three consecutive scripts — reasonably, since that is where
    /// it said what it was about to do — and produced an end/begin pair each time, chopping one
    /// stage into three and burying the real transitions among them.
    /// </para>
    /// <para>
    /// The comparison ignores case so <c>Stress Test</c> continues <c>Stress test</c> rather than
    /// forking the record over a capital letter; the originally recorded spelling is kept and
    /// returned.
    /// </para>
    /// </remarks>
    public string Begin(string name)
    {
        var clean = Clean(name, MaxNameLength);
        if (clean.Length == 0) throw new ArgumentException("A stage needs a name.", nameof(name));

        var current = session?.Stage;

        if (current is { Length: > 0 } && string.Equals(current, clean, StringComparison.OrdinalIgnoreCase))
        {
            events?.Append("stage.continue", current, executionId);
            return current;
        }

        if (current is { Length: > 0 })
        {
            events?.Append("stage.end", current, executionId, new Dictionary<string, object?> { ["reason"] = "superseded" });
        }

        if (session is not null) session.Stage = clean;
        events?.Append("stage.begin", clean, executionId);

        return clean;
    }

    /// <summary>
    /// The run's process book: a multi-page PDF of the work in order — the brief, the concepts, the boards, the frames, the
    /// finished piece — written to <paramref name="filePath"/>. <c>options</c>: <c>{ title, pages }</c>, each page
    /// <c>{ title?, image?, caption?, text?, file? }</c>, <c>image</c> and <c>file</c> project paths (a still, and a text or
    /// Markdown file such as <c>concepts.md</c>). Returns <c>{ path, pages, bytes }</c>.
    /// </summary>
    /// <remarks>
    /// What a studio hands its client beside the work, and what the run hands its reader: Lupton's concept presentation,
    /// sequential, simple and headed on every page (<i>Graphic Design Thinking</i>, pp. 108–109). It assembles what the run
    /// already made; it draws nothing new.
    /// </remarks>
    public Dictionary<string, object?> Book(string filePath, object? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Stage.book writes a PDF: name it '….pdf', not '{filePath}'.");
        var o = JsInterop.AsDict(options) ?? throw new ArgumentException("Stage.book needs { title, pages: [{ title, image, caption, text, file }] }.");
        var title = Clean(o["title"]?.ToString() ?? "", MaxNameLength);
        if (title.Length == 0) throw new ArgumentException("Stage.book needs a title: the project, as every page's header names it.");
        if (o["pages"] is not System.Collections.IEnumerable given || o["pages"] is string)
            throw new ArgumentException("Stage.book's pages is a list: [{ title, image, caption, text, file }].");

        var pages = new List<Polson.Drawing.Skia.PdfBook.Page>();
        var warnings = new List<string>();
        foreach (var item in given)
        {
            var p = JsInterop.AsDict(item) ?? throw new ArgumentException("Each of Stage.book's pages is an object: { title, image, caption, text, file }.");
            foreach (var key in p.Keys.Cast<object>().Select(k => k.ToString()!))
                if (key is not ("title" or "image" or "caption" or "text" or "file"))
                    throw new ArgumentException($"Stage.book: a page takes title, image, caption, text and file, not '{key}'.");
            byte[]? image = null;
            if (p["image"]?.ToString() is { Length: > 0 } imagePath)
            {
                var full = ProjectPath.Resolve(projectRoot, imagePath, "image", "Read");
                if (!File.Exists(full)) throw new ArgumentException($"Stage.book: '{imagePath}' is not in the project. Name a still you rendered, e.g. 'artifacts/03_concepts_a.png'.");
                // The book is the client's, and a research sheet shows other people's posters and logos.
                if (projectRoot is not null && events?.IsResearch(Path.GetRelativePath(projectRoot, full)) == true)
                    throw new ArgumentException($"Stage.book: '{imagePath}' is a research sheet of Qloo's pictures — other people's posters and logos — " +
                        "and the process book is the client's. Show the research board instead, e.g. 'artifacts/01_field_a.png' (Manual 29 §2a).");
                if (!System.Text.RegularExpressions.Regex.IsMatch(full, @"\.(png|jpe?g|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    throw new ArgumentException($"Stage.book's pages show stills (.png, .jpg, .webp), not '{imagePath}'. For a film, render a frame of it.");
                image = File.ReadAllBytes(full);
            }
            var text = p["text"]?.ToString();
            if (p["file"]?.ToString() is { Length: > 0 } filePart)
            {
                var full = ProjectPath.Resolve(projectRoot, filePart, "file", "Read");
                if (!File.Exists(full)) throw new ArgumentException($"Stage.book: '{filePart}' is not in the project.");
                text = (text is null ? "" : text + "\n\n") + File.ReadAllText(full);
                // A test run's findings are its report to the studio about the studio: friction, gaps, defects. A client's
                // book that carries them hands over the workshop's notes with the work (nightshift1: 17 of 46 pages).
                if (Path.GetFileName(full).Equals("findings.md", StringComparison.OrdinalIgnoreCase))
                    warnings.Add("findings.md is the run's report to the studio, not part of the work: leave it out of the book.");
            }
            pages.Add(new(p["title"]?.ToString(), image, p["caption"]?.ToString(), text));
        }
        if (pages.Count is 0 or > 60) throw new ArgumentException($"Stage.book takes 1 to 60 pages, not {pages.Count}.");

        var (pdf, count, overflows) = Polson.Drawing.Skia.PdfBook.Write(title, pages);
        // A page whose text runs on for pages is a file pasted, not a page written: nightshift1 put the whole of
        // direction.md in, ten pages of it. Summarise it in a page of your own — the decisions, each with its evidence.
        for (var i = 0; i < pages.Count; i++)
            if (overflows[i] > MaxOverflow)
                warnings.Add($"'{pages[i].Title ?? $"page {i + 1}"}' runs on for {overflows[i] + 1} pages: a book page is read at a glance. "
                    + "Write its substance as a page of text — the decisions and their evidence — rather than passing the whole file.");
        foreach (var w in warnings) result?.Logs.Add("[WARN] Stage.book: " + w);
        var output = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, pdf);
        events?.Append("render", session?.Stage, executionId, new Dictionary<string, object?>
        {
            ["artifact"] = events.Relativize(output),
            ["format"] = "pdf",
            ["bytes"] = pdf.Length,
            ["pages"] = count,
            ["warnings"] = warnings.Count == 0 ? null : warnings.ToArray(),
        });
        return new()
        {
            ["path"] = filePath, ["pages"] = count, ["bytes"] = (long)pdf.Length,
            ["warnings"] = warnings.Cast<object?>().ToList(),
        };
    }

    /// <summary>Ends the current stage. Harmless when none is open.</summary>
    public void End()
    {
        if (session?.Stage is not { Length: > 0 } current) return;

        events?.Append("stage.end", current, executionId);
        session.Stage = null;
    }

    /// <summary>
    /// Records a note in the run's record, tagged with the current stage.
    /// </summary>
    /// <remarks>
    /// For the reasoning that would otherwise be lost — why a direction was abandoned, what a render
    /// was meant to test. <c>log(...)</c> reaches only the caller of this one tool call; a note
    /// persists into the run directory and is what a reader sees afterwards.
    /// <para>
    /// <c>{ once: true }</c> writes the note only the first time its text is noted in this stage. A film kept as one
    /// script runs many times, so a note inside it repeated on every run, and trailer1 wrote its reasons in a separate
    /// script after the fact instead.
    /// </para>
    /// </remarks>
    public void Note(string message, object? options = null)
    {
        var once = NoteOptions(options);
        var clean = Clean(message, MaxNoteLength);
        if (clean.Length == 0) return;
        if (once && session is not null && !session.NotedOnce.Add((session.Stage, clean))) return;

        events?.Append("note", session?.Stage, executionId, new Dictionary<string, object?> { ["message"] = clean });
    }

    private static bool NoteOptions(object? options)
    {
        if (options is null) return false;
        var dict = JsInterop.AsDict(options)
            ?? throw new ArgumentException("Stage.note's second argument is an options object: { once: true }.");
        foreach (var key in dict.Keys)
            if (key?.ToString() is not "once")
                throw new ArgumentException($"Stage.note has no option '{key}'. It takes once (true to write the note only the first time its text is noted in this stage).");
        return dict["once"] switch
        {
            null => false,
            bool b => b,
            _ => throw new ArgumentException("Stage.note: once is true or false.")
        };
    }

    /// <summary>
    /// Records what you expect the next render to show, <em>before</em> you make it.
    /// </summary>
    /// <remarks>
    /// The record carries actions and, since <see cref="ProbeScope"/>, what measurements found. What it
    /// cannot carry on its own is what anybody wanted — so a run that measured and was satisfied and a
    /// run that measured, found the value wrong and redrew four times leave almost the same trace.
    /// <para>
    /// A claim written down first is the difference. It is worth stating as something checkable — "the
    /// accent should be under 15% of the frame", "the horizon within 4px of y=380" — because the point
    /// is to be able to be wrong about it.
    /// </para>
    /// </remarks>
    public void Expect(string claim)
    {
        var clean = Clean(claim, MaxNoteLength);
        if (clean.Length == 0) return;

        events?.Append("expect", session?.Stage, executionId, new Dictionary<string, object?> { ["claim"] = clean });
    }

    /// <summary>
    /// Records the verdict on a claim, and returns <paramref name="passed"/> so it can be branched on.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="Expect"/>, and usable without it — most checks are stated and
    /// settled in the same script. Returning the verdict means the call reads as the test it is:
    /// <c>if (!Stage.check('accent under 15%', share &lt; 0.15)) { … }</c>.
    /// <para>
    /// A failing check is not a failing run. It is the most useful thing a record can contain: a stage
    /// that states four checks and passes three has said precisely where it stands, which is more than
    /// a stage full of renders and no claims can say at any length.
    /// </para>
    /// </remarks>
    /// <param name="detail">What was measured. An options object may stand here instead when there is no detail.</param>
    /// <param name="options">
    /// <c>{ accepted: 'reason' }</c>: a failing check that is right about the drawing, kept on purpose. It is recorded
    /// as accepted with the reason, counted apart from passes and failures, and returns true, since there is nothing
    /// left to fix. <c>{ judged: true }</c>: the verdict is a reading of a picture, not a measurement. It still passes
    /// or fails, and is marked so a reader can tell the two kinds apart.
    /// </param>
    public bool Check(string claim, bool passed, object? detail = null, object? options = null)
    {
        // No guard for swapped arguments here: Jint's overload resolution already refuses
        // `check(boolean, string)` before the call reaches this method, so a check that could not
        // fail never gets recorded. What was wrong was the *message* — see ArgumentHelp in
        // JsDrawingEngine, which now names the swap instead of advising a hunt for a typo. A guard
        // on this side would be dead code and would also refuse the legitimate claim "true".
        if (detail is not null and not string && JsInterop.AsDict(detail) is not null && options is null)
            (detail, options) = (null, detail);
        var (reason, judged) = CheckOptions(options);

        var clean = Clean(claim, MaxNoteLength);
        if (clean.Length == 0) return passed;

        // **A read-back's verdict is the read-back's to record.** A live run dropped its Verify.image call and wrote the
        // claim itself as passed, over a check that had failed; the studio would have shown the pass.
        if (clean.StartsWith(VerifyToolkit.ReadBackPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Stage.check: a claim beginning '{VerifyToolkit.ReadBackPrefix}' is recorded by Verify.image and "
                + "Verify.film themselves, from what the reader read. Run the check; do not write its verdict.");
        if (clean.StartsWith(VerifyToolkit.ClaimPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Stage.check: a claim beginning '{VerifyToolkit.ClaimPrefix}' is recorded by Verify.claim itself, "
                + "from the code that checked it. Run the check; do not write its verdict.");
        if (clean.StartsWith(VerifyToolkit.DerivationPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Stage.check: a claim beginning '{VerifyToolkit.DerivationPrefix}' is recorded by Verify.derivation "
                + "itself, from the mathematics. Run the check; do not write its verdict.");

        // **A failure kept on purpose is its own state.** The finder that reports two fists on one rope is right;
        // the drawing wants them there. Without a third state the record showed a red check and the reason lived in
        // a note somewhere else, so a reader saw a failure the run had already decided about.
        var accepted = !passed && reason is not null;
        var fields = new Dictionary<string, object?>
        {
            ["claim"] = clean,
            ["passed"] = passed
        };

        var cleanDetail = Clean(detail?.ToString(), MaxNoteLength);
        if (cleanDetail.Length > 0) fields["detail"] = cleanDetail;

        // **A verdict from looking is not a measurement, and the record has to say which.** A live animation run
        // recorded eleven per-beat critiques as checks; they read exactly like its arc and contact measurements,
        // though each was its own reading of a contact sheet.
        if (judged) fields["judged"] = true;
        var cleanReason = accepted ? Clean(reason, MaxNoteLength) : string.Empty;
        if (accepted)
        {
            fields["accepted"] = true;
            fields["reason"] = cleanReason;
        }

        events?.Append("check", session?.Stage, executionId, fields);

        // **The verdict goes back to the caller too.** It used to reach only the run record, so a failing check was
        // invisible until the run was over - backwards for a workflow shaped draw, measure, correct. One run wrote
        // nineteen checks and could see none of them for three stages, until it wrapped Stage.check in its own log.
        var verdict = passed ? "PASS" : accepted ? "ACCEPTED" : "FAIL";
        result?.Logs.Add($"[CHECK] {verdict}{(judged ? " (judged)" : "")} {clean}" + (cleanDetail.Length > 0 ? $" - {cleanDetail}" : "")
                         + (accepted ? $" (kept: {cleanReason})" : ""));
        result?.RecordCheck(clean, passed, cleanDetail.Length > 0 ? cleanDetail : null, accepted ? cleanReason : null, judged);
        return passed || accepted;
    }

    /// <summary>
    /// The reason in <c>{ accepted: 'reason' }</c>, or null, and whether <c>{ judged: true }</c> was given. Anything
    /// else in the options is refused by name.
    /// </summary>
    private static (string? Reason, bool Judged) CheckOptions(object? options)
    {
        if (options is null) return (null, false);
        var dict = JsInterop.AsDict(options)
            ?? throw new ArgumentException("Stage.check's fourth argument is an options object, such as { accepted: 'why this failure is kept' } or { judged: true }.");
        foreach (var key in dict.Keys)
            if (key?.ToString() is not ("accepted" or "judged"))
                throw new ArgumentException($"Stage.check has no option '{key}'. It takes accepted (the reason a failing check is kept) and judged (true when the verdict is a reading of a picture, not a measurement).");

        var judged = dict.Contains("judged") && dict["judged"] is not null && (dict["judged"] is bool b
            ? b
            : throw new ArgumentException("Stage.check: judged is true or false: whether the verdict is your reading of a picture rather than a measurement."));

        if (!dict.Contains("accepted") || dict["accepted"] is null) return (null, judged);
        return dict["accepted"] is string text && text.Trim().Length > 0
            ? (text, judged)
            : throw new ArgumentException("Stage.check: accepted takes the reason, as a sentence, not true. The reason is what a reader of the record needs.");
    }

    /// <summary>Strips the characters that would corrupt a one-line JSON record, and caps the length.</summary>
    /// <remarks>
    /// A stage name can be influenced by a client brief the agent read, so it gets the same treatment
    /// as any other text arriving from outside: newlines and controls out, invisible characters out,
    /// length bounded.
    /// </remarks>
    private static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v < 0x20 || v is >= 0x7F and <= 0x9F) continue;                     // controls, newlines included
            if (v is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069) continue;   // bidi overrides
            if (v is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF) continue;       // zero-width
            if (v is >= 0xE0000 and <= 0xE007F) continue;                            // tag block
            sb.Append(rune);
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd() + "…";
    }
    #endregion
}
