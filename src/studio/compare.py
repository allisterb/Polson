"""The audiences' films side by side in one MP4, for sending to someone without the page.

The page's own grid plays the same films together at full size and lets a reader choose whose sound to hear; this
is the file that travels. `polson compare` makes it — the .NET side owns ffmpeg's arguments and draws the labels
shaped, so an Arabic or Japanese place name is set right — and this decides when, from what, and whether the file
already there is still the one asked for.

**Made when asked, not when the run ends.** It costs a minute of every core for a two-minute film, and most runs are
never compared; a reader's click is the signal it is wanted. Once made it is kept at `artifacts/comparison.mp4`, with
what it was made from in `.polson/comparison.json`, so the next reader is served the file and a run that changes a
piece afterwards gets a new one.

**The labels reach the CLI in a file.** They are the director's words — a place, an age band, a language — and
`projects` explains why words never go on a command line.
"""

from __future__ import annotations

import asyncio
import json
import shutil
import tempfile
from pathlib import Path
from typing import Any

from . import projects
from .runs import StudioError

#: Where the file is kept, project-relative, so the artifact route serves it as it serves any render.
OUTPUT = "artifacts/comparison.mp4"

#: What it was made from. Beside the engine's own caches rather than the artifacts, which a director browses.
STAMP = ".polson/comparison.json"

#: Bumped when the CLI would draw the same films differently, so a file made by an older one is made again.
VERSION = 1

#: A two-minute pair took 42 s on eight cores; six two-minute films on a small instance can take several times that.
TIMEOUT = 600.0

#: One encode at a time on this server: each uses every core, and two would each take twice as long.
_slot = asyncio.Semaphore(1)
#: One build per project, so a second click waits for the first and is then served its file.
_building: dict[str, asyncio.Lock] = {}


def films(root: Path, pieces: list[dict[str, Any]], audiences: list[dict[str, Any]]) -> list[dict[str, str]]:
    """Each audience's film, in letter order, with the two lines under it; [] unless every piece is a film.

    The labels are the page's: "Audience A · Dubai", then the age band and language. A run made outside the form has
    no `request.json`, and its films are labelled by letter alone.
    """
    who = {a.get("letter"): a for a in audiences if isinstance(a, dict)}
    out = []
    for i, piece in enumerate(pieces):
        video = next((f["path"] for f in piece.get("formats", []) if Path(f["path"]).suffix.lower() in (".mp4", ".webm")), None)
        if video is None:
            return []
        letter = piece.get("audience") or str(i + 1)
        a = who.get(letter, {})
        title = f"Audience {letter}" + (f" · {a['place']}" if a.get("place") else "")
        detail = ", ".join(t for t in (a.get("ageLabel") or "", f"in {a['languageLabel']}" if a.get("languageLabel") else "") if t)
        out.append({"path": video, "full": str((Path(root) / video).resolve()), "title": title, "detail": detail})
    return out if len(out) >= 2 else []


def _stamp(found: list[dict[str, str]]) -> dict[str, Any]:
    """What a made file was made from: each film's name, size and time, and its labels."""
    rows = []
    for f in found:
        stat = Path(f["full"]).stat()
        rows.append({"path": f["path"], "bytes": stat.st_size, "mtime_ns": stat.st_mtime_ns,
                     "title": f["title"], "detail": f["detail"]})
    return {"version": VERSION, "films": rows}


def current(root: Path, found: list[dict[str, str]]) -> dict[str, Any] | None:
    """The kept file, if it was made from exactly these films and labels; None when there is none or it is stale."""
    root = Path(root)
    output = root / OUTPUT
    try:
        kept = json.loads((root / STAMP).read_text(encoding="utf-8"))
        if output.is_file() and kept == _stamp(found):
            return {"path": OUTPUT, "bytes": output.stat().st_size}
    except (OSError, ValueError):
        pass
    return None


async def build(root: Path, found: list[dict[str, str]]) -> dict[str, Any]:
    """The comparison of `found`: the kept one when it is current, else made now. Raises `StudioError`."""
    root = Path(root)
    lock = _building.setdefault(str(root.resolve()), asyncio.Lock())
    async with lock:
        if made := current(root, found):
            return made
        async with _slot:
            scratch = Path(tempfile.mkdtemp(prefix="polson-compare-"))
            try:
                listing = scratch / "films.json"
                listing.write_text(json.dumps([{k: f[k] for k in ("title", "detail")} | {"path": f["full"]} for f in found],
                                              ensure_ascii=False), encoding="utf-8")
                argv = [*projects.launcher(), "compare", str(root / OUTPUT), "--films", str(listing)]
                try:
                    process = await asyncio.create_subprocess_exec(
                        *argv, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
                except FileNotFoundError as exc:
                    raise StudioError(f"Could not run the Polson CLI ({argv[0]}): {exc}") from exc
                try:
                    out, err = await asyncio.wait_for(process.communicate(), timeout=TIMEOUT)
                except (asyncio.TimeoutError, TimeoutError) as exc:
                    process.kill()
                    raise StudioError(f"The comparison was not finished within {TIMEOUT:.0f} s.") from exc
                if process.returncode != 0:
                    raise StudioError(projects._why(out, err) or f"compare failed with exit {process.returncode}.")
            finally:
                shutil.rmtree(scratch, ignore_errors=True)
        (root / STAMP).parent.mkdir(parents=True, exist_ok=True)
        (root / STAMP).write_text(json.dumps(_stamp(found)), encoding="utf-8")
        return {"path": OUTPUT, "bytes": (root / OUTPUT).stat().st_size}
