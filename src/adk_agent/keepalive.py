"""Keeps a Cloud Run instance's CPU while a run is working and nobody is watching it.

**The problem.** A commission runs as a background task (`intake.commission`), not inside the request that started
it. On Cloud Run's default, request-based billing an instance has CPU only while a request is in flight. Measured on
2026-10-19 (4 vCPU): a background loop got 0.3 million iterations per 10 s with no request open against 170 million
with one, so a run nobody watches all but stops. A watcher's page is enough while it is open, because its event
stream is a request held open; the gap is the run nobody is watching.

**The answer.** While any tracked run is unfinished, this server holds one request open to its own public URL,
`POLSON_SELF_URL`, and to Cloud Run that is a watcher like any other. The instance is billed only while it works,
and still scales to zero. Two details carry the weight, and both were found by measuring:

- **The hold is opened while the request that started the run is still in flight** (`intake` awaits `held`).
  Opened after, its own setup (an identity token, a TLS handshake) ran throttled and took 66 s to land.
- **Renewal overlaps.** A hold is replaced before Cloud Run's 3600 s request limit, and the old one is closed only
  once the new one is up, so there is never a moment with no request and a throttled reopen to wait for.

It reaches *this* instance only because the service runs one (`--max-instances=1`); with more, the held request
could land on another. Unset `POLSON_SELF_URL` (a developer's machine) and it does nothing, said once in the log.

Measured with renewals: the probe held 177–188 million iterations per 10 s from its first window to its last. Closing
an old hold from this side does **not** end it on the server (Cloud Run's front end does not pass the disconnect
on), so each runs to `HOLD_SECONDS` or the end of the work; at 55 against a 45-minute renewal that is at most two
open at once, a few bytes every `TICK_SECONDS`.
"""

from __future__ import annotations

import asyncio
import base64
import contextlib
import logging
import os
import time
from typing import Any, AsyncIterator, Callable

import httpx
from fastapi import FastAPI
from fastapi.responses import StreamingResponse

_logger = logging.getLogger("polson.keepalive")

#: This service's own public URL, which Cloud Run does not tell an instance. Unset means off.
SELF_URL: str = os.environ.get("POLSON_SELF_URL", "").strip().rstrip("/")

#: Where the held request goes. Behind Basic auth like everything else, and it ends by itself once no run is
#: working, so holding it open from outside cannot keep an idle instance billed.
HOLD_PATH = "/keepalive/hold"

#: The longest one held response lasts, inside Cloud Run's 3600 s request timeout, so the platform never cuts it.
HOLD_SECONDS = 55 * 60

#: When the next hold is opened beside the current one. Ten minutes before the current one ends, which is ample
#: for a reopen even at throttled speed. `POLSON_KEEPALIVE_RENEW_SECONDS` shortens it to watch renewals happen.
RENEW_SECONDS = min(float(os.environ.get("POLSON_KEEPALIVE_RENEW_SECONDS", "") or 45 * 60), 45 * 60)

#: How often the held response sends a byte and rechecks for work. A byte at this interval also keeps any proxy
#: between the instance and the front end from reading the stream as idle.
TICK_SECONDS = 20.0

#: How long a new hold may take to send its first byte before it counts as failed.
OPEN_SECONDS = 30.0

#: Wait before trying again after a hold fails to open, or ends as soon as it opened.
RETRY_SECONDS = 15.0

#: The longest any one run is held, whatever it asks for. A run that hangs instead of ending must not keep an instance
#: billed for good; past this it carries on unheld, and the log says so.
MAX_HOLD_MINUTES = 180

_tracked: set[asyncio.Task] = set()
_until: dict[asyncio.Task, tuple[float, float]] = {}     # task -> (monotonic cut-off, minutes it was given)
_held = asyncio.Event()
_holder: asyncio.Task | None = None
_said_off = False


# region Public
def active() -> int:
    """How many tracked runs are still working and still within the time they are held for."""
    now, count = time.monotonic(), 0
    for task in list(_tracked):
        if task.done():
            continue
        cutoff, minutes = _until.get(task, (float("inf"), 0.0))
        if now >= cutoff:
            _tracked.discard(task)
            _until.pop(task, None)
            _logger.warning("keepalive: stopped holding for a run still going after %.0f minutes", minutes)
            continue
        count += 1
    return count


def track(task: asyncio.Task, minutes: float | None = None) -> None:
    """Keeps this instance's CPU while `task` runs, for at most `minutes` (capped at `MAX_HOLD_MINUTES`).
    Called by `intake` for every run it launches, with the run's deadline and room to finish."""
    _tracked.add(task)
    given = min(minutes or MAX_HOLD_MINUTES, MAX_HOLD_MINUTES)
    _until[task] = (time.monotonic() + 60 * given, given)
    task.add_done_callback(_tracked.discard)
    task.add_done_callback(lambda t: _until.pop(t, None))
    _ensure()


async def held(timeout: float = 15.0) -> bool:
    """Whether a hold is up, waiting up to `timeout` for one. True at once when keepalive is off, so a caller
    awaiting it before answering a request costs nothing locally."""
    if not SELF_URL or _held.is_set():
        return True
    with contextlib.suppress(asyncio.TimeoutError):
        await asyncio.wait_for(_held.wait(), timeout)
    if not _held.is_set():
        _logger.warning("keepalive: no hold within %.0f s; the run starts anyway and the hold keeps trying", timeout)
    return _held.is_set()


def mount(app: FastAPI) -> None:
    """The route the held request asks for, and (only under `POLSON_KEEPALIVE_PROBE=1`) the measuring probe."""

    @app.get(HOLD_PATH, include_in_schema=False)
    async def hold() -> StreamingResponse:
        return StreamingResponse(_hold_body(), media_type="text/plain",
                                 headers={"Cache-Control": "no-store", "X-Accel-Buffering": "no"})

    if os.environ.get("POLSON_KEEPALIVE_PROBE", "").strip() == "1":
        _mount_probe(app)
# endregion


# region Implementation
async def _hold_body(tick: float | None = None, limit: float | None = None) -> AsyncIterator[bytes]:
    """The held response: a byte every tick while a run works, ending when none does or the hold is used up."""
    tick = TICK_SECONDS if tick is None else tick
    until = time.monotonic() + (HOLD_SECONDS if limit is None else limit)
    yield b"held\n"
    while active() and time.monotonic() < until:
        await asyncio.sleep(tick)
        yield b".\n"


def _ensure() -> None:
    global _holder, _said_off
    if not SELF_URL:
        if not _said_off:
            _said_off = True
            _logger.warning("keepalive: off (no POLSON_SELF_URL); a run nobody watches gets no CPU on Cloud Run")
        return
    if _holder is None or _holder.done():
        _held.clear()
        _holder = asyncio.get_running_loop().create_task(_hold_loop())


async def _hold_once(make: Callable[[], httpx.AsyncClient], up: asyncio.Event) -> None:
    """One held request, from opening to its end. Sets `up` at the first byte; never raises."""
    try:
        async with make() as web:
            async with web.stream("GET", SELF_URL + HOLD_PATH, headers=await _headers(web)) as answer:
                if answer.status_code != 200:
                    _logger.error("keepalive: the held request was refused with %d", answer.status_code)
                    return
                async for _ in answer.aiter_bytes():
                    up.set()
    except asyncio.CancelledError:
        raise
    except Exception as exc:                                       # noqa: BLE001 - logged; the loop retries
        _logger.error("keepalive: the held request failed (%r)", exc)


async def _hold_loop(client: Callable[[], httpx.AsyncClient] | None = None, retry: float | None = None,
                     renew: float | None = None, opening: float | None = None) -> None:
    """Holds a request to this service open while any run works: a new one beside the old, then the old let go."""
    retry = RETRY_SECONDS if retry is None else retry
    renew = RENEW_SECONDS if renew is None else renew
    opening = OPEN_SECONDS if opening is None else opening
    make = client or (lambda: httpx.AsyncClient(timeout=httpx.Timeout(10.0, read=TICK_SECONDS * 3)))
    _logger.warning("keepalive: holding %s while %d run(s) work", SELF_URL + HOLD_PATH, active())
    current: asyncio.Task | None = None
    try:
        while active():
            up = asyncio.Event()
            nxt = asyncio.create_task(_hold_once(make, up))
            waiting = asyncio.create_task(up.wait())
            await asyncio.wait({nxt, waiting}, timeout=opening, return_when=asyncio.FIRST_COMPLETED)
            waiting.cancel()
            if not up.is_set():
                # Not up: the one before, if any, is still holding; try again shortly.
                nxt.cancel()
                await asyncio.sleep(retry)
                continue
            _held.set()
            if current is not None:
                current.cancel()
            current, opened = nxt, time.monotonic()
            await asyncio.wait({current}, timeout=renew)
            # A hold that ended almost as soon as it opened was not held (a proxy closing it, say); reopening
            # straight away would be a loop hammering the server, so it waits first.
            if current.done() and time.monotonic() - opened < min(TICK_SECONDS, retry * 2):
                await asyncio.sleep(retry)
    finally:
        if current is not None:
            current.cancel()
        _held.clear()
    _logger.warning("keepalive: no run working; released")


async def _headers(web: httpx.AsyncClient) -> dict[str, str]:
    """Basic auth in `Authorization` when set; on Cloud Run, an identity token in `X-Serverless-Authorization`.

    Two headers because both gates may be up at once: a private service checks IAM, and Cloud Run reads the token
    from `X-Serverless-Authorization` when present and passes `Authorization` through to the app untouched.
    """
    headers: dict[str, str] = {}
    basic = os.environ.get("POLSON_BASIC_AUTH", "").strip()
    if basic:
        headers["Authorization"] = "Basic " + base64.b64encode(basic.encode("utf-8")).decode("ascii")
    if os.environ.get("K_SERVICE"):
        try:
            token = await web.get(
                "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity",
                params={"audience": SELF_URL}, headers={"Metadata-Flavor": "Google"})
            if token.status_code == 200:
                headers["X-Serverless-Authorization"] = "Bearer " + token.text.strip()
        except httpx.HTTPError as exc:
            # A public service needs no token, so this is said and the request tried without one.
            _logger.warning("keepalive: no identity token from the metadata server (%r)", exc)
    return headers


def _mount_probe(app: FastAPI) -> None:
    """A measurement, never a feature: `POST /keepalive/probe?minutes=N` burns CPU in the background as a tracked
    run would, logging how much work each ten seconds got. With no request in flight and no keepalive the count
    collapses; with keepalive it should hold. Mounted only under `POLSON_KEEPALIVE_PROBE=1`."""

    def burn(minutes: float) -> None:
        end, window, count = time.monotonic() + minutes * 60, time.monotonic() + 10, 0
        while time.monotonic() < end:
            for _ in range(10_000):
                count += 1
            if time.monotonic() >= window:
                _logger.warning("keepalive probe: %d iterations in the last 10 s (%d run(s) tracked)",
                                count, active())
                count, window = 0, time.monotonic() + 10

    @app.post("/keepalive/probe", include_in_schema=False)
    async def probe(minutes: float = 5.0) -> dict[str, Any]:
        task = asyncio.get_running_loop().create_task(asyncio.to_thread(burn, min(max(minutes, 0.5), 30.0)))
        track(task)
        up = await held()
        return {"probing_minutes": minutes, "keepalive": bool(SELF_URL), "held": up, "tracked": active()}
# endregion
