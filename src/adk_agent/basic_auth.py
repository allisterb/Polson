"""HTTP Basic auth over everything this server answers: the studio, the intake form, ADK's API and its console.

The public deployment is a URL handed to hackathon judges, and behind it an agent that spends a metered model,
a metered image service and a Qloo key on whatever a visitor types. Cloud Run's own IAM cannot be the gate,
because a judge has no Google identity on the project; one shared password, sent with every request by the
browser once typed, can be.

**Pure ASGI rather than `BaseHTTPMiddleware`**, because the studio streams: the run page's events are a
Server-Sent Events response that never ends on its own, and this passes it through untouched rather than
wrapping it. Websockets (ADK's live endpoint) are checked on the handshake; the server's lifespan messages pass.

Configured by `POLSON_BASIC_AUTH` as `user:password`, from the `polson-basic-auth` secret; see `install`.
"""

from __future__ import annotations

import base64
import binascii
import hmac
from typing import Any, Awaitable, Callable

Scope = dict[str, Any]
Receive = Callable[[], Awaitable[dict[str, Any]]]
Send = Callable[[dict[str, Any]], Awaitable[None]]

#: The name a browser shows in its sign-in prompt.
REALM = "Polson Studio"


def parse(value: str) -> tuple[bytes, bytes]:
    """`user:password` as the two byte strings compared against. The password may itself contain colons.

    Raises `ValueError` for anything else, and `install` lets it propagate: a malformed credential must stop the
    server starting, because the alternative is a deployment that is either open or locked to everyone while
    looking configured. On Cloud Run a revision that fails to start never takes traffic, so the last good one
    keeps serving.
    """
    user, colon, password = value.strip().partition(":")
    if not colon or not user or not password:
        raise ValueError("POLSON_BASIC_AUTH must be 'user:password', both parts non-empty")
    return user.encode("utf-8"), password.encode("utf-8")


class BasicAuth:
    """Refuses every HTTP request and websocket handshake that does not carry the configured credential."""

    # region Constructors
    def __init__(self, app: Callable[[Scope, Receive, Send], Awaitable[None]], credentials: str) -> None:
        self.app = app
        self.user, self.password = parse(credentials)
    # endregion

    # region Methods
    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] not in ("http", "websocket") or self.allowed(scope):
            await self.app(scope, receive, send)
            return

        if scope["type"] == "websocket":
            # Closing before accepting is how ASGI refuses a handshake; the client sees a 403.
            await send({"type": "websocket.close", "code": 1008})
            return

        body = b"Sign in to see the Polson Studio.\n"
        await send({"type": "http.response.start", "status": 401, "headers": [
            (b"www-authenticate", f'Basic realm="{REALM}", charset="UTF-8"'.encode()),
            (b"content-type", b"text/plain; charset=utf-8"),
            (b"content-length", str(len(body)).encode()),
        ]})
        await send({"type": "http.response.body", "body": body})

    def allowed(self, scope: Scope) -> bool:
        header = next((v for k, v in scope.get("headers") or [] if k == b"authorization"), b"")
        scheme, _, token = header.partition(b" ")
        if scheme.lower() != b"basic":
            return False
        try:
            user, colon, password = base64.b64decode(token.strip(), validate=True).partition(b":")
        except (binascii.Error, ValueError):
            return False
        # Both halves always compared, in constant time, so neither the timing nor an early exit says which was wrong.
        same_user = hmac.compare_digest(user, self.user)
        same_password = hmac.compare_digest(password, self.password)
        return bool(colon) and same_user and same_password
    # endregion


def install(app: Any, value: str | None) -> str:
    """Puts Basic auth in front of `app` when `value` is set, and says what this server is now open to.

    Added as the outermost middleware, so it runs before ADK's own CORS and origin checks and before the mounted
    studio. Unset or blank leaves the server open, which is right for a developer's machine and is said at startup
    so it is never a surprise on a public one.
    """
    if not (value or "").strip():
        return "basic auth OFF: open to anyone who can reach this server (set POLSON_BASIC_AUTH=user:password)"
    user, _ = parse(value)
    app.add_middleware(BasicAuth, credentials=value)
    return f"basic auth on: every request needs the password for user '{user.decode('utf-8')}'"
