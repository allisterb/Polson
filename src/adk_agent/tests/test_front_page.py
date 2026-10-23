"""`front_page`: `/` goes to the client studio, and ADK's console stays where it was.

Built on ADK's own `get_fast_api_app(web=True)`, not a stand-in, so an ADK upgrade that registers its root redirect
differently fails here rather than silently putting the console back on the front page.
"""

from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

from fastapi import FastAPI
from fastapi.testclient import TestClient
from google.adk.cli.fast_api import get_fast_api_app

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import front_page  # noqa: E402


class FrontDoorTests(unittest.TestCase):

    def test_adks_console_redirect_is_replaced_and_the_console_stays(self):
        with tempfile.TemporaryDirectory() as agents:
            app = get_fast_api_app(agents_dir=agents, web=True)
            client = TestClient(app)
            self.assertEqual(client.get("/", follow_redirects=False).headers["location"], "/dev-ui/")

            said = front_page.install(app, front_page.STUDIO_HOME)

            root = client.get("/", follow_redirects=False)
            self.assertEqual((root.status_code, root.headers["location"]), (307, "/studio/home"))
            self.assertIn("replacing", said)
            self.assertEqual(client.get("/dev-ui", follow_redirects=False).headers["location"], "/dev-ui/")

    def test_an_app_with_no_root_route_gets_one(self):
        app = FastAPI()
        said = front_page.install(app, "/studio/home")
        self.assertEqual(TestClient(app).get("/", follow_redirects=False).headers["location"], "/studio/home")
        self.assertNotIn("replacing", said)


if __name__ == "__main__":
    unittest.main()
