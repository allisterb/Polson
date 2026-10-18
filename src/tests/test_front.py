"""The user-facing studio: what a composed request becomes, what the pages serve, and what they refuse.

Nothing here runs the CLI or starts an agent. `projects.create` is replaced by a stand-in that writes
the directory shape the generator writes, and no test presses Start.

    python -m unittest tests.test_front      (from src/)
"""

from __future__ import annotations

import json
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient

from studio import app as app_mod
from studio import catalogue, front, projects, uploads
from studio.runs import Registry, StudioError
from tests.test_studio import project_dir


def events(project: Path, *lines: dict) -> None:
    """Writes a server record for a project, one event per line."""
    with (project / "events" / "server.jsonl").open("a", encoding="utf-8") as out:
        for i, line in enumerate(lines, 1):
            out.write(json.dumps({"ts": f"2026-10-18T10:00:{i:02d}.000Z", "seq": i, "src": "server", **line}) + "\n")


class CatalogueTests(unittest.TestCase):
    """The mapping from a visitor's request to the workflow, type and brief the studio runs."""

    def test_every_route_names_a_workflow_the_studio_ships(self):
        for card in catalogue.CARDS:
            for route in card.routes:
                self.assertIn(route.workflow, projects.WORKFLOWS, f"{card.title}: {route.workflow}")

    def test_every_workflow_but_the_harness_has_a_card(self):
        """The catalogue is a second registration point, like `projects.WORKFLOWS`; this names a missing one."""
        offered = {r.workflow for c in catalogue.CARDS for r in c.routes}
        self.assertEqual(set(projects.WORKFLOWS) - {"harness"} - offered, set())

    def test_every_kind_has_sizes_and_formats(self):
        for card in catalogue.CARDS:
            self.assertTrue(catalogue.SIZES[card.kind] and catalogue.FORMATS[card.kind], card.title)

    def test_a_good_request_parses(self):
        c = catalogue.parse("infographic", "  Apollo   descent ", "0", "blueprint", ["2480x3508"], "1200 x 600",
                            [".svg", ".pdf"], {}, "Fuel figures for the descent.")
        self.assertEqual(c.title, "Apollo descent")
        self.assertEqual(c.workflow, "vector_infographic")
        self.assertEqual([s.key for s in c.sizes], ["2480x3508", "1200x600"])
        self.assertEqual([f.ext for f in c.formats], [".svg", ".pdf"])

    def test_the_second_route_picks_the_second_workflow(self):
        c = catalogue.parse("storyboard", "Board", "2", "", ["1920x1080"], "", [".png"], {}, "Six panels.")
        self.assertEqual(c.workflow, "storyboard_quick")

    def test_refusals_say_what_was_wrong(self):
        good = dict(card_slug="poster", title="Menu", route="0", kind="", sizes=["1200x1800"], custom="",
                    formats=[".png"], extras={}, brief="Autumn menu.", places=["Lisbon"], ages=[""])
        cases = {
            "card_slug": ("logo-and-more", "not one the studio offers"),
            "title": ("   ", "Give the request a name"),
            "route": ("7", "offered routes"),
            "kind": ("swiss", "does not offer"),
            "sizes": (["999x999"], "not a preset"),
            "custom": ("huge", "width × height"),
            "formats": ([".mp4"], "cannot be delivered"),
            "brief": ("  ", "cannot infer"),
            "places": (["   "], "Say who"),
            "ages": (["99_and_up"], "age band"),
        }
        for field, (value, words) in cases.items():
            with self.subTest(field=field):
                with self.assertRaises(StudioError) as caught:
                    catalogue.parse(**{**good, field: value})
                self.assertIn(words, str(caught.exception))

    def test_no_size_and_no_format_are_refused(self):
        with self.assertRaisesRegex(StudioError, "at least one size"):
            catalogue.parse("painting", "Dusk", "0", "", [], "", [".png"], {}, "x")
        with self.assertRaisesRegex(StudioError, "at least one format"):
            catalogue.parse("painting", "Dusk", "0", "", ["1920x1080"], "", [], {}, "x")

    def test_an_extra_outside_its_options_is_refused(self):
        with self.assertRaisesRegex(StudioError, "duration"):
            catalogue.parse("film-trailer", "T", "0", "", ["1920x1080"], "", [".mp4"], {"duration": "45 seconds"},
                            "x", ["Lagos"], [""])

    def test_the_trailer_card_follows_the_trailer_workflow(self):
        """15-second films by default, at most 30; .mp4 and the looping .webp; never a .sif during a run."""
        card = catalogue.BY_SLUG["film-trailer"]
        duration = next(x for x in card.extras if x.key == "duration")
        self.assertEqual((duration.default, duration.options), ("15 seconds", ("15 seconds", "30 seconds")))
        self.assertEqual([f.ext for f in catalogue.FORMATS[card.kind]], [".mp4", ".webp"])
        self.assertTrue(card.audiences and catalogue.BY_SLUG["poster"].audiences)

    def test_the_brief_carries_what_to_deliver_after_the_visitors_words(self):
        c = catalogue.parse("film-trailer", "Harbour trailer", "0", "", ["1920x1080", "1080x1920"], "", [".mp4"],
                            {"duration": "30 seconds"}, "Lanterns on the water.",
                            ["Tokyo", "Mumbai", ""], ["45_to_54", "45_to_54", ""])
        text = c.brief_text(["map.png"])
        self.assertTrue(text.startswith("Lanterns on the water."))
        for line in ("## What to deliver", "Landscape 1920 × 1080 px; Story 1080 × 1920 px", "Formats: .mp4",
                     "Duration: 30 seconds", "documents/: map.png", "Audience A: Tokyo, aged 45 to 54",
                     "Audience B: Mumbai, aged 45 to 54", "One film per audience"):
            self.assertIn(line, text)

    def test_audiences_are_places_with_an_optional_age_band(self):
        self.assertEqual(catalogue.parse_audiences(["  São   Paulo ", "Mexico City", ""], ["", "55_and_older", ""]),
                         [("São Paulo", ""), ("Mexico City", "55_and_older")])
        for places, ages, words in ((["", ""], ["30_to_34", ""], "needs a place"),
                                    (["<script>"], [""], "place name"),
                                    (["A", "B", "C", "D"], [""] * 4, "up to 3")):
            with self.subTest(places=places), self.assertRaisesRegex(StudioError, words):
                catalogue.parse_audiences(places, ages)

    def test_slugify_gives_a_valid_project_id(self):
        for title in ("Harbour festival trailer", "!!!", "Ünïcode café — poster", "x" * 200):
            slug = catalogue.slugify(title)
            self.assertRegex(slug, projects.VALID_ID.pattern)


class UploadNameTests(unittest.TestCase):

    def test_a_plain_name_survives(self):
        self.assertEqual(uploads.safe_filename("fuel figures.csv"), "fuel figures.csv")

    def test_a_path_is_reduced_to_its_name(self):
        self.assertEqual(uploads.safe_filename("..\\..\\x\\data.csv"), "data.csv")
        self.assertEqual(uploads.safe_filename("/etc/data.csv"), "data.csv")

    def test_bad_names_are_refused(self):
        for name in ("", "...", "evil.exe", "a:b.png", ".hidden.png"):
            with self.subTest(name=name), self.assertRaises(StudioError):
                uploads.safe_filename(name)


class StatusTests(unittest.TestCase):

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-front-status-"))

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def status(self, project: Path) -> str:
        from orchestrator.events import read_events
        return front.status_of(project, None, read_events(project / "events" / "server.jsonl"))

    def test_no_record_is_a_draft(self):
        self.assertEqual(self.status(project_dir(self.root, "a")), "draft")

    def test_an_ended_run_with_renders_is_delivered(self):
        p = project_dir(self.root, "b")
        events(p, {"type": "run.start"}, {"type": "render", "artifact": "artifacts/x.webp", "format": "webp"},
               {"type": "run.end"})
        self.assertEqual(self.status(p), "delivered")

    def test_a_record_that_stopped_without_an_end_is_stopped(self):
        p = project_dir(self.root, "c")
        events(p, {"type": "run.start"}, {"type": "render", "artifact": "artifacts/x.webp", "format": "webp"})
        self.assertEqual(self.status(p), "stopped")

    def test_a_final_piece_is_delivered_without_a_run_end(self):
        """Claude Code stops the MCP server without a run.end; the per-audience finals are the evidence."""
        p = project_dir(self.root, "film")
        events(p, {"type": "run.start"}, {"type": "render", "artifact": "artifacts/final_a.mp4", "format": "mp4"})
        self.assertEqual(self.status(p), "delivered")

    def test_the_agents_own_failed_ending_outranks_the_files(self):
        p = project_dir(self.root, "quota")
        events(p, {"type": "run.start"}, {"type": "render", "artifact": "output.png", "format": "png"}, {"type": "run.end"})
        (p / "events" / "agent.jsonl").write_text(json.dumps(
            {"ts": "2026-10-18T10:00:09.000Z", "type": "run.end", "status": "incomplete", "error": "429"}) + "\n")
        self.assertEqual(self.status(p), "stopped")

    def test_a_later_session_without_an_end_is_not_delivered(self):
        p = project_dir(self.root, "d")
        events(p, {"type": "run.start"}, {"type": "render", "artifact": "artifacts/x.webp", "format": "webp"},
               {"type": "run.end"}, {"type": "run.start"})
        self.assertEqual(self.status(p), "stopped")


DIRECTION = """# Direction: two trailers

## Between the audiences

**comparePace (t1 vs t2):** *"t1 (audience A): 54% fast (14 fast, 12 slow); t2 (audience B): 82% fast (23 fast, 5 slow); Fisher's exact p = 0.040, a measurable difference."*

## Film A: Tokyo, 45–54

### Choices

| Choice | Evidence | Kind |
| :-- | :-- | :-- |
| **Pace:** long holds | `pace()` on t1: **14 fast, 12 slow**. Fast titles: *The Mandalorian*, *Akiba Maid War*. Slow titles: *Minari*, *The Farewell*. Against Mumbai it differs | Qloo (t1) |
| **Grade:** soft | t5: *Mirai* | Qloo (t5) |
"""


class LearnedTests(unittest.TestCase):
    """What the request page says the studio learned: summaries and titles, never a Qloo request or response."""

    EVENTS = [{"ts": "2026-10-18T03:45:37Z", "type": "taste.query", "id": "t1", "op": "profile", "label": "audience A",
               "distinctive": 82, "request": "/v2/insights?secret-looking=1"},
              {"ts": "2026-10-18T03:48:00Z", "type": "taste.query", "id": "t3", "op": "describe", "label": "grade A",
               "request": "/v2/insights?filter.type=urn%3Atag"}]

    def test_it_reads_the_summary_the_titles_and_the_profiles(self):
        from studio import direction
        found = direction.learned(DIRECTION, self.EVENTS)
        self.assertEqual(found["compare"][:30], "Audience A: 54% fast (14 fast,")
        self.assertEqual(found["audiences"], [{"title": "Film A: Tokyo, 45–54", "fast": ["The Mandalorian", "Akiba Maid War"],
                                               "slow": ["Minari", "The Farewell"]}])
        self.assertEqual(found["profiles"], [{"label": "audience A", "distinctive": 82}])

    def test_no_request_line_ever_reaches_it(self):
        from studio import direction
        self.assertNotIn("/v2/", json.dumps(direction.learned(DIRECTION, self.EVENTS)))

    def test_a_run_without_a_direction_has_nothing_to_say(self):
        from studio import direction
        self.assertEqual(direction.learned("", []), {"profiles": [], "compare": "", "audiences": []})


class FrontRoutingTests(unittest.TestCase):

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-front-"))
        self.registry = Registry()
        self.client = TestClient(app_mod.create_app(self.root, self.registry))

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def test_the_pages_render(self):
        project_dir(self.root, "acme", workflow="poster")
        for path in ("/home", "/workflows", "/compose/storyboard", "/compose/film-trailer", "/requests",
                     "/requests?view=drafts", "/requests/acme"):
            with self.subTest(path=path):
                self.assertEqual(self.client.get(path).status_code, 200)

    def test_the_stylesheets_and_script_are_served(self):
        for path in ("/static/polson.css", "/static/front.css", "/static/front.js", "/static/gallery/storyboard.webp"):
            self.assertEqual(self.client.get(path).status_code, 200, path)

    def test_an_unknown_workflow_or_request_is_a_404(self):
        for path in ("/compose/nope", "/requests/nope", "/requests/..%2F..%2Fetc", "/requests/nope/thumb"):
            self.assertEqual(self.client.get(path).status_code, 404, path)

    def test_a_request_shows_its_title_not_its_directory(self):
        p = project_dir(self.root, "harbour-trailer", workflow="trailer")
        (p / front.REQUEST_FILE).write_text(json.dumps({"title": "Harbour festival trailer", "card": "film-trailer"}))
        page = self.client.get("/requests")
        self.assertIn("Harbour festival trailer", self.client.get("/requests?view=drafts").text)
        self.assertEqual(page.status_code, 200)

    def test_the_thumbnail_is_the_newest_recorded_image(self):
        p = project_dir(self.root, "pic", workflow="painting")
        (p / "artifacts" / "one.webp").write_bytes(b"one")
        (p / "artifacts" / "two.png").write_bytes(b"two")
        events(p, {"type": "render", "artifact": "artifacts/one.webp", "format": "webp"},
               {"type": "render", "artifact": "artifacts/two.png", "format": "png"})
        self.assertEqual(self.client.get("/requests/pic/thumb").content, b"two")

    def test_the_thumbnail_never_reaches_documents_or_leaves_the_project(self):
        p = project_dir(self.root, "sly", workflow="painting")
        (p / "documents").mkdir()
        (p / "documents" / "private.png").write_bytes(b"private")
        (self.root / "outside.png").write_bytes(b"outside")
        events(p, {"type": "render", "artifact": "documents/private.png", "format": "png"},
               {"type": "render", "artifact": "../outside.png", "format": "png"},
               {"type": "render", "artifact": "artifacts/../documents/private.png", "format": "png"})
        self.assertEqual(self.client.get("/requests/sly/thumb").status_code, 404)

    def compose(self, **overrides):
        data = {"card": "poster", "title": "Autumn menu poster", "route": "0", "kind": "film",
                "sizes": ["1200x1800"], "formats": [".png", ".pdf"], "brief": "An autumn menu.",
                "action": "save", "x_headline": "Autumn at the Lantern",
                "aud_place": ["Lisbon", "", ""], "aud_age": ["55_and_older", "", ""]}
        data.update(overrides)
        return data

    def fake_create(self):
        """Stands in for the CLI: writes a project directory and keeps the brief it was handed."""
        handed = {}

        async def create(root, project_id, workflow, kind, brief):
            handed.update(project_id=project_id, workflow=workflow, kind=kind, brief=brief)
            return project_dir(root, project_id, workflow=workflow)
        return handed, mock.patch.object(projects, "create", create)

    def test_composing_makes_a_project_with_the_delivery_in_its_brief(self):
        handed, patch = self.fake_create()
        with patch:
            reply = self.client.post("/requests", data=self.compose(),
                                     files=[("files", ("menu notes.txt", b"pumpkin soup", "text/plain"))],
                                     follow_redirects=False)

        self.assertEqual(reply.status_code, 303)
        self.assertEqual(reply.headers["location"], "/requests/autumn-menu-poster")
        self.assertEqual((handed["workflow"], handed["kind"]), ("poster", "film"))
        self.assertIn("Formats: .png, .pdf", handed["brief"])
        self.assertIn("Headline: Autumn at the Lantern", handed["brief"])
        self.assertIn("Audience A: Lisbon, aged 55 and older", handed["brief"])
        self.assertIn("documents/: menu notes.txt", handed["brief"])

        made = self.root / "autumn-menu-poster"
        self.assertEqual((made / "documents" / "menu notes.txt").read_bytes(), b"pumpkin soup")
        record = json.loads((made / front.REQUEST_FILE).read_text())
        self.assertEqual(record["title"], "Autumn menu poster")
        self.assertEqual(record["files"], ["menu notes.txt"])

    def test_a_second_request_of_the_same_name_gets_its_own_directory(self):
        _, patch = self.fake_create()
        with patch:
            self.client.post("/requests", data=self.compose(), follow_redirects=False)
            reply = self.client.post("/requests", data=self.compose(), follow_redirects=False)
        self.assertEqual(reply.headers["location"], "/requests/autumn-menu-poster-2")

    def test_a_refused_request_keeps_what_was_typed_and_makes_nothing(self):
        handed, patch = self.fake_create()
        with patch:
            reply = self.client.post("/requests", data=self.compose(formats=[".mp4"]), follow_redirects=False)
        self.assertEqual(reply.status_code, 409)
        self.assertIn("cannot be delivered", reply.text)
        self.assertIn("An autumn menu.", reply.text)
        self.assertEqual(handed, {})
        self.assertEqual(list(self.root.iterdir()), [])

    def test_a_bad_file_refuses_the_request_before_a_project_exists(self):
        handed, patch = self.fake_create()
        with patch:
            reply = self.client.post("/requests", data=self.compose(),
                                     files=[("files", ("run.exe", b"MZ", "application/octet-stream"))],
                                     follow_redirects=False)
        self.assertEqual(reply.status_code, 409)
        self.assertIn("not a type the studio can read", reply.text)
        self.assertEqual(handed, {})

    def test_a_poster_without_an_audience_is_refused(self):
        handed, patch = self.fake_create()
        with patch:
            reply = self.client.post("/requests", data=self.compose(aud_place=["", "", ""], aud_age=["", "", ""]),
                                     follow_redirects=False)
        self.assertEqual(reply.status_code, 409)
        self.assertIn("Say who the poster is for", reply.text)
        self.assertEqual(handed, {})

    def test_the_request_page_shows_what_the_studio_learned(self):
        p = project_dir(self.root, "pair", workflow="trailer")
        (p / "direction.md").write_text(DIRECTION, encoding="utf-8")
        events(p, {"type": "taste.query", "id": "t1", "op": "profile", "label": "audience A", "distinctive": 82,
                   "request": "/v2/insights?x=1"})
        for path in ("/requests/pair", "/requests/pair/learned"):
            page = self.client.get(path).text
            self.assertIn("What the studio learned", page, path)
            self.assertIn("The Mandalorian", page, path)
            self.assertNotIn("/v2/insights", page, path)

    def test_a_project_from_another_host_is_shown_but_not_offered_a_run(self):
        p = project_dir(self.root, "elsewhere", workflow="painting", policy=False)
        page = self.client.get("/requests/elsewhere").text
        self.assertIn("cannot continue it", page)
        self.assertNotIn("Start in the studio</button>", page)

    def test_an_observing_studio_offers_the_form_and_refuses_to_make(self):
        client = TestClient(app_mod.create_app(self.root, Registry(), observe_only=True, create_at="/new"))
        page = client.get("/compose/poster")
        self.assertIn("can watch requests but not make them", page.text)
        reply = client.post("/requests", data=self.compose(), follow_redirects=False)
        self.assertEqual(reply.status_code, 409)
        self.assertEqual(list(self.root.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
