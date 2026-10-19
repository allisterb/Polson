"""Requests that outlived their instance: rebuilt from the archive, and shown exactly as any other request.

A Cloud Run instance is replaced when it idles or a revision rolls out, and its disk goes with it. Found on
2026-10-19: a delivered poster vanished from the Requests page while its files sat in the bucket. Nothing here talks to
Google Cloud Storage; the archive's bucket is the fake `test_archive` already uses.

    python -m unittest discover -s tests
"""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient

from studio import app as app_mod
from studio import front as front_mod
from studio.runs import Registry

from test_archive import FakeBlob, FakeBucket, with_archive

PNG = b"\x89PNG\r\n\x1a\n" + b"\x00" * 64


def jsonl(*events: dict) -> bytes:
    return "".join(json.dumps(e) + "\n" for e in events).encode()


def archived_poster(name: str = "lantern") -> FakeBucket:
    """A delivered poster as `mirror.py` leaves it in the bucket."""
    base = f"mirror/{name}/"
    blobs = [
        FakeBlob(base + "project.json", json.dumps({"workflow": "poster", "sdk": "adk"}).encode()),
        FakeBlob(base + "request.json", json.dumps({"title": "Lantern Keeper poster", "card": "poster",
                                                    "workflow": "poster"}).encode()),
        FakeBlob(base + "brief.md", b"An original film, The Lantern Keeper."),
        FakeBlob(base + "events/server.jsonl", jsonl(
            {"ts": "2026-10-19T18:51:00Z", "seq": 1, "src": "server", "type": "run.start"},
            {"ts": "2026-10-19T19:10:00Z", "seq": 2, "src": "server", "type": "render", "artifact": "artifacts/final_a.png"},
            {"ts": "2026-10-19T19:13:00Z", "seq": 3, "src": "server", "type": "run.end"})),
        FakeBlob(base + "events/agent.jsonl", jsonl(
            {"ts": "2026-10-19T18:51:00Z", "seq": 1, "src": "agent", "type": "run.begin"},
            {"ts": "2026-10-19T19:13:00Z", "seq": 2, "src": "agent", "type": "run.end", "reason": "completed"})),
        FakeBlob(base + "artifacts/final_a.png", PNG),
        FakeBlob(base + "artifacts/final_a.svg", b"<svg/>"),
        FakeBlob(base + "direction.md", b"# Direction\n\nWhy it looks like this."),
        FakeBlob(base + "findings.md", b"# Findings"),
    ]
    return FakeBucket(blobs, prefixes=[base])


class RebuildTests(unittest.TestCase):

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-rebuild-"))
        self.client = TestClient(app_mod.create_app(self.root, Registry()))
        front_mod._partial.clear()
        front_mod._checked_at = float("-inf")

    def test_an_archived_request_is_listed_as_it_was(self):
        with with_archive(archived_poster()):
            page = self.client.get("/requests?view=done")

        self.assertEqual(page.status_code, 200)
        self.assertIn("Lantern Keeper poster", page.text, "listed under its own title, not its folder name")
        self.assertIn("/requests/lantern", page.text)
        self.assertNotIn("rchive", page.text, "nothing on the page says where the files were")

    def test_listing_brings_back_only_what_a_row_needs(self):
        """The disk is memory on Cloud Run: the list must not restore every archived video to draw itself."""
        with with_archive(archived_poster()):
            self.client.get("/requests")

        project = self.root / "lantern"
        for light in ("project.json", "request.json", "brief.md", "events/server.jsonl", "artifacts/final_a.png"):
            self.assertTrue((project / light).is_file(), light)
        for heavy in ("artifacts/final_a.svg", "direction.md", "findings.md"):
            self.assertFalse((project / heavy).exists(), heavy)

    def test_the_thumbnail_is_served(self):
        with with_archive(archived_poster()):
            self.client.get("/requests")
            thumb = self.client.get("/requests/lantern/thumb")

        self.assertEqual(thumb.status_code, 200)
        self.assertEqual(thumb.content, PNG)

    def test_opening_it_brings_back_the_rest(self):
        with with_archive(archived_poster()):
            self.client.get("/requests")
            page = self.client.get("/requests/lantern")

        self.assertEqual(page.status_code, 200)
        self.assertTrue((self.root / "lantern" / "direction.md").is_file())
        self.assertTrue((self.root / "lantern" / "artifacts" / "final_a.svg").is_file())
        self.assertNotIn("lantern", front_mod._partial)
        self.assertNotIn("rchive", page.text)
        self.assertNotIn("estored", page.text)

    def test_a_request_opened_by_its_address_is_brought_back_whole(self):
        """A bookmarked address after the instance recycled, before anyone looked at the list."""
        with with_archive(archived_poster()):
            page = self.client.get("/requests/lantern")

        self.assertEqual(page.status_code, 200)
        self.assertTrue((self.root / "lantern" / "direction.md").is_file())

    def test_a_request_still_here_is_never_overwritten(self):
        project = self.root / "lantern"
        (project / "events").mkdir(parents=True)
        (project / "project.json").write_text(json.dumps({"workflow": "poster", "sdk": "adk"}), encoding="utf-8")
        (project / "brief.md").write_text("the live one", encoding="utf-8")

        with with_archive(archived_poster()):
            self.client.get("/requests")
            self.client.get("/requests/lantern")

        self.assertEqual((project / "brief.md").read_text(encoding="utf-8"), "the live one")
        self.assertFalse((project / "direction.md").exists())

    def test_the_archive_is_not_listed_on_every_page_load(self):
        bucket = archived_poster()
        listings = []
        real = bucket.list_blobs

        def counting(b, prefix="", delimiter=None):
            if delimiter:
                listings.append(prefix)
            return real(b, prefix, delimiter)

        with with_archive(bucket), mock.patch.object(bucket, "list_blobs", side_effect=counting):
            for _ in range(3):
                self.client.get("/requests")
                self.client.get("/home")

        self.assertEqual(len(listings), 1)

    def test_without_an_archive_nothing_changes(self):
        with mock.patch.object(front_mod.archive, "ARCHIVE_URI", ""):
            page = self.client.get("/requests?view=done")

        self.assertEqual(page.status_code, 200)
        self.assertFalse((self.root / "lantern").exists())


if __name__ == "__main__":
    unittest.main()
