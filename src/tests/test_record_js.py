"""The shared record script (`studio/static/record.js`), checked under Node.

Both the developer run page and the client request page describe events and render the agent's Markdown with
this one file, so its escaping is the boundary between an agent's words and the page's markup. The checks live in
`record_test.js`; this runs them as part of the ordinary suite, and skips where there is no `node`.

    python -m unittest tests.test_record_js      (from src/)
"""

from __future__ import annotations

import shutil
import subprocess
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent


class RecordScriptTests(unittest.TestCase):

    def test_record_js_under_node(self):
        node = shutil.which("node")
        if node is None:
            self.skipTest("node is not on the PATH")
        done = subprocess.run([node, str(HERE / "record_test.js")], capture_output=True, text=True,
                              encoding="utf-8", timeout=60)
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertRegex(done.stdout, r"\d+ passed, 0 failed")

    def test_both_pages_load_it_before_their_own_script(self):
        studio = HERE.parent / "studio" / "templates"
        run = (studio / "run.html").read_text(encoding="utf-8")
        base = (studio / "front" / "base.html").read_text(encoding="utf-8")
        self.assertLess(run.index("static/record.js"), run.index("<script>"), "run.html's inline script uses it")
        self.assertLess(base.index("static/record.js"), base.index("static/front.js"))


if __name__ == "__main__":
    unittest.main()
