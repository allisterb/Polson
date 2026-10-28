"""`verification.md`: every check the run made, in the order it made them, written by the studio from the run record.

The Verification view shows each check's **latest** verdict, which is the right summary and the wrong record: a check
that failed and was then fixed shows only its pass, so the work of getting there disappears. cruelty1 recorded 137
checks; the disclaimer that held 3.54 s where 3.83 s were needed, the title outside the title-safe area and a broken
DO were all failures it fixed, and none of them was visible after it.

**Written from the record, never by the agent.** Every line below is an event the engine wrote when the check ran:
`check` from `Stage.check`, `film.verify` / `image.verify` from the read-backs, their strays as settled, and the
documents read. An agent once dropped a failing read-back and wrote its claim as passed; a document it wrote itself
could do the same, and this one cannot. It is made when asked for, so it is always the record as it stands, and it is
never a file in the project: an agent's own `verification.md`, if one existed, is not what the page serves.

**What it does not hold**, said in the document too: checks the agent made in its head and never recorded. A line
translated, read back, found wrong and rewritten through `Documents.ask` is in `direction.md`, not here, unless the run
recorded it with `Stage.check`.
"""

from __future__ import annotations

import json
from collections import defaultdict
from typing import Any, Iterable

#: The name it is offered and served under, beside the documents the agent writes.
NAME = "verification.md"
TITLE = "Verification"
WHAT = "every check the run made, failures and their fixes included, written by the studio from the run record"

#: Detail longer than this is cut in the full list; the failures section keeps it whole.
LIST_DETAIL = 160


def document(events: Iterable[dict[str, Any]], since: str | None = None) -> str | None:
    """The document for one run's events, or None when the run recorded nothing to verify."""
    scoped = [e for e in events if not (since and (e.get("ts") or "") < since)]
    checks = [e for e in scoped if e.get("type") == "check" and e.get("claim")]
    reads = [e for e in scoped if e.get("type") in ("film.verify", "image.verify")]
    others = [e for e in scoped if e.get("type") in ("derivation.verify", "claim.verify")]
    if not (checks or reads or others):
        return None

    out = ["# Verification", "",
           "Written by the studio from the run record (`events/server.jsonl`), not by the agent: every verdict below is "
           "the one the check recorded when it ran, in the order the run made them. Checks the agent made without "
           "recording them are not here — a translation it read back and rewrote through `Documents.ask` is in "
           "`direction.md` unless it was recorded with `Stage.check`.", ""]
    out += _summary(checks, reads, scoped)
    out += _failures(checks)
    out += _read_backs(reads, scoped)
    out += _others(others)
    out += _documents(scoped)
    out += _every_check(checks)
    return "\n".join(out).rstrip() + "\n"


def _summary(checks: list[dict[str, Any]], reads: list[dict[str, Any]], events: list[dict[str, Any]]) -> list[str]:
    final = _latest(checks)
    failed_once = {c["claim"] for c in checks if not c.get("passed") and not c.get("accepted")}
    still = [c for c in final.values() if not c.get("passed") and not c.get("accepted")]
    kept = [c for c in final.values() if c.get("accepted")]
    lines = ["## Summary", ""]
    if checks:
        fixed = len(failed_once - {c["claim"] for c in still})
        lines.append(f"- **Checks:** {len(checks)} recorded, on {len(final)} claims. {len(failed_once)} failed at least once "
                     f"and {fixed} of those passed later; {len(still)} {'is' if len(still) == 1 else 'are'} still failing at the end"
                     + (f", and {len(kept)} {'was' if len(kept) == 1 else 'were'} kept on purpose with a reason." if kept else "."))
    pieces = {(e.get("video") or e.get("image")) for e in reads}
    if reads:
        strays = [s for s in _settled_strays(reads, events)]
        real = sum(1 for s in strays if s["verdict"] == "real")
        misread = sum(1 for s in strays if s["verdict"] == "misread")
        unsettled = sum(1 for s in strays if s["verdict"] not in ("real", "misread"))
        lines.append(f"- **Read-backs:** {len(reads)} of {len(pieces)} piece{'s' if len(pieces) != 1 else ''}, the latest of each below. "
                     f"Strays (text the reader reported that the piece does not set): {real} real, {misread} ruled a "
                     f"misreading, {unsettled} never settled.")
    stamp = _times(events)
    if stamp:
        lines.append(f"- **Recorded:** {stamp}.")
    return lines + [""]


def _failures(checks: list[dict[str, Any]]) -> list[str]:
    """Every claim that failed, in the order it first failed: when it failed, what the first failure measured, and what
    became of it — passed later, kept on purpose, or still failing. A claim that failed three times before passing is
    one entry, not three."""
    failed: dict[str, list[dict[str, Any]]] = {}
    for c in checks:
        if not c.get("passed"):
            failed.setdefault(c["claim"], []).append(c)
    if not failed:
        return ["## Checks that failed", "", "None: every recorded check passed when it ran.", ""]
    lines = ["## Checks that failed", "", "Each claim that failed, with what its first failure measured and what became of it.", ""]
    for claim, fails in failed.items():
        times = ", ".join(_clock(f) for f in fails)
        lines.append(f"- **{_md(claim)}** — {_stage(fails[0])}; failed {len(fails)} time{'s' if len(fails) > 1 else ''} ({times})")
        if fails[0].get("detail") is not None:
            lines.append(f"  - Measured: {_md(_text(fails[0]['detail']))}")
        last = _latest([c for c in checks if c["claim"] == claim])[claim]
        if last.get("accepted"):
            lines.append(f"  - Kept on purpose: {_md(_text(last.get('reason') or last.get('accepted')))}")
        elif last.get("passed"):
            lines.append(f"  - Passed{_time(last)}: {_md(_short(last.get('detail')))}")
        else:
            lines.append("  - **Still failing at the end of the run.**")
    return lines + [""]


def _read_backs(reads: list[dict[str, Any]], events: list[dict[str, Any]]) -> list[str]:
    """Per film or image: every attempt's counts, the latest attempt's texts set against what was read, and its strays."""
    if not reads:
        return []
    by_piece: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for e in reads:
        by_piece[e.get("video") or e.get("image") or "?"].append(e)
    verdicts = _stray_verdicts(events)
    frames = _frame_owners(reads)
    lines = ["## Read-backs", "",
             "A separate model watched each film (or read each image) and reported every text it saw. A text it read "
             "differently is shown beside the text as set, whatever the check concluded, so a reader can judge the match.",
             ""]
    for piece, attempts in by_piece.items():
        latest = attempts[-1]
        lines += [f"### `{piece}`", ""]
        for n, a in enumerate(attempts, 1):
            counts = a.get("counts") or {}
            said = ", ".join(f"{v} {k}" for k, v in counts.items() if isinstance(v, (int, float)) and v)
            lines.append(f"- Attempt {n}{_time(a)}, read by {a.get('model') or 'a model'}: {said or 'nothing counted'}")
        lines.append("")
        items = latest.get("items") or []
        if items:
            lines += ["| Text as set | On screen | Source | Read as | Result |", "| :-- | :-- | :-- | :-- | :-- |"]
            for it in items:
                read = it.get("readAs")
                differs = bool(read) and " ".join(str(read).split()) != " ".join(str(it.get("text") or "").split())
                result = str(it.get("status") or "")
                if differs and result == "seen":
                    result = "seen (read differently)"
                when = (f"{it['from']:.1f}–{it['to']:.1f} s" if isinstance(it.get("from"), (int, float))
                        and isinstance(it.get("to"), (int, float)) else "")
                lines.append(f"| {_cell(it.get('text'))} | {when} | {_cell(it.get('source'))} | "
                             f"{_cell(read) if differs else ''} | {result} |")
            lines.append("")
        strays = latest.get("strays") or []
        for s in strays:
            verdict = verdicts.get((piece, s.get("n")), s.get("verdict"))
            at = f" at {s['at']} s" if s.get("at") is not None else ""
            lines.append(f"- **Stray {s.get('n')}**{at} ({s.get('kind') or 'unlisted'}): read as “{_md(_oneline(s.get('read')))}”")
            frame = s.get("frame")
            if frame:
                owner = frames.get(frame)
                note = (f" — this file was overwritten by a later check of `{owner}`, so it no longer shows this stray"
                        if owner and owner != piece else "")
                lines.append(f"  - Frame: {frame}{note}")
            ruling = verdict.get("verdict") if isinstance(verdict, dict) else verdict
            why = verdict.get("note") if isinstance(verdict, dict) else None
            lines.append(f"  - Ruled: **{ruling or 'never settled'}**" + (f" — {_md(why)}" if why else
                                                                          " — no reason given" if ruling else ""))
        if strays:
            lines.append("")
    return lines


def _others(events: list[dict[str, Any]]) -> list[str]:
    if not events:
        return []
    lines = ["## Derivations and claims", ""]
    for e in events:
        kind = "Derivation" if e["type"] == "derivation.verify" else "Claim"
        verdict = "passed" if e.get("passed") else "failed"
        what = e.get("claim") or e.get("name")
        lines.append(f"- {kind} **{_md(_text(what))}**{_time(e)}: {verdict}" + (f" ({e['verdict']})" if e.get("verdict") else ""))
    return lines + [""]


def _documents(events: list[dict[str, Any]]) -> list[str]:
    reads: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for e in events:
        if e.get("type") == "document.read" and e.get("descriptor"):
            reads[e["descriptor"]].append(e)
    if not reads:
        return []
    lines = ["## Documents read", "", "| Document | Reads | Read by | Failed |", "| :-- | --: | :-- | --: |"]
    for name, rs in reads.items():
        models = sorted({str(r.get("model")) for r in rs if r.get("model")})
        failed = sum(1 for r in rs if not r.get("success"))
        lines.append(f"| {_cell(name)} | {len(rs)} | {_cell(', '.join(models))} | {failed} |")
    return lines + [""]


def _every_check(checks: list[dict[str, Any]]) -> list[str]:
    if not checks:
        return []
    lines = ["## Every check, in order", "", "| Time | Stage | Claim | Result | Measured |", "| :-- | :-- | :-- | :-- | :-- |"]
    for c in checks:
        result = "pass" if c.get("passed") else "kept" if c.get("accepted") else "**fail**"
        if c.get("judged"):
            result += " (judged)"
        lines.append(f"| {_clock(c)} | {_cell(c.get('stage'))} | {_cell(c['claim'])} | {result} | {_cell(_short(c.get('detail')))} |")
    return lines + [""]


# region Helpers
def _latest(checks: list[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    out: dict[str, dict[str, Any]] = {}
    for c in checks:
        out.pop(c["claim"], None)
        out[c["claim"]] = c
    return out


def _stray_verdicts(events: list[dict[str, Any]]) -> dict[tuple[str, Any], dict[str, Any]]:
    """The latest ruling on each stray, by piece and number."""
    out: dict[tuple[str, Any], dict[str, Any]] = {}
    for e in events:
        if e.get("type") in ("film.stray", "image.stray"):
            out[(e.get("video") or e.get("image"), e.get("n"))] = {"verdict": e.get("verdict"), "note": e.get("note")}
    return out


def _settled_strays(reads: list[dict[str, Any]], events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Each piece's latest strays with their rulings, for the summary's counts."""
    latest: dict[str, dict[str, Any]] = {}
    for e in reads:
        latest[e.get("video") or e.get("image")] = e
    verdicts = _stray_verdicts(events)
    return [{"verdict": (verdicts.get((piece, s.get("n"))) or {}).get("verdict") or s.get("verdict")}
            for piece, e in latest.items() for s in e.get("strays") or []]


def _frame_owners(reads: list[dict[str, Any]]) -> dict[str, str]:
    """Which piece's check last wrote each stray frame file: they are reused across pieces, so the file shows that one's."""
    owners: dict[str, str] = {}
    for e in reads:
        for s in e.get("strays") or []:
            if s.get("frame"):
                owners[s["frame"]] = e.get("video") or e.get("image")
    return owners


def _times(events: list[dict[str, Any]]) -> str:
    stamps = sorted(e["ts"] for e in events if e.get("ts"))
    return f"{stamps[0][:16].replace('T', ' ')} to {stamps[-1][11:16]} UTC" if stamps else ""


def _clock(e: dict[str, Any]) -> str:
    return (e.get("ts") or "")[11:19]


def _time(e: dict[str, Any]) -> str:
    return f" at {_clock(e)}" if e.get("ts") else ""


def _stage(e: dict[str, Any]) -> str:
    return f"stage {e['stage']}" if e.get("stage") else "no stage"


def _text(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False)
    return str(value)


def _short(value: Any) -> str:
    text = _oneline(value)
    return text if len(text) <= LIST_DETAIL else text[:LIST_DETAIL].rstrip() + "…"


def _oneline(value: Any) -> str:
    return " / ".join(line.strip() for line in _text(value).splitlines() if line.strip())


def _md(value: Any) -> str:
    """Prose from the record, on one line. The page renders Markdown with raw HTML off, so `<` needs no escaping."""
    return _oneline(value)


def _cell(value: Any) -> str:
    """A table cell: one line, pipes escaped."""
    return _md(value).replace("|", "\\|")
# endregion
