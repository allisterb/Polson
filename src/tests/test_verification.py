"""`verification.md`: the run's checks as the record has them, written by the studio, failures and their fixes included.

    python -m unittest discover -s tests
"""

from __future__ import annotations

import unittest

from studio import verification


def check(claim: str, passed: bool, ts: str, detail: str = "", **more) -> dict:
    return {"type": "check", "claim": claim, "passed": passed, "detail": detail, "stage": "Checks",
            "ts": f"2026-10-27T21:{ts}.000Z", **more}


def film(video: str, ts: str, items: list[dict], strays: list[dict] = ()) -> dict:
    return {"type": "film.verify", "video": video, "model": "nvidia/Nemotron-3.5-Super-VL", "ts": f"2026-10-27T21:{ts}.000Z",
            "counts": {"shown": len(items), "seen": sum(1 for i in items if i["status"] == "seen")},
            "items": items, "strays": list(strays)}


class DocumentTests(unittest.TestCase):
    def test_nothing_checked_is_no_document(self):
        self.assertIsNone(verification.document([{"type": "render", "ts": "2026-10-27T21:00:00Z"}]))

    def test_a_failure_fixed_later_is_kept_with_what_fixed_it(self):
        """The view shows only the pass; the document keeps the failure that came first."""
        doc = verification.document([
            check("A: every line holds still long enough to read", False, "26:44", "card disclaimer 3.54s < 3.83s"),
            check("A: every line holds still long enough to read", True, "28:13", "every line holds"),
        ])
        self.assertIn("## Checks that failed", doc)
        self.assertIn("failed 1 time (21:26:44)", doc)
        self.assertIn("Measured: card disclaimer 3.54s < 3.83s", doc)
        self.assertIn("Passed at 21:28:13: every line holds", doc)
        self.assertIn("1 failed at least once and 1 of those passed later; 0 are still failing", doc)

    def test_repeated_failures_of_one_claim_are_one_entry(self):
        doc = verification.document([check("read-back of d", False, f"4{n}:00", "pending stray") for n in range(3)]
                                    + [check("read-back of d", True, "45:00", "settled")])
        self.assertEqual(doc.count("- **read-back of d**"), 1)
        self.assertIn("failed 3 times (21:40:00, 21:41:00, 21:42:00)", doc)

    def test_a_claim_still_failing_and_one_kept_on_purpose_are_told_apart(self):
        doc = verification.document([
            check("C: the concept stands apart", False, "45:31", "sits with 5 of the field"),
            check("the end card is centred", False, "46:00", "off by 3 px", accepted=True, reason="the grid wants it"),
        ])
        self.assertIn("**Still failing at the end of the run.**", doc)
        self.assertIn("Kept on purpose: the grid wants it", doc)

    def test_a_text_read_differently_is_shown_beside_the_text_set_even_when_counted_seen(self):
        """cruelty1: the reader read अनुबंध (contract) as अनूभू and the check counted it seen."""
        doc = verification.document([film("artifacts/final_b.mp4", "43:20", [
            {"text": "प्यार एक अनुबंध है।", "from": 2.08, "to": 4.83, "source": "own words", "readAs": "प्यार एक अनूभू है।",
             "status": "seen"},
            {"text": "असहनीय क्रूरता", "from": 7.7, "to": 10.7, "source": "own words", "status": "seen"}])])
        self.assertIn("| प्यार एक अनुबंध है। | 2.1–4.8 s | own words | प्यार एक अनूभू है। | seen (read differently) |", doc)
        self.assertIn("| असहनीय क्रूरता | 7.7–10.7 s | own words |  | seen |", doc)

    def test_a_fact_checked_on_the_web_shows_its_verdict_and_sources(self):
        doc = verification.document([film("artifacts/final_a.mp4", "43:20", [
            {"text": "DIRECTED BY F. GARY GRAY", "from": 12.0, "to": 15.0, "source": "web", "status": "seen",
             "detail": "supported (0.999): imdb.com, en.wikipedia.org",
             "web": {"claim": "Be Cool (2005) was directed by F. Gary Gray.", "verdict": "supported"}}])])
        self.assertIn("| DIRECTED BY F. GARY GRAY | 12.0–15.0 s | web: supported (0.999): imdb.com, en.wikipedia.org |", doc)

    def test_a_stray_shows_what_was_read_how_it_was_ruled_and_whether_a_reason_was_given(self):
        stray = {"n": 1, "read": "INTOLERABLE CRUELTY\nएंटी-सेमिटिक अबाधन", "at": 11, "frame": "artifacts/verify/stray-1.png",
                 "kind": "never set", "verdict": None}
        doc = verification.document([
            film("artifacts/final_b.mp4", "43:20", [], [stray]),
            {"type": "film.stray", "video": "artifacts/final_b.mp4", "n": 1, "verdict": "misread", "ts": "2026-10-27T21:43:37Z"},
        ])
        self.assertIn("read as “INTOLERABLE CRUELTY / एंटी-सेमिटिक अबाधन”", doc)
        self.assertIn("Ruled: **misread** — no reason given", doc)
        self.assertIn("1 ruled a misreading, 0 never settled", doc)

    def test_a_frame_another_films_check_overwrote_is_said_to_be(self):
        """`Verify.stray` frames are reused across films, so the file can show another film's stray."""
        stray = {"n": 1, "read": "x", "at": 11, "frame": "artifacts/verify/stray-1.png", "kind": "never set"}
        doc = verification.document([film("artifacts/final_b.mp4", "43:20", [], [stray]),
                                     film("artifacts/final_d.mp4", "43:39", [], [stray])])
        b = doc.split("### `artifacts/final_b.mp4`")[1].split("###")[0]
        d = doc.split("### `artifacts/final_d.mp4`")[1]
        self.assertIn("overwritten by a later check of `artifacts/final_d.mp4`", b)
        self.assertNotIn("overwritten", d)

    def test_every_check_is_listed_in_order_and_table_cells_cannot_break_the_table(self):
        doc = verification.document([check("a | b", True, "10:00", "x\ny"), check("c", False, "11:00")])
        listing = doc.split("## Every check, in order")[1]
        self.assertLess(listing.index("a \\| b"), listing.index("| c |"))
        self.assertIn("x / y", listing)
        self.assertIn("**fail**", listing)

    def test_a_previous_runs_events_are_left_out(self):
        doc = verification.document([check("old", False, "00:00"), check("new", True, "30:00")], since="2026-10-27T21:20:00Z")
        self.assertNotIn("old", doc)
        self.assertIn("| new |", doc)


class UnresolvedTests(unittest.TestCase):
    """What the run page shows beside the finished work: checks failing at the end, whatever the report says."""

    def test_the_studios_failing_checks_come_first_and_fixed_or_kept_ones_are_left_out(self):
        """the_odyssey: both films at 1280 × 720 against 1920 × 1080, and a report that never said so."""
        size = "Delivered size of artifacts/final_a.mp4: one the brief asks for"
        got = verification.unresolved([
            check("the title holds two seconds", False, "10:00", "1.4 s"),
            check(size, False, "20:00", "1280 × 720 px; the brief asks for 1920 × 1080.", by="verify"),
            check("contrast", False, "21:00", "3.9 : 1"), check("contrast", True, "22:00", "4.6 : 1"),
            check("the end card is centred", False, "23:00", "3 px", accepted=True, reason="the grid wants it"),
        ])
        self.assertEqual([(u["claim"], u["studio"]) for u in got], [(size, True), ("the title holds two seconds", False)])
        self.assertEqual(got[0]["detail"], "1280 × 720 px; the brief asks for 1920 × 1080.")

    def test_a_check_that_failed_then_passed_is_resolved(self):
        self.assertEqual(verification.unresolved([check("x", False, "10:00"), check("x", True, "11:00")]), [])

    def test_a_previous_runs_failures_are_left_out(self):
        got = verification.unresolved([check("old", False, "00:00"), check("new", False, "30:00")], since="2026-10-27T21:20:00Z")
        self.assertEqual([u["claim"] for u in got], ["new"])


class UnplacedTests(unittest.TestCase):
    """A picture the client supplied that never reached the piece, which no check of the agent's would record."""

    BRIEF = ("A sign.\n\n---\n\n## What to deliver\n\n- Logo, in documents/: sole mates.svg\n"
             "- Images to include, in documents/: shopfront.jpg, trainer.png\n- Files supplied, in documents/: guide.pdf\n")

    def test_the_briefs_logo_and_images_are_what_is_supplied(self):
        self.assertEqual(verification.supplied(self.BRIEF),
                         ["documents/sole mates.svg", "documents/shopfront.jpg", "documents/trainer.png"])
        self.assertEqual(verification.supplied("A brief with no pictures.\n- Files supplied, in documents/: a.png\n"), [])

    def test_each_picture_no_place_event_drew_is_named_as_the_studios_finding(self):
        events = [{"type": "place", "file": "documents/Sole Mates.svg", "ts": "2026-11-18T10:00:00Z"},
                  {"type": "place", "file": "documents/trainer.png", "ts": "2026-11-17T10:00:00Z"}]
        got = verification.unplaced(events, self.BRIEF, since="2026-11-18T00:00:00Z")
        self.assertEqual([(u["claim"], u["studio"]) for u in got],
                         [("documents/shopfront.jpg is on the piece", True), ("documents/trainer.png is on the piece", True)])


if __name__ == "__main__":
    unittest.main()
