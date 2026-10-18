"""The studio as a person commissioning work sees it.

The developer pages (`/`, `/runs/<id>`) show the machinery: projects, sessions, the record, the
sense-making curve. These show the same things in a client's terms — choose a workflow, say what you
want in which sizes and formats, attach the files to use, and watch it get made — styled by the Polson
Studio design system (`static/polson.css`).

**Nothing here is a second path to the agent.** A request becomes a project through `projects.create`,
which hands the brief to the CLI as a file; a run starts through `Registry.start`; a live request page
reads `/runs/<id>/events` and the artifact and deliverable routes the developer page already uses, with
their containment and allowlists unchanged. The one new file route, `/requests/<name>/thumb`, takes no
visitor path at all: it serves the newest image the project's own record says the engine rendered.

**One file is written beside a project, `request.json`**: the title and choices the person made, so the
request reads as "Harbour festival trailer" rather than as its directory name. It is display data only —
nothing the agent reads — and a project without one falls back to its id.
"""

from __future__ import annotations

import json
import re
import shutil
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, HTMLResponse, RedirectResponse
from fastapi.templating import Jinja2Templates

from orchestrator.events import read_events

from . import catalogue, projects, uploads
from . import direction as direction_mod
from . import observe as observe_mod
from .runs import Registry, Run, StudioError

REQUEST_FILE = "request.json"

#: What the thumbnail route may serve: still images only, never video, never a print PDF.
THUMB_SUFFIXES = frozenset({".webp", ".png", ".jpg", ".jpeg", ".svg"})

#: What the workflows name a finished piece: `output`, `final`, or one per audience `final_a`, `final_b`…
FINAL = re.compile(r"output|final(_[a-z])?")

#: Status words, in the order a list sorts them: what needs you first.
ORDER = {"needs-you": 0, "running": 1, "queued": 2, "draft": 3, "delivered": 4, "stopped": 5}


# region Reading a project as a request
def read_request(project: Path) -> dict[str, Any]:
    """The `request.json` written when the request was composed, or {} for any other project."""
    try:
        data = json.loads((project / REQUEST_FILE).read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def run_for(registry: Registry, project: Path) -> Run | None:
    """The run to show for a project: a live driven run first, then the newest of any kind."""
    root = project.resolve()
    mine = [r for r in registry.runs if Path(r.project.root).resolve() == root]
    return next((r for r in mine if r.live and not r.observed), None) or (mine[0] if mine else None)


def status_of(project: Path, run: Run | None, events: list[dict[str, Any]]) -> str:
    """One of the six status words, from the run this server holds and the project's own record.

    The record decides for a project this server did not run: no events is a draft, a `run.end` after
    renders is delivered, and anything else that has stopped moving is stopped. A live driven run is
    in the studio, or needs you while a question is open.
    """
    if run is not None and run.live and not run.observed:
        director = getattr(run.stream, "director", None)
        return "needs-you" if director is not None and director.pending else "running"
    if run is not None and run.observed and run.live:
        return "running"
    if run is not None and run.status in ("failed", "incomplete", "cancelled"):
        return "stopped"
    if not events:
        return "draft"
    started = [i for i, e in enumerate(events) if e.get("type") == "run.start"]
    after = events[started[-1]:] if started else events
    # The agent's own ending, where its runtime wrote one, outranks the files: a run that hit a quota error
    # after writing output.png stopped, and the live page says so from the same event.
    since = after[0].get("ts", "") if started else ""
    for e in reversed(read_events(project / "events" / "agent.jsonl")):
        if e.get("type") == "run.end" and (e.get("ts") or "") >= since:
            how = e.get("reason") or e.get("status") or ""
            if e.get("error") or how not in ("", "completed", "done"):
                return "stopped"
            break
    # A finished piece is the evidence that matters: Claude Code ends a run by stopping the MCP server, which
    # writes no run.end, so a run that delivered `final_a.mp4` would otherwise read as stopped.
    if any(e.get("type") == "render" and FINAL.fullmatch(Path(e.get("artifact") or "").stem) for e in after):
        return "delivered"
    if any(e.get("type") == "run.end" for e in after) and any(e.get("type") == "render" for e in events):
        return "delivered"
    return "stopped"


def latest_render(project: Path, events: list[dict[str, Any]]) -> Path | None:
    """The newest still image the record says was rendered, if it is still on disk and inside the project."""
    root = project.resolve()
    for event in reversed(events):
        if event.get("type") != "render" or (event.get("motion") or "").lower() == "sheet":
            continue
        artifact = event.get("artifact") or ""
        if Path(artifact).suffix.lower() not in THUMB_SUFFIXES:
            continue
        candidate = (root / artifact).resolve()
        if root not in candidate.parents:
            continue
        relative = candidate.relative_to(root).as_posix()
        if relative.split("/", 1)[0].lower() in ("documents",):
            continue
        if candidate.is_file():
            return candidate
    return None


def describe(project: Path, registry: Registry) -> dict[str, Any]:
    """A project as one row of the requests list."""
    record = read_request(project)
    try:
        manifest = json.loads((project / "project.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        manifest = {}
    workflow = record.get("workflow") or manifest.get("workflow") or ""
    card = catalogue.BY_SLUG.get(record.get("card", "")) or catalogue.card_for(workflow)
    events = read_events(project / "events" / "server.jsonl")
    run = run_for(registry, project)

    stamp = events[-1].get("ts", "") if events else ""
    if not stamp:
        try:
            stamp = datetime.fromtimestamp((project / "project.json").stat().st_mtime, timezone.utc).isoformat()
        except OSError:
            stamp = ""

    return {
        "id": project.name,
        "title": record.get("title") or project.name,
        "workflow": card.title if card else (workflow or "Unknown"),
        "kind": card.kind if card else "image",
        "status": status_of(project, run, events),
        "updated": stamp,
        "updated_label": when(stamp),
        "thumb": latest_render(project, events) is not None,
        # The same test `discover` makes: the orchestrator runs Antigravity projects with a tool policy only.
        # Anything else is watched here and worked in its own host, so the page offers no Start or change.
        "runnable": (manifest.get("sdk") or "agy").lower() == "agy" and (project / "agent.config.json").is_file(),
        "record": record,
        "run": run,
        "events": events,
    }


def requests(root: Path, registry: Registry) -> list[dict[str, Any]]:
    """Every project under `root` as a request, needs-you first, then newest."""
    rows = [describe(m.parent, registry) for m in Path(root).glob("*/project.json")]
    rows.sort(key=lambda r: r["updated"], reverse=True)
    rows.sort(key=lambda r: ORDER.get(r["status"], 9))
    return rows


def when(stamp: str) -> str:
    """'Oct 18, 11:02' from an ISO timestamp, or '' when there is none."""
    try:
        moment = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
    except ValueError:
        return ""
    return moment.strftime("%b %d, %H:%M").replace(" 0", " ")


def learned_from(project: Path, run: Run | None, events: list[dict[str, Any]]) -> dict[str, Any]:
    """What the run learned from its audiences, or {} before it has written `direction.md`."""
    try:
        text = (project / "direction.md").read_text(encoding="utf-8", errors="replace")
    except OSError:
        text = ""
    found = direction_mod.learned(text, events, run.since if run else None)
    return found if (found["profiles"] or found["compare"] or found["audiences"]) else {}


def unique_id(root: Path, title: str) -> str:
    """A free project id derived from the title: `harbour-festival-trailer`, then `-2`, `-3`…"""
    base_id = catalogue.slugify(title)
    for n in range(1, 100):
        candidate = base_id if n == 1 else f"{base_id[:44]}-{n}"
        if not (root / candidate).exists():
            return candidate
    raise StudioError("There are too many requests with that name; choose another.")
# endregion


def install(app: FastAPI, templates: Jinja2Templates, base, contain) -> None:
    """Adds the user-facing pages to the studio app. `base` and `contain` are `app.base` and `app.contain`."""

    notices: dict[str, str] = {}

    def page(request: Request, name: str, nav: str, status_code: int = 200, **context: Any) -> HTMLResponse:
        state = request.app.state
        # Questions open right now, from the runs in memory rather than a scan of every project.
        waiting = sum(1 for r in state.registry.runs if r.live and not r.observed
                      and getattr(getattr(r.stream, "director", None), "pending", None))
        return templates.TemplateResponse(request, f"front/{name}", {
            "base": base(request), "nav": nav, "waiting_count": waiting,
            "observe_only": state.observe_only, "create_at": state.create_at,
            "active": state.registry.active, **context,
        }, status_code=status_code)

    def project_dir(request: Request, name: str) -> Path:
        try:
            path = contain(request.app.state.root, name)
        except StudioError as exc:
            raise HTTPException(status_code=404, detail=str(exc)) from exc
        if path.parent != Path(request.app.state.root).resolve() or not (path / "project.json").is_file():
            raise HTTPException(status_code=404, detail=f"no such request: {name}")
        return path

    # region Pages
    @app.get("/home", response_class=HTMLResponse)
    async def home(request: Request) -> Any:
        rows = requests(request.app.state.root, request.app.state.registry)
        return page(request, "home.html", "home", rows=rows[:6],
                    waiting=[r for r in rows if r["status"] == "needs-you"],
                    working=[r for r in rows if r["status"] == "running"],
                    drafts=[r for r in rows if r["status"] == "draft"][:3],
                    cards=[c for c in catalogue.CARDS if c.image][:3])

    @app.get("/workflows", response_class=HTMLResponse)
    async def workflows(request: Request) -> Any:
        return page(request, "workflows.html", "new", cards=catalogue.CARDS)

    @app.get("/compose/{slug}", response_class=HTMLResponse)
    async def compose(request: Request, slug: str) -> Any:
        card = catalogue.BY_SLUG.get(slug)
        if card is None:
            raise HTTPException(status_code=404, detail=f"no such workflow: {slug}")
        return composer(request, card, {})

    def composer(request: Request, card: catalogue.Card, form: dict[str, Any], refused: str = "",
                 status_code: int = 200) -> HTMLResponse:
        route = int(form.get("route") or 0) if str(form.get("route") or "0").isdigit() else 0
        route = min(route, len(card.routes) - 1)
        return page(request, "compose.html", "new", status_code=status_code, card=card,
                    sizes=catalogue.SIZES[card.kind], formats=catalogue.FORMATS[card.kind],
                    styles={i: catalogue.types_for(card, r.workflow) for i, r in enumerate(card.routes)},
                    form=form, route=route, refused=refused, ages=catalogue.AGE_BANDS,
                    max_audiences=catalogue.MAX_AUDIENCES,
                    accept=",".join(sorted(uploads.DOCUMENT_SUFFIXES)), max_files=uploads.MAX_FILES)

    @app.get("/requests", response_class=HTMLResponse)
    async def listing(request: Request, view: str = "active") -> Any:
        rows = requests(request.app.state.root, request.app.state.registry)
        groups = {"active": [r for r in rows if r["status"] in ("needs-you", "running", "queued")],
                  "drafts": [r for r in rows if r["status"] == "draft"],
                  "done": [r for r in rows if r["status"] in ("delivered", "stopped")]}
        view = view if view in groups else "active"
        return page(request, "requests.html", "requests", rows=groups[view], view=view,
                    counts={k: len(v) for k, v in groups.items()})

    @app.get("/requests/{name}", response_class=HTMLResponse)
    async def show(request: Request, name: str) -> Any:
        project = project_dir(request, name)
        row = describe(project, request.app.state.registry)
        run = row["run"]

        # A finished project this server never ran has no run id, and the artifact and deliverable
        # routes are keyed by one. Watching it is free and read-only, so it is how those routes open.
        if run is None and row["events"]:
            try:
                run = await observe_mod.observe(request.app.state.registry, project)
            except StudioError:
                run = None

        brief = ""
        try:
            brief = (project / "brief.md").read_text(encoding="utf-8", errors="replace")
        except OSError:
            pass

        # A driven run answers through its own director. An observed one only where the agent is in this
        # process (the ADK runtime); elsewhere the host owns the question and the page shows it read-only.
        answerable = run is not None and (not run.observed or observe_mod._ask_channel() is not None)

        return page(request, "request.html", "requests", row=row, run=run, brief=brief, answerable=answerable,
                    card=catalogue.BY_SLUG.get(row["record"].get("card", "")) or catalogue.card_for(
                        row["record"].get("workflow", "")),
                    learned=learned_from(project, run, row["events"]),
                    notice=notices.pop(name, ""))

    @app.get("/requests/{name}/learned", response_class=HTMLResponse)
    async def learned(request: Request, name: str) -> Any:
        """The learned panel alone, for the page to swap in once a run has written its direction."""
        project = project_dir(request, name)
        row = describe(project, request.app.state.registry)
        return templates.TemplateResponse(request, "front/learned.html",
                                          {"learned": learned_from(project, row["run"], row["events"])})

    @app.get("/requests/{name}/thumb")
    async def thumb(request: Request, name: str) -> FileResponse:
        project = project_dir(request, name)
        path = latest_render(project, read_events(project / "events" / "server.jsonl"))
        if path is None:
            raise HTTPException(status_code=404, detail="nothing rendered yet")
        return FileResponse(path, headers={"Cache-Control": "no-cache", "X-Content-Type-Options": "nosniff"})
    # endregion

    # region Actions
    @app.post("/requests")
    async def create(request: Request,
                     card: str = Form(...), title: str = Form(""), route: str = Form("0"),
                     kind: str = Form(""), sizes: list[str] = Form([]), custom_size: str = Form(""),
                     formats: list[str] = Form([]), brief: str = Form(""), action: str = Form("save"),
                     aud_place: list[str] = Form([]), aud_age: list[str] = Form([]),
                     files: list[UploadFile] = File([])) -> Any:
        state = request.app.state
        form_data = await request.form()
        extras = {k[2:]: str(v) for k, v in form_data.items() if k.startswith("x_")}
        form = {"title": title, "route": route, "kind": kind, "sizes": sizes, "custom_size": custom_size,
                "formats": formats, "brief": brief, "extras": extras,
                "audiences": [{"place": p, "age": aud_age[i] if i < len(aud_age) else ""}
                              for i, p in enumerate(aud_place)]}

        chosen = catalogue.BY_SLUG.get(card)
        if chosen is None:
            raise HTTPException(status_code=404, detail=f"no such workflow: {card}")
        if state.observe_only:
            return composer(request, chosen, form, "This studio can watch requests but not make them. "
                            "Use the create page instead.", 409)

        scratch = Path(tempfile.mkdtemp(prefix="polson-request-"))
        try:
            commission = catalogue.parse(card, title, route, kind, sizes, custom_size, formats, extras, brief,
                                         aud_place, aud_age)
            staged = await uploads.stage(files, scratch)
            project_id = unique_id(state.root, commission.title)
            made = await projects.create(state.root, project_id, commission.workflow, commission.kind,
                                         commission.brief_text([p.name for p in staged]))
        except StudioError as exc:
            shutil.rmtree(scratch, ignore_errors=True)
            note = " Files have to be chosen again." if any((f.filename or "").strip() for f in files) else ""
            return composer(request, chosen, form, f"{exc}{note}", 409)

        try:
            if staged:
                documents = made / "documents"
                documents.mkdir(exist_ok=True)
                for path in staged:
                    shutil.copy2(path, documents / path.name)
            record = commission.record()
            record["files"] = [p.name for p in staged]
            record["created"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
            (made / REQUEST_FILE).write_text(json.dumps(record, indent=2), encoding="utf-8")
        finally:
            shutil.rmtree(scratch, ignore_errors=True)

        if action == "start":
            try:
                await state.registry.start(made, "")
            except StudioError as exc:
                notices[made.name] = f"Saved as a draft, but not started: {exc}"

        return RedirectResponse(f"{base(request)}/requests/{made.name}", status_code=303)

    @app.post("/requests/{name}/start")
    async def start(request: Request, name: str, change: str = Form("")) -> Any:
        """Starts a draft, or asks for a change to finished work by continuing its session."""
        project = project_dir(request, name)
        if request.app.state.observe_only:
            notices[name] = "This studio can watch requests but not run them."
        else:
            change = change.strip()
            try:
                if change:
                    await request.app.state.registry.start(project, change, resume=True)
                else:
                    await request.app.state.registry.start(project, "", resume=False)
            except StudioError as exc:
                notices[name] = str(exc)
        return RedirectResponse(f"{base(request)}/requests/{name}", status_code=303)
    # endregion
