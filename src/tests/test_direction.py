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

from test_run_page import EXTRACT, RUN_HTML
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

    def test_a_handle_in_prose_needs_its_boundaries(self):
        self.assertEqual(direction.refs("t2026 is a year, at5 is not a handle, (t7) is"), ["t7"])


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


HARNESS = EXTRACT + """
const make = new Function(extract('esc') + extract('inline') + extract('tasteRef') + '; return { inline, tasteRef };');
const { inline, tasteRef } = make();
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
            out = subprocess.run(["node", str(script), str(RUN_HTML), str(data)],
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
        self.assertEqual(marks, "<i>Isle of Dogs</i> and <code>t5</code> and <b>bold</b>")
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
