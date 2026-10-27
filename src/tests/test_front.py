"""The user-facing studio: what a composed request becomes, what the pages serve, and what they refuse.

Nothing here runs the CLI or starts an agent. `projects.create` is replaced by a stand-in that writes
the directory shape the generator writes, and no test presses Start.

    python -m unittest tests.test_front      (from src/)
"""

from __future__ import annotations

import json
import re
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient

from studio import app as app_mod
from studio import catalogue, front, projects, techniques, uploads
from studio.runs import Registry, StudioError
from tests.test_studio import project_dir


#: The design technique catalogue the form is tested against, pinned rather than read from a CLI that may not be built.
TECHNIQUES = (
    techniques.Technique("alternative-grid", "Alternative grid", ("poster", "motion"), "Manual 30 §2", "A grid from an object."),
    techniques.Technique("mockups", "Mock-ups", ("poster", "motion", "logo"), "Manual 30 §5", "The work where it lives."),
    techniques.Technique("mystery-reveal", "Mystery → reveal", ("motion",), "Manual 25 §14a", "Opens on a mystery.", measured=False),
)


def pin_techniques(test: unittest.TestCase) -> None:
    """Serves `TECHNIQUES` as the catalogue for the length of one test."""
    patcher = mock.patch.object(techniques, "catalogue", lambda: TECHNIQUES)
    patcher.start()
    test.addCleanup(patcher.stop)


def events(project: Path, *lines: dict) -> None:
    """Writes a server record for a project, one event per line."""
    with (project / "events" / "server.jsonl").open("a", encoding="utf-8") as out:
        for i, line in enumerate(lines, 1):
            out.write(json.dumps({"ts": f"2026-10-18T10:00:{i:02d}.000Z", "seq": i, "src": "server", **line}) + "\n")


class TechniqueCatalogueTests(unittest.TestCase):
    """The form's catalogue is the CLI's; a CLI that cannot answer hides the field rather than failing the form."""

    def tearDown(self) -> None:
        techniques.reset()

    def test_a_cli_that_cannot_run_offers_no_techniques(self):
        techniques.reset()
        with mock.patch.object(projects, "launcher", lambda: [str(Path(tempfile.gettempdir()) / "no-such-polson.exe")]):
            self.assertEqual(techniques.catalogue(), ())
            self.assertEqual(techniques.for_workflow("poster"), ())

    def test_choices_are_ordered_as_the_catalogue_is(self):
        with mock.patch.object(techniques, "catalogue", lambda: TECHNIQUES):
            self.assertEqual(techniques.parse("motion", {"mystery-reveal": "deny", "mockups": "request",
                                                         "alternative-grid": "request", "x": ""}),
                             (["alternative-grid", "mockups"], ["mystery-reveal"]))
            self.assertEqual(techniques.names(["mockups", "gone"]), ["Mock-ups", "gone"])


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

    def test_a_product_ad_is_a_trailer_of_type_product(self):
        c = catalogue.parse("product-ad", "Nightshift", "0", "", ["1920x1080"], "", [".mp4"],
                            {"product": "Nightshift, a canned cold brew", "comparables": "Blue Bottle, La Colombe",
                             "claim": "Steeped for twenty hours.", "cta": "Find it chilled."},
                            "An ad for a cold brew.", ["Berlin", "Lagos"], ["30_to_34", ""])
        self.assertEqual((c.workflow, c.kind), ("motion", "product"))
        text = c.brief_text([])
        for line in ("- One ad per audience", "- Comparable brands: Blue Bottle, La Colombe",
                     "- Claim: Steeped for twenty hours.", "- Call to action: Find it chilled."):
            self.assertIn(line, text)

    def test_a_film_trailer_never_becomes_a_product_ad(self):
        """One workflow, two cards: the film trailer offers no type, so it cannot be given the ad's."""
        film = catalogue.BY_SLUG["film-trailer"]
        self.assertEqual(catalogue.types_for(film, "motion"), [])
        self.assertIs(catalogue.card_for("motion"), film, "an untyped motion project made elsewhere is a film trailer")

    def test_a_motion_project_made_elsewhere_is_named_by_its_type(self):
        """
        A headless run records no card, so the card comes from the workflow and the type: every one of them used to be
        "Film trailer". A project.json from before the rename says `trailer`, and reads the same.
        """
        for workflow in ("motion", "trailer"):
            self.assertIs(catalogue.card_for(workflow, "product"), catalogue.BY_SLUG["product-ad"], workflow)
            self.assertIs(catalogue.card_for(workflow, "report"), catalogue.BY_SLUG["report-explainer"], workflow)
            self.assertIs(catalogue.card_for(workflow, "pitch"), catalogue.BY_SLUG["startup-pitch"], workflow)
            self.assertIs(catalogue.card_for(workflow, ""), catalogue.BY_SLUG["film-trailer"], workflow)
        self.assertIs(catalogue.card_for("poster", "product"), catalogue.BY_SLUG["poster"])
        with self.assertRaises(StudioError):
            catalogue.parse("film-trailer", "Harbour", "0", "product", ["1920x1080"], "", [".mp4"], {}, "A film.",
                            ["Tokyo"], [""])
        c = catalogue.parse("film-trailer", "Harbour", "0", "", ["1920x1080"], "", [".mp4"], {}, "A film.",
                            ["Tokyo", "Mumbai"], ["", ""])
        self.assertEqual(c.kind, "")
        self.assertIn("- One film per audience", c.brief_text([]))

    def test_a_report_explainer_is_a_trailer_of_type_report_with_its_questions_numbered(self):
        c = catalogue.parse("report-explainer", "NVIDIA explained", "0", "", ["1920x1080"], "", [".mp4"],
                            {"viewer": "New retail investors", "questions": "How big was revenue?; Where did it come from?"},
                            "Explain the annual report.", [], [])
        self.assertEqual((c.workflow, c.kind), ("motion", "report"))
        text = c.brief_text(["nvda-10k.pdf"])
        for line in ("- Who is watching: New retail investors",
                     "- Their questions:\n  1. How big was revenue?\n  2. Where did it come from?",
                     "- Files supplied, in documents/: nvda-10k.pdf"):
            self.assertIn(line, text)
        self.assertNotIn("per audience", text)

    def test_a_report_explainers_long_fields_keep_one_question_a_line(self):
        c = catalogue.parse("report-explainer", "NVIDIA explained", "0", "", ["1920x1080"], "", [".mp4"],
                            {"viewer": "Retail investors\nwho know the products", "questions": "How big was revenue?\n\nWhere did it come from?\n"},
                            "Explain the annual report.", [], [])
        text = c.brief_text(["nvda-10k.pdf"])
        self.assertIn("- Who is watching: Retail investors who know the products", text)
        self.assertIn("- Their questions:\n  1. How big was revenue?\n  2. Where did it come from?", text)
        with self.assertRaisesRegex(StudioError, "keep it under 2000"):
            catalogue.parse("report-explainer", "Long", "0", "", ["1920x1080"], "", [".mp4"],
                            {"questions": "Why? " * 500}, "Explain.", [], [])

    def test_a_startup_pitch_is_a_trailer_of_type_pitch_with_its_terms_in_the_brief(self):
        c = catalogue.parse("startup-pitch", "UberCab retold", "0", "", ["1920x1080"], "", [".mp4"],
                            {"company": "UberCab", "told_as": "A retelling of a past deck", "raise": "$1.25M seed"},
                            "Tell the 2008 deck.", [], [])
        self.assertEqual((c.workflow, c.kind), ("motion", "pitch"))
        text = c.brief_text(["uber-2008.pdf"])
        for line in ("- Company: UberCab", "- Told as: A retelling of a past deck", "- Stage: As the deck says",
                     "- The raise: $1.25M seed", "- Duration: 2 minutes", "- Narration: A voice",
                     "- Files supplied, in documents/: uber-2008.pdf"):
            self.assertIn(line, text)
        self.assertNotIn("Use of funds", text, "a field left blank is not asked for")
        card = catalogue.BY_SLUG["startup-pitch"]
        self.assertTrue(card.audiences_optional)
        with self.assertRaisesRegex(StudioError, "attach the PDF"):
            card.check_files(["model.csv"])
        with self.assertRaisesRegex(StudioError, "told as from the list"):
            catalogue.parse("startup-pitch", "X", "0", "", ["1920x1080"], "", [".mp4"], {"told_as": "Both"}, "A pitch.")

    def test_a_report_explainer_needs_its_pdf(self):
        card = catalogue.BY_SLUG["report-explainer"]
        with self.assertRaisesRegex(StudioError, "attach the PDF"):
            card.check_files(["notes.txt", "spot.mp4"])
        card.check_files(["Annual Report.PDF"])
        catalogue.BY_SLUG["product-ad"].check_files([])     # a card that needs nothing refuses nothing

    def test_a_poster_may_be_a_product_ad(self):
        self.assertIn(("product", "Product ad"), catalogue.types_for(catalogue.BY_SLUG["poster"], "poster"))

    def test_audiences_are_places_with_an_optional_age_band(self):
        self.assertEqual(catalogue.parse_audiences(["  São   Paulo ", "Mexico City", ""], ["", "55_and_older", ""]),
                         [("São Paulo", ""), ("Mexico City", "55_and_older")])
        for places, ages, words in ((["", ""], ["30_to_34", ""], "needs a place"),
                                    (["<script>"], [""], "place name"),
                                    (list("ABCDEFG"), [""] * 7, "up to 6")):
            with self.subTest(places=places), self.assertRaisesRegex(StudioError, words):
                catalogue.parse_audiences(places, ages)

    def test_a_report_explainers_audiences_are_optional(self):
        """With places, a film per audience in its conventions; without, one film for whoever the brief describes."""
        none = catalogue.parse("report-explainer", "NVDA", "0", "", ["1920x1080"], "", [".mp4"], {}, "Explain the 10-K.")
        self.assertEqual([], none.audiences)
        two = catalogue.parse("report-explainer", "NVDA", "0", "", ["1920x1080"], "", [".mp4"], {}, "Explain the 10-K.",
                              places=["Mumbai", "Tokyo"], ages=["35_to_44", ""])
        self.assertEqual([("Mumbai", "35_to_44"), ("Tokyo", "")], two.audiences)
        text = two.brief_text([])
        self.assertIn("Audience A: Mumbai", text)
        self.assertIn("One film per audience", text)
        self.assertTrue(catalogue.BY_SLUG["report-explainer"].audiences_optional)
        self.assertFalse(catalogue.BY_SLUG["poster"].audiences_optional)

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

    def test_a_run_started_elsewhere_reads_as_running_while_its_record_moves(self):
        """nightshift1 (claude -p) read as Stopped while its server wrote every few seconds: a moving record is a live run."""
        from datetime import datetime, timedelta, timezone

        def stamp(seconds_ago):
            return (datetime.now(timezone.utc) - timedelta(seconds=seconds_ago)).strftime("%Y-%m-%dT%H:%M:%S.000Z")

        live = project_dir(self.root, "live")
        events(live, {"type": "run.start", "ts": stamp(600)}, {"type": "script.ok", "ts": stamp(20)})
        self.assertEqual(self.status(live), "running")

        cold = project_dir(self.root, "cold")
        events(cold, {"type": "run.start", "ts": stamp(4000)}, {"type": "script.ok", "ts": stamp(3600)})
        self.assertEqual(self.status(cold), "stopped")

        done = project_dir(self.root, "done")
        events(done, {"type": "run.start", "ts": stamp(600)}, {"type": "render", "artifact": "output.png", "ts": stamp(30)},
               {"type": "run.end", "ts": stamp(20)})
        self.assertEqual(self.status(done), "delivered")

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
    """What the request page says the studio learned: summaries and titles in plain words, no request line or payload."""

    EVENTS = [{"ts": "2026-10-18T03:45:37Z", "type": "taste.query", "id": "t1", "op": "profile", "label": "audience A",
               "distinctive": 82, "request": "/v2/insights?secret-looking=1"},
              {"ts": "2026-10-18T03:48:00Z", "type": "taste.query", "id": "t3", "op": "describe", "label": "grade A",
               "request": "/v2/insights?filter.type=urn%3Atag"}]

    def test_it_reads_the_summary_the_titles_and_the_profiles(self):
        from studio import direction
        found = direction.learned(DIRECTION, self.EVENTS)
        self.assertEqual(found["compare"][:30], "Audience A: 54% fast (14 fast,")
        # "Film A" in the direction is the profile labelled "audience A": the letter ties them.
        self.assertEqual(found["audiences"], [{"name": "Audience A", "who": "", "distinctive": 82, "pace": None,
                                               "fast": ["The Mandalorian", "Akiba Maid War"],
                                               "slow": ["Minari", "The Farewell"]}])

    def test_the_pace_counts_come_from_the_profile_before_any_direction(self):
        from studio import direction
        profile = {"type": "taste.query", "id": "t4", "op": "profile", "label": "audience A", "distinctive": 76,
                   "audience": {"place": "Berlin", "age": "30_to_34", "gender": None},
                   "pace": {"fast": 15, "slow": 6, "n": 46, "fastShare": 0.714}}
        [a] = direction.learned("", [profile])["audiences"]
        self.assertEqual((a["who"], a["pace"], a["fast"]), ("Berlin, 30 to 34", {"fast": 15, "slow": 6, "n": 46, "share": 71}, []))

    def test_a_profile_fetched_again_is_shown_once(self):
        from studio import direction
        again = [dict(self.EVENTS[0]), dict(self.EVENTS[0], distinctive=90)]
        self.assertEqual([a["distinctive"] for a in direction.learned("", again)["audiences"]], [90])

    def test_titles_written_as_prose_under_a_pace_heading_are_read(self):
        from studio import direction
        text = ("## Audience A — Berlin, 30–34\n\n### Space\n\nThe fast list is *Wrong*.\n\n### Pace\n\n"
                "`pace()`: **fast 15, slow 6, n 46 — a 71% fast share.** The fast list is led by *Uncut Gems*, "
                "*The Marvelous Mrs. Maisel*, *Vikings*; the slow list is *Suspiria*, *Victoria*.\n\n"
                "### Grade\n\n*Midsommar* is tagged slow.\n")
        [a] = direction.learned(text, self.EVENTS[:1])["audiences"]
        self.assertEqual(a["fast"], ["Uncut Gems", "The Marvelous Mrs. Maisel", "Vikings"])
        self.assertEqual(a["slow"], ["Suspiria", "Victoria"])

    def test_titles_no_profile_matches_are_kept_under_their_heading(self):
        from studio import direction
        found = direction.learned(DIRECTION, [])
        self.assertEqual([(a["name"], a["pace"]) for a in found["audiences"]], [("Film A: Tokyo, 45–54", None)])

    def test_no_request_line_ever_reaches_it(self):
        from studio import direction
        self.assertNotIn("/v2/", json.dumps(direction.learned(DIRECTION, self.EVENTS)))

    def test_a_run_without_a_direction_has_nothing_to_say(self):
        from studio import direction
        self.assertEqual(direction.learned("", []), {"compare": "", "audiences": []})


class FrontRoutingTests(unittest.TestCase):

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-front-"))
        self.registry = Registry()
        pin_techniques(self)
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

    def test_a_headless_run_is_listed_by_its_type(self):
        """A product ad made by a headless run, with no request record, is listed as a product ad, not a film trailer."""
        p = project_dir(self.root, "nightshift9", workflow="trailer")
        manifest = json.loads((p / "project.json").read_text(encoding="utf-8"))
        (p / "project.json").write_text(json.dumps({**manifest, "type": "product"}), encoding="utf-8")
        row = front.describe(p, self.registry)
        self.assertEqual(row["workflow"], "Product ad")
        self.assertIs(row["card"], catalogue.BY_SLUG["product-ad"])

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

        async def create(root, project_id, workflow, kind, brief, auto=False, request=(), deny=()):
            handed.update(project_id=project_id, workflow=workflow, kind=kind, brief=brief, auto=auto,
                          request=list(request), deny=list(deny))
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

    def test_a_reference_video_is_staged_and_named_apart_in_the_brief(self):
        handed, patch = self.fake_create()
        with patch:
            reply = self.client.post("/requests", data=self.compose(),
                                     files=[("files", ("menu notes.txt", b"pumpkin soup", "text/plain")),
                                            ("files", ("rival spot.mp4", b"\x00\x00\x00\x18ftyp", "video/mp4"))],
                                     follow_redirects=False)

        self.assertEqual(reply.status_code, 303)
        self.assertIn("- Files supplied, in documents/: menu notes.txt\n", handed["brief"])
        self.assertIn("- Reference video, in documents/ (watch with Video.ask): rival spot.mp4", handed["brief"])
        made = self.root / "autumn-menu-poster"
        self.assertTrue((made / "documents" / "rival spot.mp4").is_file())

    def test_the_report_explainers_long_fields_are_text_boxes_after_the_brief(self):
        page = self.client.get("/compose/report-explainer").text
        brief = page.index('id="brief"')
        for key in ("viewer", "questions"):
            box = page.index(f'<textarea id="x_{key}" name="x_{key}"')
            self.assertGreater(box, brief, f"{key} should come after the brief")
            self.assertNotIn(f'<input class="fr-inline" name="x_{key}"', page)
        self.assertIn('name="x_duration"', page[:brief])      # a choice stays in the header

    def test_the_product_ads_typed_fields_are_text_boxes_after_the_brief(self):
        """Product, comparables, claim and call to action are written, not chosen: room under the brief, not a bar field."""
        page = self.client.get("/compose/product-ad").text
        brief = page.index('id="brief"')
        for key in ("product", "comparables", "claim", "cta"):
            box = page.index(f'<textarea id="x_{key}" name="x_{key}" rows="2"')
            self.assertGreater(box, brief, f"{key} should come after the brief")
            self.assertNotIn(f'<input class="fr-inline" name="x_{key}"', page)
        self.assertIn('name="x_duration"', page[:brief])      # a choice stays in the bar

    def test_the_startup_pitch_composer_asks_for_its_terms_and_makes_audiences_optional(self):
        page = self.client.get("/compose/startup-pitch").text
        brief = page.index('id="brief"')
        for key in ("company", "told_as", "stage", "duration", "narration"):
            self.assertIn(f'name="x_{key}"', page[:brief], f"{key} should be in the header")
        self.assertGreater(page.index('<textarea id="x_raise" name="x_raise" rows="2"'), brief)
        self.assertGreater(page.index('<textarea id="x_use_of_funds" name="x_use_of_funds"'), brief)
        self.assertIn("data-audiences data-optional", page)

    def test_the_posters_headline_is_a_text_box_after_the_brief(self):
        """A headline is words to be set, often two lines: written under the brief, not squeezed into the bar."""
        page = self.client.get("/compose/poster").text
        self.assertGreater(page.index('<textarea id="x_headline" name="x_headline" rows="2"'), page.index('id="brief"'))
        self.assertNotIn('<input class="fr-inline" name="x_headline"', page)

    def test_the_composer_offers_mp4_among_the_files_it_takes(self):
        page = self.client.get("/compose/poster")
        self.assertIn(".mp4", page.text)
        self.assertIn("reference video (MP4)", page.text)

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
        p = project_dir(self.root, "pair", workflow="motion")
        (p / "direction.md").write_text(DIRECTION, encoding="utf-8")
        events(p, {"type": "taste.query", "id": "t1", "op": "profile", "label": "audience A", "distinctive": 82,
                   "request": "/v2/insights?x=1"})
        for path in ("/requests/pair", "/requests/pair/learned"):
            page = self.client.get(path).text
            self.assertIn("What the studio learned", page, path)
            self.assertIn("The Mandalorian", page, path)
            self.assertNotIn("/v2/insights", page, path)
        # Something learned is enough to show the Deliverables tab before anything is delivered.
        tab = re.search(r'<button[^>]*data-view="deliverables"[^>]*>', self.client.get("/requests/pair").text).group(0)
        self.assertNotIn("hidden", tab)

    def test_a_project_from_another_host_is_shown_but_not_offered_a_run(self):
        p = project_dir(self.root, "elsewhere", workflow="painting", policy=False)
        page = self.client.get("/requests/elsewhere").text
        self.assertIn("cannot continue it", page)
        self.assertNotIn("Start in the studio</button>", page)

    def test_a_request_with_a_record_has_a_progress_and_an_agent_view(self):
        p = project_dir(self.root, "harbour", workflow="drawing")
        events(p, {"type": "run.start"}, {"type": "stage.begin", "stage": "Ground"})
        page = self.client.get("/requests/harbour").text
        self.assertIn('id="tab-progress"', page)
        self.assertIn('id="tab-agent"', page)
        self.assertIn('id="panel-agent"', page)
        self.assertIn("data-agent-log", page)
        self.assertRegex(page, r'id="panel-progress" role="tabpanel"')

    def test_a_request_with_a_record_has_the_qloo_views_hidden_until_there_is_something_to_show(self):
        p = project_dir(self.root, "harbour", workflow="poster")
        events(p, {"type": "run.start"})
        page = self.client.get("/requests/harbour").text
        for view in ("audiences", "contrast", "choices"):
            self.assertRegex(page, rf'id="tab-{view}"[^>]*\bhidden\b', view)
            self.assertIn(f'id="panel-{view}"', page)
        self.assertIn("data-evmap", page)

    def test_the_taste_route_serves_profiles_contrasts_and_the_map_without_a_request_line(self):
        p = project_dir(self.root, "pair", workflow="poster")
        (p / "direction.md").write_text(
            "## Between the audiences\n\n| Choice | Evidence | Kind |\n| :-- | :-- | :-- |\n"
            "| What only A draws on | only A: *The Long Quiet* | Qloo (t1) |\n", encoding="utf-8")
        profile = {"type": "taste.query", "op": "profile", "label": "audience A", "request": "/v2/insights?x=1",
                   "audience": {"place": "Tokyo", "age": "45_to_54"}, "pace": {"fast": 1, "slow": 2, "n": 5},
                   "domains": [{"type": "urn:entity:movie", "n": 3, "top": [{"name": "The Long Quiet", "rank": 1}]}]}
        events(p, dict(profile, id="t1"), dict(profile, id="t1"),       # fetched twice: one audience, the latest
               {"type": "taste.contrast", "a": {"id": "t1", "label": "audience A"}, "b": {"id": "t2", "label": "audience B"},
                "domains": [], "summary": "movie: 0 shared"})
        data = self.client.get("/requests/pair/taste").json()
        self.assertEqual([a["label"] for a in data["audiences"]], ["audience A"])
        self.assertEqual(data["audiences"][0]["domains"][0]["top"][0]["name"], "The Long Quiet")
        self.assertEqual(len(data["contrasts"]), 1)
        self.assertEqual([c["text"] for c in data["map"]["choices"]], ["What only A draws on"])
        self.assertNotIn("/v2/insights", json.dumps(data))

    def test_a_draft_has_no_agent_view_to_show(self):
        project_dir(self.root, "fresh", workflow="drawing")
        page = self.client.get("/requests/fresh").text
        self.assertNotIn('id="tab-agent"', page)
        self.assertNotIn("data-agent-log", page)

    def test_the_poster_composer_offers_its_graphic_design_techniques(self):
        page = self.client.get("/compose/poster").text
        self.assertIn("Graphic design", page)
        self.assertIn('name="tech_alternative-grid" value="" checked', page, "the studio decides unless asked")
        self.assertIn('name="tech_mockups" value="request"', page)
        self.assertNotIn("tech_mystery-reveal", page, "a motion technique is not offered for a poster")
        self.assertNotIn("Graphic design", self.client.get("/compose/painting").text, "none apply to painting")

    def test_design_choices_reach_the_cli_and_the_record(self):
        handed, patch = self.fake_create()
        with patch:
            reply = self.client.post("/requests", data=self.compose(**{"tech_mockups": "deny", "tech_alternative-grid": "request"}),
                                     follow_redirects=False)
        self.assertEqual(reply.status_code, 303, reply.text[:300])
        self.assertEqual((handed["request"], handed["deny"]), (["alternative-grid"], ["mockups"]))
        self.assertNotIn("alternative-grid", handed["brief"], "the director's settings are not the client's words")
        record = json.loads((self.root / "autumn-menu-poster" / front.REQUEST_FILE).read_text())
        self.assertEqual(record["techniques"], {"request": ["alternative-grid"], "deny": ["mockups"]})
        page = self.client.get("/requests/autumn-menu-poster").text
        self.assertIn("Alternative grid", page)
        self.assertIn("Don't use", page)

    def test_a_technique_the_workflow_does_not_offer_is_refused_before_anything_is_made(self):
        handed, patch = self.fake_create()
        for field, value, words in (("tech_mystery-reveal", "request", "not one this workflow offers"),
                                    ("tech_mockups", "maybe", "left to the studio, used, or not used")):
            with self.subTest(field=field), patch:
                reply = self.client.post("/requests", data=self.compose(**{field: value}), follow_redirects=False)
                self.assertEqual(reply.status_code, 409)
                self.assertIn(words, reply.text)
        self.assertEqual(handed, {})
        self.assertEqual(list(self.root.iterdir()), [])

    def test_a_refused_request_keeps_its_design_choices(self):
        handed, patch = self.fake_create()
        with patch:
            reply = self.client.post("/requests", data=self.compose(formats=[".mp4"], **{"tech_mockups": "deny"}),
                                     follow_redirects=False)
        self.assertEqual(reply.status_code, 409)
        self.assertIn('name="tech_mockups" value="deny" checked', reply.text)

    def test_an_observing_studio_offers_the_form_and_refuses_to_make(self):
        client = TestClient(app_mod.create_app(self.root, Registry(), observe_only=True, create_at="/new"))
        page = client.get("/compose/poster")
        self.assertIn("can watch requests but not make them", page.text)
        reply = client.post("/requests", data=self.compose(), follow_redirects=False)
        self.assertEqual(reply.status_code, 409)
        self.assertEqual(list(self.root.iterdir()), [])


class FakeCommissioner:
    """Stands in for `adk_agent.intake.Commissioner`: offers drawing (review only) and the infographics."""

    TYPES = {"drawing": ("review",), "vector_infographic": ("editorial", "swiss"), "infographic": ("editorial",),
             "motion": ("pitch", "product", "report")}

    def __init__(self, root: Path, refuse: Exception | None = None) -> None:
        self.root, self.refuse, self.made = root, refuse, []

    def types(self, workflow: str):
        return self.TYPES.get(workflow)

    def free_name(self, title: str) -> str:
        return catalogue.slugify(title).replace("-", "_")

    async def commission(self, name, workflow, kind, brief, documents, auto=False, request=(), deny=()):
        if self.refuse is not None:
            raise self.refuse
        self.made.append({"name": name, "workflow": workflow, "kind": kind, "brief": brief, "auto": auto,
                          "request": list(request), "deny": list(deny),
                          "documents": [Path(d).name for d in documents],
                          "contents": [Path(d).read_bytes() for d in documents]})
        project_dir(self.root, name, workflow=workflow, policy=False)
        return f"/studio/runs/{name}-watch-1"


class Refusal(Exception):
    def __init__(self, status: int, message: str) -> None:
        super().__init__(message)
        self.status = status


class CommissionedComposerTests(unittest.TestCase):
    """The composer on the ADK runtime: an observing studio that was handed a commissioner.

    Point 5 of the 2026-10-18 UI review: the deployed studio is mounted read-only, so before this the new
    composer answered 409 and sent a visitor to the old form at /new.
    """

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-front-adk-"))
        pin_techniques(self)
        self.commissioner = FakeCommissioner(self.root)
        self.client = TestClient(app_mod.create_app(self.root, Registry(), observe_only=True, create_at="/new",
                                                    commissioner=self.commissioner))

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def drawing(self, **overrides):
        data = {"card": "drawing", "title": "Lighthouse sketch", "route": "0", "kind": "",
                "sizes": ["1080x1080"], "formats": [".png"], "brief": "A lighthouse on a rocky point.",
                "action": "start"}
        data.update(overrides)
        return data

    def test_the_composer_offers_start_and_no_draft(self):
        page = self.client.get("/compose/drawing").text
        self.assertNotIn("can watch requests but not make them", page)
        self.assertNotIn("Save as draft", page, "this host cannot start a draft later")
        self.assertRegex(page, r'value="start"\s*\n?\s*>Start in the studio', "Start must not be disabled")

    def test_a_workflow_the_host_does_not_run_says_so_before_the_form_is_filled(self):
        page = self.client.get("/compose/poster").text
        self.assertIn("does not make poster requests yet", page)
        self.assertIn('value="start" disabled', page)

    def test_a_request_is_commissioned_and_opens_its_own_page(self):
        reply = self.client.post("/requests", data=self.drawing(),
                                 files=[("files", ("notes.txt", b"lantern room first", "text/plain"))],
                                 follow_redirects=False)

        self.assertEqual(reply.status_code, 303, reply.text[:300])
        self.assertTrue(reply.headers["location"].endswith("/requests/lighthouse_sketch"))
        made = self.commissioner.made[0]
        self.assertEqual((made["name"], made["workflow"]), ("lighthouse_sketch", "drawing"))
        self.assertEqual(made["kind"], "review", "one offered type is chosen, as the intake form does")
        self.assertIn("A lighthouse on a rocky point.", made["brief"])
        self.assertIn("## What to deliver", made["brief"])
        self.assertEqual((made["documents"], made["contents"]), (["notes.txt"], [b"lantern room first"]))
        record = json.loads((self.root / "lighthouse_sketch" / front.REQUEST_FILE).read_text(encoding="utf-8"))
        self.assertEqual((record["title"], record["type"], record["files"]), ("Lighthouse sketch", "review", ["notes.txt"]))

    def trailer(self, card: str, **overrides):
        data = {"card": card, "title": "Nightshift", "route": "0", "kind": "", "sizes": ["1920x1080"],
                "formats": [".mp4"], "brief": "Fifteen seconds.", "aud_place": ["Berlin", "Lagos"],
                "aud_age": ["", ""], "action": "start"}
        data.update(overrides)
        return data

    def test_a_product_ad_is_commissioned_as_a_product_trailer(self):
        reply = self.client.post("/requests", data=self.trailer("product-ad", x_claim="Steeped for twenty hours."),
                                 follow_redirects=False)
        self.assertEqual(reply.status_code, 303, reply.text[:300])
        made = self.commissioner.made[0]
        self.assertEqual((made["workflow"], made["kind"]), ("motion", "product"))
        self.assertIn("- Claim: Steeped for twenty hours.", made["brief"])

    def test_a_report_explainer_is_refused_without_its_pdf_and_commissioned_with_it(self):
        data = self.trailer("report-explainer", x_questions="How big was revenue?", aud_place=[], aud_age=[])
        refused = self.client.post("/requests", data=data, follow_redirects=False)
        self.assertEqual(refused.status_code, 409)
        self.assertIn("attach the PDF", refused.text)
        self.assertEqual(self.commissioner.made, [])

        reply = self.client.post("/requests", data=data, files=[("files", ("nvda-10k.pdf", b"%PDF-1.4", "application/pdf"))],
                                 follow_redirects=False)
        self.assertEqual(reply.status_code, 303, reply.text[:300])
        made = self.commissioner.made[0]
        self.assertEqual((made["workflow"], made["kind"], made["documents"]), ("motion", "report", ["nvda-10k.pdf"]))
        self.assertIn("  1. How big was revenue?", made["brief"])

    def test_a_startup_pitch_is_commissioned_as_a_pitch_trailer_with_its_deck(self):
        data = self.trailer("startup-pitch", x_company="UberCab", aud_place=["Singapore", "London"])
        reply = self.client.post("/requests", data=data, files=[("files", ("deck.pdf", b"%PDF-1.4", "application/pdf"))],
                                 follow_redirects=False)
        self.assertEqual(reply.status_code, 303, reply.text[:300])
        made = self.commissioner.made[0]
        self.assertEqual((made["workflow"], made["kind"], made["documents"]), ("motion", "pitch", ["deck.pdf"]))
        self.assertIn("- Audience A: Singapore", made["brief"])
        self.assertIn("- One film per audience", made["brief"])
        self.assertFalse(made["auto"], "asking is the default")

    def test_decide_everything_commissions_an_autonomous_run(self):
        reply = self.client.post("/requests", data=self.trailer("film-trailer", auto="1"), follow_redirects=False)
        self.assertEqual(reply.status_code, 303, reply.text[:300])
        self.assertTrue(self.commissioner.made[0]["auto"])
        page = self.client.get("/compose/film-trailer").text
        self.assertIn('name="auto" value="1"', page)
        self.assertIn('name="auto" value="" checked', page)

    def test_design_choices_are_commissioned_on_this_host_too(self):
        data = self.trailer("product-ad", **{"tech_alternative-grid": "request", "tech_mystery-reveal": "deny"})
        reply = self.client.post("/requests", data=data, follow_redirects=False)
        self.assertEqual(reply.status_code, 303, reply.text[:300])
        made = self.commissioner.made[0]
        self.assertEqual((made["request"], made["deny"]), (["alternative-grid"], ["mystery-reveal"]))

    def test_a_film_trailer_is_not_given_the_workflows_lone_type(self):
        """The host offers `trailer` one type; the film trailer's card offers none, and the card decides."""
        reply = self.client.post("/requests", data=self.trailer("film-trailer"), follow_redirects=False)
        self.assertEqual(reply.status_code, 303, reply.text[:300])
        self.assertEqual(self.commissioner.made[0]["kind"], "")

    def test_neither_trailer_card_shows_a_kind_picker(self):
        for card in ("product-ad", "film-trailer"):
            with self.subTest(card=card):
                self.assertNotIn('name="kind"', self.client.get(f"/compose/{card}").text)

    def test_a_workflow_the_host_does_not_run_is_refused_without_commissioning(self):
        reply = self.client.post("/requests", data={**self.drawing(), "card": "poster", "kind": "film",
                                                    "sizes": ["1200x1800"], "aud_place": ["Lisbon"],
                                                    "aud_age": [""]})
        self.assertEqual(reply.status_code, 409)
        self.assertIn("does not make poster requests yet", reply.text)
        self.assertEqual(self.commissioner.made, [])

    def test_a_type_the_host_does_not_offer_is_refused(self):
        reply = self.client.post("/requests", data=self.drawing(kind="seed"))
        self.assertEqual(reply.status_code, 409)
        self.assertIn("review", reply.text)
        self.assertEqual(self.commissioner.made, [])

    def test_the_hosts_own_refusal_is_shown_with_its_status(self):
        self.commissioner.refuse = Refusal(429, "This host has started its 25 runs for today.")
        reply = self.client.post("/requests", data=self.drawing())
        self.assertEqual(reply.status_code, 429)
        self.assertIn("started its 25 runs for today", reply.text)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_an_unexpected_error_is_not_swallowed_as_a_refusal(self):
        self.commissioner.refuse = RuntimeError("the CLI is missing")
        client = TestClient(self.client.app, raise_server_exceptions=False)
        self.assertEqual(client.post("/requests", data=self.drawing()).status_code, 500)


class AdkStatusTests(unittest.TestCase):
    """A watched ADK run that is waiting on an answer reads as Needs you, like a driven one."""

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-front-ask-"))

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def test_an_open_question_on_a_watched_run_needs_you(self):
        p = project_dir(self.root, "lighthouse")
        run = mock.Mock(observed=True, live=True, status="running")
        channel = mock.Mock()
        with mock.patch.object(front.observe_mod, "_ask_channel", return_value=channel):
            channel.pending.return_value = ["q1"]
            self.assertEqual(front.status_of(p, run, []), "needs-you")
            channel.pending.assert_called_with("lighthouse")
            channel.pending.return_value = []
            self.assertEqual(front.status_of(p, run, []), "running")

    def test_a_run_the_host_is_working_is_running_however_still_its_record(self):
        """ftcexplainer1 read as Stopped during a 154 s model turn: a still record is not an ended run."""
        p = project_dir(self.root, "ftc")
        quiet = mock.Mock(observed=True, live=False, status="watching")
        events = [{"type": "script.ok"}]
        self.assertEqual(front.status_of(p, quiet, events), "stopped")
        self.assertEqual(front.status_of(p, quiet, events, lambda name: name == "ftc"), "running")
        self.assertEqual(front.status_of(p, quiet, events, lambda name: False), "stopped")

    def test_a_commissioned_run_before_its_first_event_is_queued_not_a_draft(self):
        p = project_dir(self.root, "fresh")
        watching = mock.Mock(observed=True, live=False, status="watching")
        self.assertEqual(front.status_of(p, watching, []), "queued")
        self.assertEqual(front.status_of(p, None, []), "draft")

    def test_a_watched_run_that_has_ended_is_delivered_while_still_live(self):
        """Measured on harbour_at_dawn: Delivered on its page, Active on the list for three minutes after."""
        p = project_dir(self.root, "harbour")
        events(p, {"type": "run.start"}, {"type": "render", "artifact": "output.webp", "format": "webp"})
        with (p / "events" / "agent.jsonl").open("w", encoding="utf-8") as out:
            out.write(json.dumps({"ts": "2026-10-18T23:00:00Z", "type": "run.begin"}) + "\n")
            out.write(json.dumps({"ts": "2026-10-18T23:16:08Z", "type": "run.end", "reason": "completed"}) + "\n")
        live = mock.Mock(observed=True, live=True, status="watching")
        from orchestrator.events import read_events
        self.assertEqual(front.status_of(p, live, read_events(p / "events" / "server.jsonl")), "delivered")

    def test_a_new_run_after_an_earlier_end_is_running_again(self):
        """One MCP server per app on ADK: only the agent's own last start counts."""
        p = project_dir(self.root, "again")
        with (p / "events" / "agent.jsonl").open("w", encoding="utf-8") as out:
            for kind in ("run.begin", "run.end", "run.begin"):
                out.write(json.dumps({"ts": "2026-10-18T23:00:00Z", "type": kind}) + "\n")
        live = mock.Mock(observed=True, live=True, status="watching")
        with mock.patch.object(front.observe_mod, "_ask_channel", return_value=None):
            self.assertEqual(front.status_of(p, live, [{"type": "run.start"}]), "running")

    def test_without_a_channel_it_is_simply_running(self):
        p = project_dir(self.root, "elsewhere")
        with mock.patch.object(front.observe_mod, "_ask_channel", return_value=None):
            self.assertEqual(front.status_of(p, mock.Mock(observed=True, live=True, status="running"), []), "running")


class GalleryFallbackTests(unittest.TestCase):
    """The card pictures are not in git, so a clone has none: a card shows its kind icon, not a broken image."""

    # Rendered with context rather than through a page: Jinja caches an imported macro module with the
    # globals it first saw, so patching the global after a page has rendered would test nothing.
    CARD = '{% import "front/macros.html" as m with context %}{{ m.workflow_card(card, "") }}'

    def card(self, gallery: frozenset) -> str:
        TestClient(app_mod.create_app(Path(tempfile.gettempdir()), Registry()))   # installs the front's globals
        template = app_mod.TEMPLATES.env.from_string(self.CARD)
        return template.render(card=catalogue.BY_SLUG["poster"], gallery=gallery)

    def test_a_missing_picture_falls_back_to_the_kind_icon(self):
        html = self.card(frozenset())
        self.assertNotIn("/static/gallery/", html)
        self.assertIn("ps-wcard-empty", html)

    def test_a_picture_that_is_here_is_shown(self):
        self.assertIn("/static/gallery/poster.webp", self.card(frozenset({"poster.webp"})))

    def test_the_gallery_is_what_is_on_disk(self):
        TestClient(app_mod.create_app(Path(tempfile.gettempdir()), Registry()))
        on_disk = {p.name for p in (Path(front.__file__).parent / "static" / "gallery").glob("*") if p.is_file()}
        self.assertEqual(app_mod.TEMPLATES.env.globals["gallery"], on_disk)


class TitleListTests(unittest.TestCase):
    """A full stop inside an italic title does not end the list of titles."""

    def test_a_title_with_a_full_stop_is_kept_and_so_are_those_after_it(self):
        from studio import direction
        cell = "Fast titles: *Dr. Strangelove*, *Mr. Nobody*, *Heat*. Slow titles: *Minari*."
        self.assertEqual(direction._titles_after(cell, "fast"), ["Dr. Strangelove", "Mr. Nobody", "Heat"])
        self.assertEqual(direction._titles_after(cell, "slow"), ["Minari"])

    def test_a_sentence_before_any_title_ends_the_list(self):
        from studio import direction
        self.assertEqual(direction._titles_after("Fast titles: none. Slow titles: *Minari*.", "fast"), [])


if __name__ == "__main__":
    unittest.main()
