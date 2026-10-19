"""`keepalive`: the request the server holds open to itself while a run works and nobody watches.

What it must never do matters as much as what it does: hold on after the last run ends (an idle instance kept
billed), or fail in a way that stops the run it exists to protect.
"""

from __future__ import annotations

import asyncio
import base64
import os
import sys
import unittest
from pathlib import Path
from unittest import mock

import httpx
from fastapi import FastAPI
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import keepalive  # noqa: E402


class KeepaliveTestCase(unittest.IsolatedAsyncioTestCase):

    def setUp(self) -> None:
        keepalive._tracked.clear()
        keepalive._until.clear()
        keepalive._holder = None
        keepalive._held = asyncio.Event()
        keepalive._said_off = False

    def working(self, seconds: float) -> asyncio.Task:
        run = asyncio.create_task(asyncio.sleep(seconds))
        keepalive._tracked.add(run)
        run.add_done_callback(keepalive._tracked.discard)
        return run


class HoldBodyTests(KeepaliveTestCase):

    async def collect(self, **kw) -> list[bytes]:
        return [chunk async for chunk in keepalive._hold_body(**kw)]

    async def test_with_no_run_working_it_answers_and_ends(self):
        """Holding it open from outside cannot keep an idle instance billed."""
        self.assertEqual(await self.collect(tick=0.01), [b"held\n"])

    async def test_it_ticks_while_a_run_works_and_ends_with_the_run(self):
        run = asyncio.create_task(asyncio.sleep(0.12))
        keepalive._tracked.add(run)
        run.add_done_callback(keepalive._tracked.discard)

        chunks = await self.collect(tick=0.02)

        self.assertTrue(run.done())
        self.assertGreater(chunks.count(b".\n"), 2)

    async def test_it_ends_at_its_limit_even_while_a_run_works(self):
        """Inside Cloud Run's request timeout, so the platform never cuts it and the loop renews it instead."""
        run = asyncio.create_task(asyncio.sleep(10))
        keepalive._tracked.add(run)
        try:
            chunks = await asyncio.wait_for(self.collect(tick=0.01, limit=0.05), 2)
        finally:
            run.cancel()
        self.assertLess(len(chunks), 20)


class TrackTests(KeepaliveTestCase):

    async def test_without_a_self_url_it_tracks_but_holds_nothing_and_says_so(self):
        with mock.patch.object(keepalive, "SELF_URL", ""), self.assertLogs("polson.keepalive", "WARNING") as log:
            run = asyncio.create_task(asyncio.sleep(0.01))
            keepalive.track(run)
            self.assertEqual(keepalive.active(), 1)
            self.assertIsNone(keepalive._holder)
            await run
        self.assertIn("off", log.output[0])
        await asyncio.sleep(0)
        self.assertEqual(keepalive.active(), 0)

    async def test_one_holder_for_many_runs(self):
        with mock.patch.object(keepalive, "SELF_URL", "https://self.invalid"), \
             mock.patch.object(keepalive, "_hold_loop", lambda: asyncio.sleep(1)):
            runs = [asyncio.create_task(asyncio.sleep(0.05)) for _ in range(3)]
            for run in runs:
                keepalive.track(run)
            holder = keepalive._holder
            self.assertIsNotNone(holder)
            for run in runs:
                keepalive.track(run)
            self.assertIs(keepalive._holder, holder)
            holder.cancel()
            await asyncio.gather(*runs)


class NotLongerThanNeededTests(KeepaliveTestCase):
    """The director's condition: once the work is done, or is past its time, nothing keeps the instance up."""

    async def test_a_finished_run_ends_the_hold_within_a_tick(self):
        with mock.patch.object(keepalive, "SELF_URL", ""):
            run = asyncio.create_task(asyncio.sleep(0.05))
            keepalive.track(run)
            started = asyncio.get_running_loop().time()
            chunks = [c async for c in keepalive._hold_body(tick=0.02)]
        self.assertTrue(run.done())
        self.assertLess(asyncio.get_running_loop().time() - started, 0.2)
        self.assertEqual(keepalive.active(), 0)
        self.assertGreater(len(chunks), 1)

    async def test_a_run_past_its_minutes_is_no_longer_held_though_it_still_runs(self):
        """A hung run must not keep an instance billed for good."""
        with mock.patch.object(keepalive, "SELF_URL", ""):
            hung = asyncio.create_task(asyncio.sleep(10))
            keepalive.track(hung, minutes=0.001)                  # 60 ms
            self.assertEqual(keepalive.active(), 1)
            await asyncio.sleep(0.1)
            with self.assertLogs("polson.keepalive", "WARNING") as log:
                self.assertEqual(keepalive.active(), 0)
            self.assertFalse(hung.done(), "the run itself is not stopped, only no longer held")
            hung.cancel()
        self.assertIn("stopped holding", log.output[0])
        self.assertEqual([c async for c in keepalive._hold_body(tick=0.01)], [b"held\n"])

    async def test_no_run_is_held_longer_than_the_ceiling_whatever_it_asks(self):
        with mock.patch.object(keepalive, "SELF_URL", ""):
            run = asyncio.create_task(asyncio.sleep(10))
            keepalive.track(run, minutes=10_000)
            cutoff, given = keepalive._until[run]
            run.cancel()
        self.assertEqual(given, keepalive.MAX_HOLD_MINUTES)


class HoldLoopTests(KeepaliveTestCase):

    def client(self, handler):
        return lambda: httpx.AsyncClient(transport=httpx.MockTransport(handler))

    async def test_it_holds_until_the_last_run_ends_then_lets_go(self):
        asked = []

        async def handler(request: httpx.Request) -> httpx.Response:
            asked.append(str(request.url))
            await asyncio.sleep(0.05)
            return httpx.Response(200, content=b"held\n")

        run = asyncio.create_task(asyncio.sleep(0.2))
        keepalive._tracked.add(run)
        run.add_done_callback(keepalive._tracked.discard)
        with mock.patch.object(keepalive, "SELF_URL", "https://self.invalid"), \
             mock.patch.dict(os.environ, {"POLSON_BASIC_AUTH": "", "K_SERVICE": ""}):
            await asyncio.wait_for(keepalive._hold_loop(self.client(handler), retry=0.01), 3)

        self.assertTrue(run.done())
        self.assertGreaterEqual(len(asked), 2, "a hold that ends while work remains is reopened")
        self.assertTrue(all(u == "https://self.invalid/keepalive/hold" for u in asked))

    async def test_a_refused_or_failing_hold_is_retried_and_never_raises(self):
        answers = iter([httpx.ConnectError("down"), httpx.Response(401), httpx.Response(200, content=b"x")])

        async def handler(request: httpx.Request) -> httpx.Response:
            answer = next(answers, httpx.Response(200, content=b"x"))
            if isinstance(answer, Exception):
                raise answer
            return answer

        run = asyncio.create_task(asyncio.sleep(0.15))
        keepalive._tracked.add(run)
        run.add_done_callback(keepalive._tracked.discard)
        with mock.patch.object(keepalive, "SELF_URL", "https://self.invalid"), \
             mock.patch.dict(os.environ, {"POLSON_BASIC_AUTH": "", "K_SERVICE": ""}), \
             self.assertLogs("polson.keepalive", "ERROR") as log:
            await asyncio.wait_for(keepalive._hold_loop(self.client(handler), retry=0.01), 3)

        self.assertTrue(any("refused with 401" in line for line in log.output))
        self.assertTrue(any("failed" in line for line in log.output))

    async def test_renewal_opens_the_new_hold_before_closing_the_old(self):
        """Measured on Cloud Run: a reopen with no request in flight runs throttled and took 66 s to land, so a
        renewal must never leave a moment with no hold."""
        moments: list[tuple[str, int]] = []
        count = 0

        async def handler(request: httpx.Request) -> httpx.Response:
            nonlocal count
            count += 1
            n = count

            async def body():
                moments.append(("open", n))
                try:
                    while True:
                        yield b".\n"
                        await asyncio.sleep(0.01)
                finally:
                    moments.append(("close", n))
            return httpx.Response(200, content=body())

        self.working(0.3)
        with mock.patch.object(keepalive, "SELF_URL", "https://self.invalid"), \
             mock.patch.dict(os.environ, {"POLSON_BASIC_AUTH": "", "K_SERVICE": ""}):
            await asyncio.wait_for(keepalive._hold_loop(self.client(handler), retry=0.01, renew=0.08), 3)

        self.assertGreaterEqual(count, 3)
        for n in range(2, count + 1):
            self.assertLess(moments.index(("open", n)), moments.index(("close", n - 1)),
                            f"hold {n} opened only after hold {n - 1} closed: {moments}")
        self.assertFalse(keepalive._held.is_set(), "nothing is held once no run works")


    async def test_a_hold_reaching_another_instance_stops_the_asking(self):
        """During a rollout the service URL reaches the new revision while a run still works on the old instance.
        Seen on Cloud Run: the old instance asked every 15 s, keeping the new one warm and helping nothing."""
        asked = []

        async def handler(request: httpx.Request) -> httpx.Response:
            asked.append(request.headers.get(keepalive.INSTANCE_HEADER))
            return httpx.Response(409, text="held for another instance\n")

        run = self.working(5)
        try:
            with mock.patch.object(keepalive, "SELF_URL", "https://self.invalid"), \
                 mock.patch.dict(os.environ, {"POLSON_BASIC_AUTH": "", "K_SERVICE": ""}), \
                 self.assertLogs("polson.keepalive", "WARNING") as log:
                await asyncio.wait_for(keepalive._hold_loop(self.client(handler), retry=0.01), 2)
        finally:
            run.cancel()

        self.assertEqual(asked, [keepalive.INSTANCE], "one ask, carrying this instance's id, then it stops")
        self.assertTrue(any("another instance" in line for line in log.output))


class HeldTests(KeepaliveTestCase):

    async def test_off_it_answers_at_once(self):
        with mock.patch.object(keepalive, "SELF_URL", ""):
            self.assertTrue(await asyncio.wait_for(keepalive.held(5), 0.5))

    async def test_on_it_waits_for_a_hold_and_says_when_none_came(self):
        with mock.patch.object(keepalive, "SELF_URL", "https://self.invalid"):
            later = asyncio.get_running_loop().call_later(0.05, keepalive._held.set)
            self.assertTrue(await keepalive.held(2))
            later.cancel()
            keepalive._held.clear()
            with self.assertLogs("polson.keepalive", "WARNING") as log:
                self.assertFalse(await keepalive.held(0.05))
        self.assertIn("no hold", log.output[0])


class HeaderTests(KeepaliveTestCase):

    async def test_basic_auth_and_an_identity_token_travel_in_separate_headers(self):
        """A private service reads IAM from X-Serverless-Authorization and hands Authorization to the app."""
        async def handler(request: httpx.Request) -> httpx.Response:
            self.assertEqual(request.headers["Metadata-Flavor"], "Google")
            self.assertEqual(request.url.params["audience"], "https://self.invalid")
            return httpx.Response(200, text="id-token\n")

        with mock.patch.object(keepalive, "SELF_URL", "https://self.invalid"), \
             mock.patch.dict(os.environ, {"POLSON_BASIC_AUTH": "judge:pw", "K_SERVICE": "polson-studio"}):
            async with httpx.AsyncClient(transport=httpx.MockTransport(handler)) as web:
                headers = await keepalive._headers(web)

        self.assertEqual(headers["Authorization"], "Basic " + base64.b64encode(b"judge:pw").decode())
        self.assertEqual(headers["X-Serverless-Authorization"], "Bearer id-token")

    async def test_off_cloud_run_and_without_a_password_it_sends_neither(self):
        with mock.patch.dict(os.environ, {"POLSON_BASIC_AUTH": "", "K_SERVICE": ""}):
            async with httpx.AsyncClient() as web:
                self.assertEqual(await keepalive._headers(web), {keepalive.INSTANCE_HEADER: keepalive.INSTANCE})


class MountTests(unittest.TestCase):

    def app(self, probe: str) -> TestClient:
        app = FastAPI()
        with mock.patch.dict(os.environ, {"POLSON_KEEPALIVE_PROBE": probe}):
            keepalive.mount(app)
        return TestClient(app)

    def test_the_hold_route_answers_and_ends_when_nothing_works(self):
        keepalive._tracked.clear()
        page = self.app("").get("/keepalive/hold")

        self.assertEqual(page.status_code, 200)
        self.assertEqual(page.text, "held\n")

    def test_an_instance_refuses_a_hold_sent_for_another(self):
        keepalive._tracked.clear()
        client = self.app("")
        other = client.get("/keepalive/hold", headers={keepalive.INSTANCE_HEADER: "someone-else"})
        own = client.get("/keepalive/hold", headers={keepalive.INSTANCE_HEADER: keepalive.INSTANCE})

        self.assertEqual(other.status_code, 409)
        self.assertEqual(own.status_code, 200)

    def test_the_probe_exists_only_when_asked_for(self):
        """A CPU burner is a measurement, never part of a deployment."""
        self.assertEqual(self.app("").post("/keepalive/probe").status_code, 404)
        self.assertEqual(self.app("0").post("/keepalive/probe").status_code, 404)


if __name__ == "__main__":
    unittest.main()
