"""Which routes a visitor can reach on a deployment with no sign-in: the studio and the intake form, and nothing else.

`get_fast_api_app` serves ADK's whole API beside the studio, and basic auth used to be the only thing in front of it.
Open — as a hackathon entry must be, judges being given a URL and no password — that API lets anyone:

- run any app over `/run_sse`, `/run` or `/run_live`, **past the run allowance**, which `intake` checks and ADK does not;
- list, read and delete every visitor's sessions and artifacts;
- hold `/keepalive/hold` open, which keeps an instance's CPU allocated.

So every path outside `PUBLIC` is answered 404, as though it did not exist, unless the request carries the deployment's
`TOKEN`, a hash of a secret its instances share (`_token`). It never leaves the service: `intake` sends it on the
in-process requests that start a run, and `keepalive` on the request it holds against its own service.

An allowlist, so a route a later ADK adds is closed until it is named here. Pure ASGI, like `basic_auth`, so the
studio's event streams pass through untouched.

On by default on Cloud Run (`K_SERVICE` set) and off on a developer's machine, where the console needs the API.
`POLSON_GATE_API=1` or `0` says so explicitly.
"""

from __future__ import annotations

import hashlib
import hmac
import os
import secrets
from typing import Any, Awaitable, Callable

Scope = dict[str, Any]
Receive = Callable[[], Awaitable[dict[str, Any]]]
Send = Callable[[dict[str, Any]], Awaitable[None]]

#: The header an internal request carries, and its value for this deployment.
HEADER = "x-polson-internal"


def _token() -> str:
    """One value for every instance of a deployment, which no visitor can know: a one-way hash of the agent's key.

    Random per process, it was right for the in-process launch and wrong for the keepalive: a hold that landed on a sibling
    instance (Cloud Run starts one at times, 2026-10-29) was a stranger there, refused by the gate with a 404 before the
    hold route could say whose it was. With no key (a developer's machine) it is random, as before.
    """
    key = os.environ.get("POLSON_AGENT_PLATFORM_KEY", "").strip()
    return hmac.new(key.encode(), b"polson api gate", hashlib.sha256).hexdigest() if key else secrets.token_urlsafe(32)


TOKEN = _token()

#: Paths open to everyone, exactly. `/studio` is open with everything under it; the studio refuses what a reader may
#: not do itself (`intake.observing_only`). `/new` and `/projects` are the intake form, which checks the allowance.
PUBLIC = frozenset({"/", "/new", "/projects", "/health", "/studio"})
PUBLIC_UNDER = ("/studio/",)


def public(path: str) -> bool:
    """Whether `path` is open without the token."""
    return path in PUBLIC or path.startswith(PUBLIC_UNDER)


def internal() -> dict[str, str]:
    """The header that lets one of this process's own requests through."""
    return {HEADER: TOKEN}


def enabled() -> bool:
    """`POLSON_GATE_API` when set; otherwise on under Cloud Run and off elsewhere."""
    value = os.environ.get("POLSON_GATE_API", "").strip()
    return value == "1" if value else bool(os.environ.get("K_SERVICE"))


class ApiGate:
    """Answers 404 to every request outside `PUBLIC` that does not carry `TOKEN`."""

    # region Constructors
    def __init__(self, app: Callable[[Scope, Receive, Send], Awaitable[None]], token: str = TOKEN) -> None:
        self.app = app
        self.token = token.encode("ascii")
    # endregion

    # region Methods
    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] not in ("http", "websocket") or self.allowed(scope):
            await self.app(scope, receive, send)
            return

        if scope["type"] == "websocket":
            await send({"type": "websocket.close", "code": 1008})
            return

        # FastAPI's own answer for a missing route, so a closed one is indistinguishable from an absent one.
        body = b'{"detail":"Not Found"}'
        await send({"type": "http.response.start", "status": 404, "headers": [
            (b"content-type", b"application/json"),
            (b"content-length", str(len(body)).encode()),
        ]})
        await send({"type": "http.response.body", "body": body})

    def allowed(self, scope: Scope) -> bool:
        if public(scope.get("path") or "/"):
            return True
        sent = next((v for k, v in scope.get("headers") or [] if k == HEADER.encode()), b"")
        return hmac.compare_digest(sent, self.token)
    # endregion


def install(app: Any) -> str:
    """Puts the gate in front of `app` when `enabled()`, and says what is open, for the startup log."""
    if not enabled():
        return "API gate OFF: ADK's API and console are open to anyone who can reach this server"
    app.add_middleware(ApiGate)
    return "API gate on: only " + ", ".join(sorted(PUBLIC)) + " and /studio/* answer a visitor"
