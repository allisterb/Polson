"""`newproject.write_app`: the generated app is what carries a run's own token cap to `build_app`."""

import ast
import tempfile
import unittest
from pathlib import Path

import newproject


class WriteAppTests(unittest.TestCase):
    def _call(self, **kwargs) -> ast.Call:
        with tempfile.TemporaryDirectory() as tmp:
            agent = newproject.write_app(Path(tmp) / "project", "setofsix", Path(tmp) / "apps", **kwargs) / "agent.py"
            tree = ast.parse(agent.read_text(encoding="utf-8"))
        return next(n.value for n in tree.body if isinstance(n, ast.Assign) and n.targets[0].id == "app")

    def test_a_set_carries_its_own_cap(self):
        keywords = {k.arg: ast.literal_eval(k.value) for k in self._call(budget_tokens=9_000_000).keywords}
        self.assertEqual(keywords, {"name": "setofsix", "budget_tokens": 9_000_000})

    def test_without_one_the_host_cap_applies(self):
        self.assertEqual([k.arg for k in self._call().keywords], ["name"])


if __name__ == "__main__":
    unittest.main()
