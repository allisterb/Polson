"""*Why it looks like this*: a run's `direction.md`, read as choices and the evidence behind them.

The poster workflow writes one file, `direction.md`, in which every visual choice sits beside its evidence and
the kind of that evidence — Qloo, research, the brief, or an answer the agent assumed. That file is what a judge
of a taste-grounded poster needs, and as Markdown it is a wall of pipes. This reads its tables into rows the run
page can lay out, and links each `t5`-style reference to the Taste query the run actually made, so a Qloo line
shows the request behind it rather than asking to be taken on trust (design doc §4.6).

**The file is agent-written and therefore untrusted.** Nothing here interprets it beyond splitting tables: cells
come back as plain strings, and the page escapes them before it formats anything.
"""

from __future__ import annotations

import re
from collections.abc import Iterable
from typing import Any

#: A direction file is a page of tables, not a book. Anything longer is truncated, said so, and still served.
MAX_DIRECTION = 200_000

#: Kinds the page has a badge for, by the stem that names each, in the order a reader weighs them. A stem
#: rather than a word, because the agent writes "Assumed", "assumption", "Judgement on the render", "judgment".
KINDS = (("qloo", "qloo"), ("research", "research"), ("brief", "brief"), ("assum", "assumed"),
         ("check", "check"), ("judg", "judgement"), ("workaround", "workaround"))

#: A Taste result's handle as the agent cites it: `t5`, `t12`. Bounded so `t2026` in prose is not one.
TASTE_REF = re.compile(r"(?<![\w-])t(\d{1,3})(?![\w-])")

#: A section heading, `##` to `####`; the document's own `#` title is not a section.
_HEADING = re.compile(r"^(#{2,4})\s+(.*\S)\s*$")

#: The row under a table's header: dashes and optional alignment colons between pipes.
_SEPARATOR = re.compile(r"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")


def cells(line: str) -> list[str]:
    """A Markdown table row's cells, honouring `\\|` and pipes inside backticks."""
    out, cell, code, i = [], [], False, 0
    text = line.strip()
    if text.startswith("|"):
        text = text[1:]
    if text.endswith("|") and not text.endswith("\\|"):
        text = text[:-1]
    while i < len(text):
        ch = text[i]
        if ch == "\\" and i + 1 < len(text) and text[i + 1] == "|":
            cell.append("|")
            i += 2
            continue
        if ch == "`":
            code = not code
        if ch == "|" and not code:
            out.append("".join(cell).strip())
            cell = []
        else:
            cell.append(ch)
        i += 1
    out.append("".join(cell).strip())
    return out


def kinds(cell: str) -> list[dict[str, str]]:
    """The kinds a row's last cell names — `Qloo / Brief` is two — each as a badge key and its words."""
    found = []
    for part in re.split(r"\s*[/,;+]\s*", cell):
        words = part.strip()
        if not words:
            continue
        low = words.lower()
        key = next((key for stem, key in KINDS if stem in low), "other")
        found.append({"key": key, "label": words})
    return found


def parse(text: str) -> list[dict[str, Any]]:
    """Sections of rows from every table whose last column is *Kind*, under the heading it sits beneath.

    A section also carries its **lead** — the first paragraph under the heading, before the table — because
    that is where the agent says which profile a section rests on, and a row out of that context loses it.
    Tables without a *Kind* column, and bullet lists, are left to the document itself, which the page links.
    """
    sections: list[dict[str, Any]] = []
    title, lead, lead_open = "", "", True
    lines = text.splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        heading = _HEADING.match(line)
        if heading:
            title, lead, lead_open = heading.group(2), "", True
            i += 1
            continue

        if line.lstrip().startswith("|") and i + 1 < len(lines) and _SEPARATOR.match(lines[i + 1].strip()):
            header = [c.lower() for c in cells(line)]
            i += 2
            body = []
            while i < len(lines) and lines[i].lstrip().startswith("|"):
                body.append(cells(lines[i]))
                i += 1
            lead_open = False
            if len(header) >= 3 and header[-1] == "kind":
                rows = [_row(r) for r in body if len(r) >= 2 and any(r)]
                if rows:
                    sections.append({"title": title, "lead": lead, "leadRefs": refs(lead),
                                     "columns": header[:-1], "rows": rows})
            continue

        stripped = line.strip()
        if lead_open and stripped and not stripped.startswith(("-", "*", ">", "|")):
            lead = f"{lead} {stripped}".strip() if lead else stripped
        elif lead_open and not stripped and lead:
            lead_open = False
        i += 1
    return sections


def _row(cells_: list[str]) -> dict[str, Any]:
    choice, kind = cells_[0], cells_[-1] if len(cells_) >= 3 else ""
    evidence = " — ".join(c for c in cells_[1:-1] if c) if len(cells_) >= 3 else cells_[1]
    return {"choice": choice, "evidence": evidence, "kinds": kinds(kind), "refs": refs(f"{choice} {evidence}")}


def refs(text: str) -> list[str]:
    """The Taste handles a cell cites, in order of first mention: `t5`, `t9`."""
    seen: list[str] = []
    for match in TASTE_REF.finditer(text):
        handle = f"t{int(match.group(1))}"
        if handle not in seen:
            seen.append(handle)
    return seen


def queries(events: Iterable[dict[str, Any]], since: str | None = None) -> dict[str, dict[str, Any]]:
    """The run's Taste queries by handle, from the server's record: what each was for and what it asked.

    Scoped to the run as the curve is, since a project's record outlives any one run and a handle like `t5`
    is reused by the next session. The request is Qloo's endpoint and canonical query — never the key, which the
    server never records.
    """
    found: dict[str, dict[str, Any]] = {}
    for event in events:
        if event.get("type") != "taste.query" or not event.get("id"):
            continue
        if since and (event.get("ts") or "") < since:
            continue
        found[event["id"]] = {
            "op": event.get("op") or "",
            "label": event.get("label") or "",
            "request": event.get("request") or "",
            "requests": event.get("requests"),
            "fromCache": bool(event.get("fromCache")),
        }
    return found


def explain(text: str, events: Iterable[dict[str, Any]], since: str | None = None) -> dict[str, Any]:
    """The panel's whole payload: sections, the queries their rows cite, and whether the file was cut short."""
    truncated = len(text) > MAX_DIRECTION
    sections = parse(text[:MAX_DIRECTION])
    cited = {h for s in sections for r in s["rows"] for h in r["refs"]} | {h for s in sections for h in s["leadRefs"]}
    known = queries(events, since)
    return {
        "sections": sections,
        "queries": {h: q for h, q in known.items() if h in cited},
        "truncated": truncated,
    }
