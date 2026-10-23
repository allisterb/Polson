"""Where a visitor lands on `/`: the client studio, not ADK's developer console.

ADK's `get_fast_api_app(web=True)` registers `GET /` as a redirect to `/dev-ui/` (`api_server.py`, 2.8.0). On a
deployment that sent a judge given the bare URL into the developer console: the event stream, raw tool calls and
the agent graph, rather than the studio a client uses. The console stays served at `/dev-ui/`; only the door moves.

ADK's route is removed rather than shadowed: Starlette matches routes in the order they were added, so a second
`GET /` added after ADK's would never be reached.
"""

from __future__ import annotations

from fastapi import FastAPI
from fastapi.responses import RedirectResponse
from starlette.routing import Route

#: The client studio's home, where `/` goes when the studio is mounted.
STUDIO_HOME = "/studio/home"


def install(app: FastAPI, target: str) -> str:
    """Make `GET /` redirect to `target`, replacing any root route already there. Returns what it did, for the log."""
    before = len(app.router.routes)
    app.router.routes[:] = [r for r in app.router.routes
                            if not (isinstance(r, Route) and r.path == "/" and "GET" in (r.methods or ()))]
    replaced = before - len(app.router.routes)

    @app.get("/", include_in_schema=False)
    async def front_page() -> RedirectResponse:
        return RedirectResponse(target)

    return f"/ redirects to {target}" + (" (replacing ADK's redirect to the console)" if replaced else "")
