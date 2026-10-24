"""Research sheets: renders of Qloo's pictures, which the studio never shows.

`Taste.images` fetches Qloo's own picture of each entity an answer named: a film's poster, a brand's logo. An agent
looks at them on a sheet of its own (Manual 29 §2a), and that sheet is other people's work, with real faces on the
posters. Qloo's Terms of Use §8 forbid displaying a logo that appears in its service without its say-so. So a run's
research sheet stays with the agent: never served, never in the stream the page draws from, never the finished piece.
What the client sees instead is the research board, which carries no picture.

**One rule, by execution**, the one `RunEventLog.IsResearch` applies on the .NET side: the engine records
`taste.pictures` the first time a script takes a Qloo picture's pixels, so every render that script made is research,
whatever it is called. It cannot catch a legitimate render, because no other script takes those pixels.

**Applied only where the record is already being read**: the live stream, the finished piece and a request's
thumbnail. The artifact route does not consult it. The page never links a sheet once the stream drops it, and reading
the record on every image request (event files reach 400 KB) would cost every page load to guard a guessed filename.
A name rule (`01_research_*`) was tried and dropped: it would also have hidden an infographic's `research_spend.png`.
"""

from __future__ import annotations

from typing import Any, Iterable


def research_executions(events: Iterable[dict[str, Any]]) -> set[str]:
    """The executions whose scripts took a Qloo picture's pixels."""
    return {e["execution"] for e in events if e.get("type") == "taste.pictures" and e.get("execution")}


def is_research(event: dict[str, Any], executions: set[str]) -> bool:
    """Whether an event names a render made by one of those executions."""
    return (bool(event.get("artifact")) and str(event.get("type") or "").startswith("render")
            and event.get("execution") in executions)


class StreamFilter:
    """Drops research renders from a live stream, in order: `taste.pictures` always precedes a script's `outFile` render."""

    def __init__(self) -> None:
        self.executions: set[str] = set()

    def keep(self, event: dict[str, Any]) -> bool:
        if event.get("type") == "taste.pictures" and event.get("execution"):
            self.executions.add(event["execution"])
        return not is_research(event, self.executions)
