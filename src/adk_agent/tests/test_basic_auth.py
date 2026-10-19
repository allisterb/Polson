"""`basic_auth`: the one password in front of the public deployment.

The refusals matter most. A gate that lets a wrong password through, or a misconfiguration that leaves the server
open while looking configured, is worse than no gate, because nobody then checks.
"""

from __future__ import annotations

import base64
import sys
import unittest
from pathlib import Path

from fastapi import FastAPI, WebSocket
from fastapi.responses import StreamingResponse
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import basic_auth  # noqa: E402


def header(user: str, password: str) -> dict[str, str]:
    return {"Authorization": "Basic " + base64.b64encode(f"{user}:{password}".encode()).decode()}


def make_app(value: str | None) -> tuple[FastAPI, str]:
    app = FastAPI()

    @app.get("/studio/home")
    async def home() -> dict[str, str]:
        return {"page": "home"}

    @app.get("/events")
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

    said = basic_auth.install(app, value)
    return app, said


class BasicAuthTests(unittest.TestCase):

    def setUp(self) -> None:
        app, self.said = make_app("judge:s3cret:with:colons")
        self.client = TestClient(app)

    def test_no_credential_is_asked_for_one(self):
        """The browser shows its sign-in prompt only when the 401 names the scheme."""
        page = self.client.get("/studio/home")

        self.assertEqual(page.status_code, 401)
        self.assertEqual(page.headers["www-authenticate"], 'Basic realm="Polson Studio", charset="UTF-8"')
        self.assertNotIn("home", page.text)

    def test_the_right_credential_passes(self):
        page = self.client.get("/studio/home", headers=header("judge", "s3cret:with:colons"))

        self.assertEqual(page.status_code, 200)
        self.assertEqual(page.json(), {"page": "home"})

    def test_a_wrong_password_or_user_is_refused(self):
        for user, password in (("judge", "s3cret"), ("judge", "s3cret:with:colon"), ("Judge", "s3cret:with:colons"),
                               ("", "s3cret:with:colons"), ("judge", "")):
            self.assertEqual(self.client.get("/studio/home", headers=header(user, password)).status_code, 401,
                             (user, password))

    def test_malformed_headers_are_refused_not_raised(self):
        for value in ("Basic", "Basic !!!not-base64", "Bearer abc",
                      "Basic " + base64.b64encode(b"no-colon-here").decode(),
                      "Basic " + base64.b64encode("judge:s3cret:with:colons".encode()).decode() + "garbage"):
            self.assertEqual(self.client.get("/studio/home", headers={"Authorization": value}).status_code, 401, value)

    def test_the_scheme_is_case_insensitive(self):
        """RFC 7617 names the scheme case-insensitively, and some clients send `basic`."""
        token = header("judge", "s3cret:with:colons")["Authorization"].split(" ", 1)[1]

        self.assertEqual(self.client.get("/studio/home", headers={"Authorization": "basic " + token}).status_code, 200)

    def test_a_stream_passes_through_whole(self):
        """The run page's events are a stream that must not be buffered or cut by the gate."""
        page = self.client.get("/events", headers=header("judge", "s3cret:with:colons"))

        self.assertEqual(page.text, "data: one\n\ndata: two\n\n")
        self.assertEqual(self.client.get("/events").status_code, 401)

    def test_a_websocket_needs_the_credential_too(self):
        with self.assertRaises(WebSocketDisconnect):
            with self.client.websocket_connect("/run_live") as ws:
                ws.receive_text()

        with self.client.websocket_connect("/run_live", headers=header("judge", "s3cret:with:colons")) as ws:
            self.assertEqual(ws.receive_text(), "hello")

    def test_startup_says_it_is_on_and_for_whom_but_not_the_password(self):
        self.assertIn("basic auth on", self.said)
        self.assertIn("'judge'", self.said)
        self.assertNotIn("s3cret", self.said)


class ConfigurationTests(unittest.TestCase):

    def test_unset_or_blank_leaves_it_open_and_says_so(self):
        for value in (None, "", "   \n"):
            app, said = make_app(value)
            self.assertEqual(TestClient(app).get("/studio/home").status_code, 200)
            self.assertIn("OFF", said)

    def test_a_malformed_value_stops_startup_rather_than_opening_the_server(self):
        for value in ("judge", "judge:", ":s3cret", ":"):
            with self.assertRaises(ValueError, msg=value):
                make_app(value)

    def test_a_trailing_newline_from_a_secret_file_is_not_part_of_the_password(self):
        app, _ = make_app("judge:s3cret\n")

        self.assertEqual(TestClient(app).get("/studio/home", headers=header("judge", "s3cret")).status_code, 200)


if __name__ == "__main__":
    unittest.main()
