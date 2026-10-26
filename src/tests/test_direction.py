"""*Why it looks like this*: `direction.md` read into choices and evidence, served, and rendered safely.

    python -m unittest discover -s src/tests -t .

Three layers, each tested where it lives: the parser (`studio/direction.py`), the route (`/runs/{id}/why`), and the
page's inline formatter, pulled out of `run.html` and run under node as `test_run_page.py` does, because
`direction.md` is agent-written and the formatter is what stands between it and the page's markup.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from fastapi.testclient import TestClient

from studio import app as app_mod
from studio import direction
from studio.runs import Registry, Run

from test_run_page import EXTRACT, RECORD_JS, RUN_HTML
from test_studio import _loaded, project_dir

DIRECTION = """# Direction — THE LONG LISTEN

Two posters, one film.

## Shared — the film and the system

| Choice | Evidence | Kind |
| :-- | :-- | :-- |
| Film: quiet science fiction | logline and genre line | Brief |
| Comparables as signals only | resolve `t1`–`t3`; "Ruled out" line | Qloo / Brief |
| Register (Q5): uneasy and quiet — *assumed, not confirmed* | the genre line | Assumed |

## Audience A — Lagos, 24 and younger

Profile `t5` (locality passed by id from `t4`). 12 distinctive films.

| Choice | Evidence | Kind |
| :-- | :-- | :-- |
| Halftone head | the Spider-Verse films (t5 movie items 6, 9); a pipe in code `a|b` stays | Qloo |
| Headline flush left | all capitals slow reading (Carter et al., p. 79) | Research |

## Measured sizes

| Element | Size |
| :-- | :-- |
| Title | 74 mm |

## Amendments

| Change | Why | Kind |
| :-- | :-- | :-- |
| Cyan offset 0.9 → 0.75 | the squint | Check (squint) |
| Figure enlarged | read as a keyhole | Judgment on the render |
"""

EVENTS = [
    {"type": "taste.query", "ts": "2026-10-16T10:00:00Z", "id": "t1", "op": "resolve", "label": "Arrival",
     "request": "/search?query=Arrival", "fromCache": False},
    {"type": "taste.query", "ts": "2026-10-16T10:00:05Z", "id": "t5", "op": "profile", "label": "audience A",
     "requests": 7, "fromCache": False},
    {"type": "taste.query", "ts": "2026-10-16T10:00:06Z", "id": "t9", "op": "demographics", "label": "uncited",
     "request": "/v2/insights?x"},
    {"type": "render", "ts": "2026-10-16T10:00:07Z", "artifact": "final_a.png", "format": "png"},
]


class ParseTests(unittest.TestCase):
    """The tables a direction is made of, and nothing else."""

    def setUp(self) -> None:
        self.sections = direction.parse(DIRECTION)

    def test_only_tables_with_a_kind_column_become_sections(self):
        self.assertEqual([s["title"] for s in self.sections],
                         ["Shared — the film and the system", "Audience A — Lagos, 24 and younger", "Amendments"])

    def test_a_row_is_its_choice_evidence_and_kinds(self):
        row = self.sections[0]["rows"][1]
        self.assertEqual(row["choice"], "Comparables as signals only")
        self.assertEqual([k["key"] for k in row["kinds"]], ["qloo", "brief"])
        self.assertEqual(row["refs"], ["t1", "t3"])

    def test_kinds_are_read_by_stem_and_unknown_ones_kept_as_written(self):
        keys = [r["kinds"][0]["key"] for r in self.sections[2]["rows"]]
        self.assertEqual(keys, ["check", "judgement"])     # "Judgment", the American spelling, too
        self.assertEqual(self.sections[0]["rows"][2]["kinds"][0]["key"], "assumed")
        self.assertEqual(direction.kinds("Intuition")[0], {"key": "other", "label": "Intuition"})

    def test_the_lead_paragraph_and_its_references_come_with_the_section(self):
        audience = self.sections[1]
        self.assertTrue(audience["lead"].startswith("Profile `t5`"))
        self.assertEqual(audience["leadRefs"], ["t5", "t4"])

    def test_a_pipe_inside_code_does_not_split_a_cell(self):
        self.assertIn("`a|b` stays", self.sections[1]["rows"][0]["evidence"])

    def test_queries_are_scoped_to_the_run_and_limited_to_what_is_cited(self):
        payload = direction.explain(DIRECTION, EVENTS, since="2026-10-16T10:00:01Z")
        self.assertEqual(sorted(payload["queries"]), ["t5"])          # t1 predates the run; t9 is not cited
        self.assertEqual(payload["queries"]["t5"]["requests"], 7)
        self.assertFalse(payload["truncated"])

    def test_a_concepts_table_is_a_section_with_its_middle_columns_as_evidence(self):
        """The template's concepts table has five columns; the three between choice and kind are the evidence."""
        concepts = direction.parse(
            "## Audience B\n\n| Concept | Operation | Dominant | Evidence | Kind |\n| :-- | :-- | :-- | :-- | :-- |\n"
            "| **Chosen** — the dish | scale as narrative | dark ground | `t5` item 4 | Qloo |\n"
            "| Rejected — … | | | | |\n")
        rows = concepts[0]["rows"]
        self.assertEqual(rows[0]["evidence"], "scale as narrative — dark ground — `t5` item 4")
        self.assertEqual(rows[0]["refs"], ["t5"])
        self.assertEqual(rows[1]["kinds"], [])

    def test_a_handle_in_prose_needs_its_boundaries(self):
        self.assertEqual(direction.refs("t2026 is a year, at5 is not a handle, (t7) is"), ["t7"])


class AudienceListTests(unittest.TestCase):
    """An `insights` query asked about an audience, shown with the tags the direction cites from it on its entities."""

    TEXT = """# Direction

## Choices

| Choice | Evidence | Kind |
| :-- | :-- | :-- |
| Matte can | `"Raw Textures"`, `"Matte"` (t6, item 2) | Qloo (t6) |
"""

    def lists(self, *events):
        return direction.taste_views(self.TEXT, list(events))["lists"]

    def test_entities_carry_only_the_tags_the_direction_cites_from_that_query(self):
        ent = [{"rank": 1, "name": "Kiosk", "tags": ["Matte", "Loud"]},
               {"rank": 2, "name": "Cafe Onion", "tags": ["Raw Textures", "Matte", "Cosy", "Raw textures"]}]
        [l] = self.lists({"type": "taste.query", "id": "t6", "op": "insights", "label": "audience A buyers: brand",
                          "results": 2, "entities": ent,
                          "request": "/v2/insights?filter.type=urn%3Aentity%3Abrand&signal.demographics.age=30_to_34"
                                     "&signal.location.query=Berlin&take=50"})
        self.assertEqual(l["audience"], {"place": "Berlin", "age": "30_to_34", "gender": None})
        self.assertEqual(l["type"], "urn:entity:brand")
        self.assertEqual([(x["name"], x["cited"]) for x in l["entities"]],
                         [("Kiosk", ["Matte"]), ("Cafe Onion", ["Raw Textures", "Matte"])])

    def test_a_query_with_no_audience_is_not_a_list_and_an_old_run_has_no_entities(self):
        lists = self.lists(
            {"type": "taste.query", "id": "t7", "op": "insights", "label": "anywhere", "request": "/v2/insights?filter.type=urn%3Aentity%3Abrand"},
            {"type": "taste.query", "id": "t8", "op": "insights", "label": "places",
             "request": "/v2/insights?filter.location.query=Berlin&filter.type=urn%3Aentity%3Aplace", "results": 20})
        self.assertEqual([(l["id"], l["entities"]) for l in lists], [("t8", None)])


class EvidenceMapTests(unittest.TestCase):
    """Query → reference → choice → what it is for, each edge in the state the reference check left it."""

    TEXT = """# Direction

## Film A: Tokyo

### Choices

| Choice | Evidence | Kind |
| :-- | :-- | :-- |
| **Pace:** long holds | `pace()` on t1: slow titles *The Long Quiet*, *Amélie's Garden (2019)* | Qloo (t1) |
| Grade | t5 over *Long Quiet*: **"Dreamlike Visuals"** (item 1), "Neon Noir" (item 4) | Qloo (t5) |
| Headline | all capitals slow reading (*Typographic Design*, p. 79) | Research |

## Film B: Mumbai

| Choice | Evidence | Kind |
| :-- | :-- | :-- |
| Arrivals land harder | the fast lean | Qloo (t2) |
"""

    EVENTS = [
        {"type": "taste.query", "id": "t1", "op": "profile", "label": "audience A"},
        {"type": "taste.query", "id": "t2", "op": "profile", "label": "audience B"},
        {"type": "taste.query", "id": "t5", "op": "describe", "label": "grade A"},
        {"type": "taste.check", "path": "direction.md", "passed": False, "checked": 5, "unverified": ['"Neon Noir"'],
         "rows": [
             {"section": "Choices", "choice": "Pace: long holds", "handles": ["t1"], "problems": [], "tags": [],
              "titles": [{"title": "The Long Quiet", "found": True, "in": "t1"},
                         {"title": "Amélie's Garden (2019)", "found": True, "in": "t1"}]},
             {"section": "Choices", "choice": "Grade", "handles": ["t5"], "problems": [],
              "titles": [{"title": "Long Quiet", "found": True, "in": "t5"}],
              "tags": [{"tag": "Dreamlike Visuals", "found": True, "in": "t5"},
                       {"tag": "Neon Noir", "found": False, "in": None}]},
         ]},
    ]

    def setUp(self) -> None:
        self.map = direction.explain(self.TEXT, self.EVENTS)["map"]
        self.ids = {n["text"]: n["id"] for n in self.map["refs"]} | {c["text"]: c["id"] for c in self.map["choices"]}
        self.edges = {(e["from"], e["to"]): e["state"] for e in self.map["edges"]}

    def test_a_subsection_carries_its_parent_heading(self):
        self.assertEqual([(p["parent"], p["title"]) for p in self.map["pieces"]],
                         [("Film A: Tokyo", "Choices"), ("", "Film B: Mumbai")])

    def test_only_qloo_rows_are_choices_and_each_reference_appears_once(self):
        self.assertEqual([c["text"] for c in self.map["choices"]],
                         ["**Pace:** long holds", "Grade", "Arrivals land harder"])
        # *The Long Quiet* and *Long Quiet* are one title; a Research row's italics are not Qloo references.
        self.assertEqual([(r["kind"], r["text"]) for r in self.map["refs"]],
                         [("title", "The Long Quiet"), ("title", "Amélie's Garden (2019)"),
                          ("tag", "Dreamlike Visuals"), ("tag", "Neon Noir")])
        self.assertEqual([q["id"] for q in self.map["queries"]], ["t1", "t2", "t5"])

    def test_edges_carry_the_checks_verdicts(self):
        quiet, neon = self.ids["The Long Quiet"], self.ids["Neon Noir"]
        pace, grade = self.ids["**Pace:** long holds"], self.ids["Grade"]
        self.assertEqual(self.edges[("t1", quiet)], "verified")
        self.assertEqual(self.edges[("t5", quiet)], "verified")       # the describe was asked about it
        self.assertEqual(self.edges[(quiet, pace)], "verified")
        self.assertEqual(self.edges[("t5", neon)], "unverified")      # not returned: hangs from the query cited
        self.assertEqual(self.edges[(neon, grade)], "unverified")
        self.assertEqual(self.edges[(grade, "s0")], "unverified")

    def test_a_row_with_no_references_links_its_query_straight_to_the_choice_unchecked(self):
        harder = self.ids["Arrivals land harder"]
        self.assertEqual(self.edges[("t2", harder)], "unchecked")
        self.assertEqual(self.edges[(harder, "s1")], "unchecked")

    def test_a_tag_in_backticks_is_read_as_the_engine_reads_it(self):
        row = {"choice": "Matte can", "evidence": '`"Raw Textures"` (t6, item 32); `"Matte Finishes"` (item 46)'}
        self.assertEqual([(r["kind"], r["text"]) for r in direction.row_references(row)],
                         [("tag", "Raw Textures"), ("tag", "Matte Finishes")])

    def test_every_tag_of_a_list_sharing_one_item_is_read(self):
        row = {"choice": "Matte can", "evidence": '`"Industrial-Chic"`, `"Raw Textures"` and `"Neutral Color Palette"` '
                                                  '(t6, item 32); "Minimalist" on 41 of 50'}
        self.assertEqual([r["text"] for r in direction.row_references(row)],
                         ["Industrial-Chic", "Raw Textures", "Neutral Color Palette"])

    def test_a_tag_at_an_entity_named_before_its_item_is_read(self):
        row = {"choice": "Kept", "evidence": '`"Cold drip espresso"` at *DoubleEye* (t8, item 8): present in this city'}
        self.assertEqual([(r["kind"], r["text"]) for r in direction.row_references(row)],
                         [("title", "DoubleEye"), ("tag", "Cold drip espresso")])

    def test_names_compare_as_the_engine_compares_them(self):
        self.assertEqual(direction.ref_key("Amélie’s Garden (2019)"), "amelie s garden")
        self.assertEqual(direction.ref_key("The Long Quiet"), direction.ref_key("long quiet"))


class VerificationTests(unittest.TestCase):
    """What the Verification view draws: each film's check, its strays as settled, the run's checks and its documents."""

    FILM = {"type": "film.verify", "ts": "2026-10-22T10:00:00Z", "video": "output.mp4", "duration": 9, "model": "nemotron",
            "items": [{"text": "BE COOL", "from": 0, "to": 3, "source": "brief", "status": "seen"}],
            "strays": [{"n": 1, "read": "$744M", "at": 4.2, "frame": "artifacts/verify/stray-1.png", "kind": "different figure"},
                       {"n": 2, "read": "GHOST", "at": 6.0, "frame": "artifacts/verify/stray-2.png", "kind": "never set"}]}

    def test_a_stray_is_shown_once_settled_and_only_counted_before(self):
        events = [self.FILM, {"type": "film.stray", "video": "output.mp4", "n": 1, "verdict": "misread", "note": "the frame says $974M"}]
        film = direction.verification(events, None)["films"][0]
        self.assertEqual([(s["n"], s["verdict"]) for s in film["strays"]], [(1, "misread")])
        self.assertEqual(film["pending"], 1)                          # GHOST: not looked at, so not shown
        self.assertNotIn("GHOST", json.dumps(film["strays"]))

    def test_a_new_check_of_the_same_film_replaces_the_old(self):
        again = dict(self.FILM, ts="2026-10-22T11:00:00Z", strays=[])
        films = direction.verification([self.FILM, again], None)["films"]
        self.assertEqual((len(films), films[0]["pending"]), (1, 0))

    def test_an_image_check_is_drawn_like_a_films_with_its_size_and_crops(self):
        image = {"type": "image.verify", "image": "artifacts/verify/final.png", "source": "canvas", "width": 1200,
                 "height": 1600, "model": "nemotron",
                 "items": [{"text": "2,205.8%", "box": [40, 80, 120, 30], "source": "document", "status": "seen"}],
                 "strays": [{"n": 1, "read": "2,206.8%", "frame": "artifacts/verify/final-stray-1.png", "kind": "different figure"}]}
        before = direction.verification([image], None)["images"][0]
        self.assertEqual((before["width"], before["height"], before["strays"], before["pending"]), (1200, 1600, [], 1))
        after = direction.verification([image, {"type": "image.stray", "image": "artifacts/verify/final.png", "n": 1,
                                                "verdict": "misread"}], None)
        self.assertEqual([s["frame"] for s in after["images"][0]["strays"]], ["artifacts/verify/final-stray-1.png"])
        self.assertEqual(after["films"], [])

    def test_a_derivation_is_shown_step_by_step_and_a_new_check_replaces_the_old(self):
        first = {"type": "derivation.verify", "name": "area", "steps": ["∫₀² x²/2 dx", "8/6", "3/4"], "passed": False,
                 "counts": {"proved": 1, "differs": 1},
                 "pairs": [{"from": 1, "to": 2, "verdict": "proved", "read": None},
                           {"from": 2, "to": 3, "verdict": "differs", "at": None, "values": [1.333333333333, 0.75], "read": None}]}
        fixed = dict(first, steps=["∫₀² x²/2 dx", "8/6", "4/3"], passed=True,
                     pairs=[dict(first["pairs"][0]), {"from": 2, "to": 3, "verdict": "proved", "read": None}])
        other = dict(first, name="solve", steps=["2x = 4", "x = 2"], passed=True, pairs=[{"from": 1, "to": 2, "verdict": "proved"}])
        only = direction.verification([first], None)["derivations"]
        self.assertEqual([(d["name"], d["passed"]) for d in only], [("area", False)])
        self.assertEqual(only[0]["pairs"][1]["values"], [1.333333333333, 0.75])
        both = direction.verification([first, other, fixed], None)["derivations"]
        self.assertEqual([(d["name"], d["passed"], d["steps"][-1]) for d in both], [("solve", True, "x = 2"), ("area", True, "4/3")])
        self.assertEqual(direction.verification([{"type": "derivation.verify", "name": "x"}], None)["derivations"], [])

    def test_a_claim_is_shown_with_the_code_that_checked_it(self):
        first = {"type": "claim.verify", "name": "growth", "claim": "Revenue grew 46%.", "verdict": "error", "passed": False,
                 "checks": [], "code": "check('x', rounds(", "error": "SyntaxError"}
        again = dict(first, verdict="holds", passed=True, error=None, code="check('up 46%', rounds(45.94, 46), 'pct 45.94')",
                     checks=[{"what": "up 46%", "passed": True, "detail": "pct 45.94"}])
        claims = direction.verification([first, again, {"type": "claim.verify", "name": "x"}], None)["claims"]
        self.assertEqual([(c["name"], c["verdict"]) for c in claims], [("growth", "holds")])
        self.assertIn("rounds(45.94, 46)", claims[0]["code"])
        self.assertEqual(claims[0]["checks"][0]["what"], "up 46%")

    def test_the_latest_verdict_per_claim_and_the_documents_read(self):
        events = [{"type": "check", "claim": "reading time", "passed": False, "detail": "title 1.1 s"},
                  {"type": "check", "claim": "reading time", "passed": True, "detail": "title 2.2 s"},
                  {"type": "check", "claim": "every text has a source", "passed": True, "judged": True},
                  {"type": "document.read", "descriptor": "documents/x.pdf", "model": "nemotron", "success": True},
                  {"type": "document.read", "descriptor": "documents/x.pdf", "model": "nemotron", "success": False}]
        v = direction.verification(events, {"passed": True, "checked": 3, "unverified": 0})
        self.assertEqual([(c["claim"], c["passed"]) for c in v["checks"]],
                         [("reading time", True), ("every text has a source", True)])
        self.assertTrue(v["checks"][1]["judged"])
        self.assertEqual(v["documents"], [{"document": "documents/x.pdf", "model": "nemotron", "reads": 1}])
        self.assertTrue(v["references"]["passed"])


class ReferenceCheckTests(unittest.TestCase):
    """`Taste.checkReferences()` laid onto the rows: verified, unverified with what is missing, or unchecked."""

    #: As the engine records it: the agent's own words and the verdicts, never a raw Qloo payload.
    CHECK = {
        "type": "taste.check", "ts": "2026-10-16T10:05:00Z", "path": "direction.md", "passed": False, "checked": 3,
        "unverified": ["*Remembered Film*"],
        "rows": [
            {"section": "Shared — the film and the system", "choice": "Comparables as signals only",
             "handles": ["t1", "t3"], "titles": [{"title": "Arrival", "found": True, "in": "t1"}], "tags": [],
             "problems": []},
            {"section": "Audience A — Lagos, 24 and younger", "choice": "Halftone head", "handles": ["t5"],
             "titles": [{"title": "Spider-Verse", "found": True, "in": "t5"},
                        {"title": "Remembered Film", "found": False, "in": None, "elsewhere": None}],
             "tags": [], "problems": []},
        ],
    }

    def test_each_qloo_row_gets_its_verdict(self):
        payload = direction.explain(DIRECTION, EVENTS + [self.CHECK])
        shared, audience = payload["sections"][0], payload["sections"][1]
        self.assertEqual(shared["rows"][1]["check"]["state"], "verified")
        bad = audience["rows"][0]["check"]
        self.assertEqual(bad["state"], "unverified")
        self.assertEqual(bad["missing"], ["*Remembered Film*"])
        self.assertNotIn("check", audience["rows"][1])                   # a Research row is not a Qloo row
        self.assertEqual(payload["referenceCheck"], {"passed": False, "checked": 3, "unverified": 1,
                                                     "ts": "2026-10-16T10:05:00Z"})

    def test_a_row_written_after_the_check_is_unchecked_not_inherited(self):
        edited = DIRECTION.replace("| Halftone head |", "| Halftone head, reworded |")
        row = direction.explain(edited, EVENTS + [self.CHECK])["sections"][1]["rows"][0]
        self.assertEqual(row["check"]["state"], "unchecked")

    def test_the_latest_check_of_this_run_wins_and_none_means_none(self):
        later = dict(self.CHECK, ts="2026-10-16T11:00:00Z", passed=True, unverified=[],
                     rows=[dict(r, titles=[dict(t, found=True) for t in r["titles"]]) for r in self.CHECK["rows"]])
        payload = direction.explain(DIRECTION, EVENTS + [self.CHECK, later])
        self.assertTrue(payload["referenceCheck"]["passed"])
        self.assertEqual(payload["sections"][1]["rows"][0]["check"]["state"], "verified")
        self.assertIsNone(direction.explain(DIRECTION, EVENTS)["referenceCheck"])
        self.assertIsNone(direction.explain(DIRECTION, EVENTS + [self.CHECK], since="2026-10-16T10:30:00Z")["referenceCheck"])


class WhyRouteTests(unittest.TestCase):
    """`/runs/{id}/why`: one fixed file, through the same containment as every deliverable."""

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="example-why-"))
        self.registry = Registry()
        self.client = TestClient(app_mod.create_app(self.root, self.registry))
        self.project = project_dir(self.root, "acme")
        self.registry._runs["acme-1"] = Run(
            id="acme-1", project=_loaded(self.project), prompt="", stream=None,
            started="2026-10-16T00:00:00+00:00", status="done")

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def test_no_direction_is_a_404(self):
        self.assertEqual(self.client.get("/runs/acme-1/why").status_code, 404)

    def test_the_direction_comes_back_with_its_cited_queries(self):
        (self.project / "direction.md").write_text(DIRECTION, encoding="utf-8")
        (self.project / "events").mkdir(exist_ok=True)
        (self.project / "events" / "server.jsonl").write_text(
            "\n".join(json.dumps(e) for e in EVENTS) + "\n", encoding="utf-8")

        body = self.client.get("/runs/acme-1/why").json()

        self.assertEqual(len(body["sections"]), 3)
        self.assertEqual(sorted(body["queries"]), ["t1", "t5"])
        self.assertEqual(body["queries"]["t1"]["request"], "/search?query=Arrival")

    def test_the_listing_marks_the_document_that_feeds_the_panel(self):
        """The page asks the listing which document is the direction; it names none in its own code."""
        (self.project / "direction.md").write_text(DIRECTION, encoding="utf-8")
        (self.project / "findings.md").write_text("# Findings", encoding="utf-8")

        listing = self.client.get("/runs/acme-1/deliverables").json()["deliverables"]

        self.assertEqual([d["name"] for d in listing if d.get("panel") == "why"], ["direction.md"])

    def test_an_unknown_run_is_a_404(self):
        self.assertEqual(self.client.get("/runs/nope/why").status_code, 404)


# `esc` and `inline` come from the shared `record.js`, as the page takes them; `tasteRef` is still the page's own.
HARNESS = EXTRACT + """
const vm = require('vm');
const box = { window: {} };
vm.runInNewContext(fs.readFileSync(process.argv[4], 'utf8'), box);
const { esc, inline } = box.window.PolsonRecord;
const tasteRef = new Function('esc', extract('tasteRef') + '; return tasteRef;')(esc);
const cases = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
process.stdout.write(JSON.stringify({
  inline: cases.inline.map(inline),
  ref: tasteRef('t5', cases.queries),
}));
"""


@unittest.skipUnless(shutil.which("node"), "node is not installed")
class InlineFormatterTests(unittest.TestCase):
    """The page's formatter escapes first and formats after, so agent text cannot become markup."""

    def run_harness(self, cases: dict) -> dict:
        with tempfile.TemporaryDirectory() as tmp:
            script, data = Path(tmp) / "h.js", Path(tmp) / "c.json"
            script.write_text(HARNESS, encoding="utf-8")
            data.write_text(json.dumps(cases), encoding="utf-8")
            out = subprocess.run(["node", str(script), str(RUN_HTML), str(data), str(RECORD_JS)],
                                 capture_output=True, text=True, timeout=60, check=True)
            return json.loads(out.stdout)

    def test_markup_in_a_cell_is_escaped_and_the_three_marks_still_format(self):
        result = self.run_harness({
            "inline": ["<img src=x onerror=alert(1)>", "*Isle of Dogs* and `t5` and **bold**",
                       "`<script>alert(1)</script>`"],
            "queries": {"t5": {"op": "profile", "label": "<b>audience</b>", "request": "/v2?a=<x>", "requests": 7}},
        })

        hostile, marks, coded = result["inline"]
        self.assertNotIn("<img", hostile)
        self.assertIn("&lt;img", hostile)
        self.assertEqual(marks, "<em>Isle of Dogs</em> and <code>t5</code> and <strong>bold</strong>")
        self.assertEqual(coded, "<code>&lt;script&gt;alert(1)&lt;/script&gt;</code>")

        self.assertNotIn("<b>audience</b>", result["ref"])
        self.assertIn("&lt;b&gt;audience&lt;/b&gt;", result["ref"])
        self.assertIn("/v2?a=&lt;x&gt;", result["ref"])
        self.assertIn("7 requests", result["ref"])


REFRESH_HARNESS = EXTRACT + """
const make = new Function(extract('changesDeliverables') + '; return changesDeliverables;');
const changesDeliverables = make();
const cases = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
process.stdout.write(JSON.stringify(cases.map(c => changesDeliverables(c.type, c))));
"""


@unittest.skipUnless(shutil.which("node"), "node is not installed")
class DeliverablesRefreshTests(unittest.TestCase):
    """What makes the page ask for the listing again, for every host's write tool and every render."""

    def test_writes_from_either_host_and_renders_refresh_the_listing(self):
        cases = [
            {"type": "tool.call", "tool": "write_script"},          # Antigravity
            {"type": "tool.call", "tool": "Write"},                 # Claude Code, as poster2 recorded it
            {"type": "tool.call", "tool": "Edit"},
            {"type": "tool.call", "tool": "MultiEdit"},
            {"type": "render", "artifact": "artifacts/final_a.png", "format": "png"},
            {"type": "tool.call", "tool": "Read"},                   # reading changes nothing
            {"type": "tool.call", "tool": "mcp__polson__ExecuteScript"},
            {"type": "thinking"},
        ]
        with tempfile.TemporaryDirectory() as tmp:
            script, data = Path(tmp) / "h.js", Path(tmp) / "c.json"
            script.write_text(REFRESH_HARNESS, encoding="utf-8")
            data.write_text(json.dumps(cases), encoding="utf-8")
            out = subprocess.run(["node", str(script), str(RUN_HTML), str(data)],
                                 capture_output=True, text=True, timeout=60, check=True)

        self.assertEqual(json.loads(out.stdout), [True, True, True, True, True, False, False, False])


if __name__ == "__main__":
    unittest.main()
