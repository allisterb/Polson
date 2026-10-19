"""`serve.py`, the one entry point behind `polson studio`, `tools\\adk-studio.cmd` and the `adk-studio` launch config.

Nothing here serves: `uvicorn.run` is replaced, so these check what it would be asked to run and with what
environment, and the refusals that keep a mistyped folder from starting an empty studio.
"""

from __future__ import annotations

import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import serve  # noqa: E402


class ServeTests(unittest.TestCase):

    def setUp(self) -> None:
        self.root = tempfile.mkdtemp(prefix="polson-serve-")
        self.cwd = os.getcwd()
        self.env = mock.patch.dict(os.environ, {}, clear=False)
        self.env.start()
        for key in ("POLSON_PROJECTS_DIR", "POLSON_BUDGET_TOKENS"):
            os.environ.pop(key, None)

    def tearDown(self) -> None:
        self.env.stop()
        os.chdir(self.cwd)
        os.rmdir(self.root)

    def run_main(self, *argv: str):
        out, err = io.StringIO(), io.StringIO()
        with mock.patch("uvicorn.run") as run, redirect_stdout(out), redirect_stderr(err):
            code = serve.main(list(argv))
        return code, run, out.getvalue(), err.getvalue()

    def test_the_projects_folder_is_required(self):
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            serve.parse([])
        self.assertEqual(caught.exception.code, 2)

    def test_a_folder_that_does_not_exist_is_refused_and_nothing_is_served(self):
        code, run, _, err = self.run_main(os.path.join(self.root, "nope"))
        self.assertEqual(code, 2)
        self.assertIn("no such directory", err)
        run.assert_not_called()

    def test_without_a_built_cli_nothing_is_served(self):
        with mock.patch.object(serve, "REPO", Path(self.root)):
            code, run, _, err = self.run_main(self.root)
        self.assertEqual(code, 1)
        self.assertIn("build", err)
        run.assert_not_called()

    def test_it_serves_main_app_over_the_folder_with_the_defaults(self):
        if not (serve.REPO / "bin" / "cli" / "Polson.CLI.dll").is_file():
            self.skipTest("bin/cli is not built here")
        code, run, out, _ = self.run_main(self.root)
        self.assertEqual(code, 0)
        run.assert_called_once()
        self.assertEqual(run.call_args.args, ("main:app",))
        self.assertEqual(run.call_args.kwargs["port"], serve.DEFAULT_PORT)
        self.assertEqual(run.call_args.kwargs["host"], "127.0.0.1")
        self.assertEqual(Path(run.call_args.kwargs["app_dir"]), serve.HERE)
        self.assertEqual(os.environ["POLSON_PROJECTS_DIR"], str(Path(self.root).resolve()))
        self.assertEqual(os.environ["POLSON_BUDGET_TOKENS"], serve.DEFAULT_TOKENS)
        self.assertIn(f"localhost:{serve.DEFAULT_PORT}/studio/home", out)

    def test_a_token_cap_already_set_is_kept_and_the_port_is_taken(self):
        if not (serve.REPO / "bin" / "cli" / "Polson.CLI.dll").is_file():
            self.skipTest("bin/cli is not built here")
        os.environ["POLSON_BUDGET_TOKENS"] = "0"
        code, run, _, _ = self.run_main(self.root, "--port", "8123")
        self.assertEqual(code, 0)
        self.assertEqual(run.call_args.kwargs["port"], 8123)
        self.assertEqual(os.environ["POLSON_BUDGET_TOKENS"], "0")


if __name__ == "__main__":
    unittest.main()
