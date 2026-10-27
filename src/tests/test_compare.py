"""The audiences' films side by side in one MP4: which films, under what labels, and when the kept file still stands.

The encode itself is the CLI's (`ComparisonTests` reads its output back); these test what the studio decides around it,
with no CLI and no ffmpeg.

    python -m unittest discover -s tests
"""

from __future__ import annotations

import asyncio
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from studio import compare


def piece(letter: str, *paths: str) -> dict:
    return {"kind": "render", "audience": letter, "formats": [{"format": Path(p).suffix[1:], "path": p} for p in paths]}


AUDIENCES = [{"letter": "A", "place": "Dubai", "ageLabel": "25–34", "languageLabel": "Arabic"},
             {"letter": "B", "place": "Tokyo", "ageLabel": "", "languageLabel": ""}]


class FilmsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_each_audiences_film_is_labelled_as_the_page_labels_it(self):
        films = compare.films(self.root, [piece("A", "artifacts/final_a.mp4", "artifacts/final_a.webp"),
                                          piece("B", "artifacts/final_b.mp4")], AUDIENCES)
        self.assertEqual([(f["path"], f["title"], f["detail"]) for f in films],
                         [("artifacts/final_a.mp4", "Audience A · Dubai", "25–34, in Arabic"),
                          ("artifacts/final_b.mp4", "Audience B · Tokyo", "")])
        self.assertEqual(Path(films[0]["full"]), (self.root / "artifacts/final_a.mp4").resolve())

    def test_a_run_made_outside_the_form_is_labelled_by_letter(self):
        films = compare.films(self.root, [piece("A", "a.mp4"), piece("B", "b.mp4")], [])
        self.assertEqual([f["title"] for f in films], ["Audience A", "Audience B"])

    def test_nothing_to_compare_without_a_film_for_every_audience(self):
        self.assertEqual(compare.films(self.root, [piece("A", "a.mp4"), piece("B", "b.webp")], AUDIENCES), [])
        self.assertEqual(compare.films(self.root, [piece("A", "a.mp4")], AUDIENCES), [])


class KeptTests(unittest.TestCase):
    """A kept file is offered only while it was made from exactly these films and labels."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "artifacts").mkdir()
        for name in ("final_a.mp4", "final_b.mp4"):
            (self.root / "artifacts" / name).write_bytes(b"film " + name.encode())
        self.pieces = [piece("A", "artifacts/final_a.mp4"), piece("B", "artifacts/final_b.mp4")]

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def made(self, audiences=AUDIENCES) -> list[dict]:
        """As if the CLI had written the file: the films, the output, and the stamp `build` keeps."""
        films = compare.films(self.root, self.pieces, audiences)

        async def cli(*argv, **_):
            (self.root / compare.OUTPUT).write_bytes(b"side by side")
            listing = json.loads(Path(argv[argv.index("--films") + 1]).read_text(encoding="utf-8"))
            self.assertEqual([f["title"] for f in listing], [f["title"] for f in films])
            process = mock.Mock(returncode=0)
            process.communicate = mock.AsyncMock(return_value=(b"{}", b""))
            return process

        with mock.patch.object(compare.asyncio, "create_subprocess_exec", side_effect=cli):
            made = asyncio.run(compare.build(self.root, films))
        self.assertEqual(made, {"path": compare.OUTPUT, "bytes": len(b"side by side")})
        return films

    def test_none_before_it_is_made(self):
        self.assertIsNone(compare.current(self.root, compare.films(self.root, self.pieces, AUDIENCES)))

    def test_made_once_and_then_served_without_the_cli(self):
        films = self.made()
        self.assertEqual(compare.current(self.root, films), {"path": compare.OUTPUT, "bytes": 12})
        with mock.patch.object(compare.asyncio, "create_subprocess_exec", side_effect=AssertionError("ran the CLI again")):
            self.assertEqual(asyncio.run(compare.build(self.root, films))["path"], compare.OUTPUT)

    def test_a_film_that_changes_makes_it_stale(self):
        films = self.made()
        film = self.root / "artifacts" / "final_b.mp4"
        film.write_bytes(b"a longer film than before")
        self.assertIsNone(compare.current(self.root, films))

    def test_new_labels_make_it_stale(self):
        self.made()
        renamed = [AUDIENCES[0], {**AUDIENCES[1], "place": "Osaka"}]
        self.assertIsNone(compare.current(self.root, compare.films(self.root, self.pieces, renamed)))

    def test_a_refusal_is_the_clis_own_sentence_and_keeps_nothing(self):
        films = compare.films(self.root, self.pieces, AUDIENCES)
        process = mock.Mock(returncode=1)
        process.communicate = mock.AsyncMock(return_value=(b"", b"compare: film 2 has no sound to keep.\n"))
        with mock.patch.object(compare.asyncio, "create_subprocess_exec", mock.AsyncMock(return_value=process)):
            with self.assertRaisesRegex(compare.StudioError, "has no sound to keep"):
                asyncio.run(compare.build(self.root, films))
        self.assertFalse((self.root / compare.STAMP).exists())


if __name__ == "__main__":
    unittest.main()
