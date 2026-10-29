"""`api_gate`: what a visitor can reach when the deployment has no sign-in.

The open side matters as much as the closed one: a gate that closes the studio's event stream breaks the demo, and
one that leaves `/run_sse` open lets anyone start runs past the allowance.
"""

from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path
from unittest import mock

from fastapi import FastAPI, WebSocket
from fastapi.responses import StreamingResponse
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import api_gate  # noqa: E402


def make_app() -> FastAPI:
    app = FastAPI()

    for path in ("/", "/new", "/health", "/studio", "/studio/home", "/list-apps", "/keepalive/hold", "/docs",
                 "/apps/x/users/user/sessions", "/studios", "/dev-ui/"):
        app.add_api_route(path, lambda path=path: {"path": path}, methods=["GET"])

    @app.post("/projects")
    async def projects() -> dict[str, str]:
        return {"made": "yes"}

    @app.post("/run_sse")
    async def run_sse() -> dict[str, str]:
        return {"ran": "yes"}

    @app.get("/studio/runs/x/events")
    async def events() -> StreamingResponse:
        async def stream():
            yield b"data: one\n\n"
            yield b"data: two\n\n"
        return StreamingResponse(stream(), media_type="text/event-stream")

    @app.websocket("/run_live")
    async def live(ws: WebSocket) -> None:
        await ws.accept()
        await ws.send_text("hello")
        await ws.close()

    app.add_middleware(api_gate.ApiGate)
    return app


class ApiGateTests(unittest.TestCase):

    def setUp(self) -> None:
        self.client = TestClient(make_app())

    def test_the_studio_and_the_intake_form_are_open(self):
        for path in ("/", "/new", "/health", "/studio", "/studio/home"):
            self.assertEqual(self.client.get(path).status_code, 200, path)
        self.assertEqual(self.client.post("/projects").json(), {"made": "yes"})

    def test_the_studio_event_stream_passes_through_whole(self):
        with self.client.stream("GET", "/studio/runs/x/events") as page:
            self.assertEqual(page.status_code, 200)
            self.assertEqual(b"".join(page.iter_bytes()), b"data: one\n\ndata: two\n\n")

    def test_adk_api_and_everything_unnamed_answers_as_though_absent(self):
        for path in ("/list-apps", "/keepalive/hold", "/docs", "/apps/x/users/user/sessions", "/dev-ui/",
                     "/studios", "/nothing-here"):
            page = self.client.get(path)
            self.assertEqual((page.status_code, page.json()), (404, {"detail": "Not Found"}), path)
        self.assertEqual(self.client.post("/run_sse").status_code, 404)

    def test_the_process_token_lets_its_own_requests_through(self):
        self.assertEqual(self.client.post("/run_sse", headers=api_gate.internal()).json(), {"ran": "yes"})
        self.assertEqual(self.client.get("/keepalive/hold", headers=api_gate.internal()).status_code, 200)

    def test_a_wrong_or_empty_token_is_refused(self):
        for value in ("", "x", api_gate.TOKEN[:-1], api_gate.TOKEN + "x"):
            self.assertEqual(self.client.post("/run_sse", headers={api_gate.HEADER: value}).status_code, 404, value)

    def test_a_websocket_handshake_is_refused_without_the_token(self):
        with self.assertRaises(WebSocketDisconnect):
            with self.client.websocket_connect("/run_live") as ws:
                ws.receive_text()
        with self.client.websocket_connect("/run_live", headers=api_gate.internal()) as ws:
            self.assertEqual(ws.receive_text(), "hello")

    def test_on_by_default_only_under_cloud_run(self):
        cases = (({"K_SERVICE": "svc", "POLSON_GATE_API": ""}, True), ({"K_SERVICE": "", "POLSON_GATE_API": ""}, False),
                 ({"K_SERVICE": "svc", "POLSON_GATE_API": "0"}, False), ({"K_SERVICE": "", "POLSON_GATE_API": "1"}, True))
        for env, expected in cases:
            with mock.patch.dict(os.environ, env):
                self.assertEqual(api_gate.enabled(), expected, env)

    def test_install_says_what_it_did(self):
        with mock.patch.dict(os.environ, {"POLSON_GATE_API": "1"}):
            app = FastAPI()
            self.assertIn("API gate on", api_gate.install(app))
            self.assertEqual(TestClient(app).get("/openapi.json").status_code, 404)
        with mock.patch.dict(os.environ, {"POLSON_GATE_API": "0"}):
            app = FastAPI()
            self.assertIn("API gate OFF", api_gate.install(app))
            self.assertEqual(TestClient(app).get("/openapi.json").status_code, 200)


if __name__ == "__main__":
    unittest.main()
