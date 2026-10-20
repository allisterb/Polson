"""`peek` puts the picture into the next request itself — one call to look, not `peek` then `load_artifacts`.

nemologo3 (Nemotron, 2026-10-20) saw its first renders, then read `load_artifacts`' "temporarily inserted and removed"
as proof it could not see, and worked from ASCII pixel dumps for the rest of the run.
"""

import asyncio
import base64
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from google.adk.models.llm_request import LlmRequest  # noqa: E402
from google.genai import types  # noqa: E402

import studio  # noqa: E402

#: A 1x1 PNG.
PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")


def request_after(*responses: tuple[str, dict]) -> LlmRequest:
    parts = [types.Part(function_response=types.FunctionResponse(name=name, response=body)) for name, body in responses]
    return LlmRequest(contents=[types.Content(role="user", parts=parts)])


class AttachPeeksTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-peek-")) / "acme"
        (self.root / "artifacts").mkdir(parents=True)
        (self.root / "artifacts" / "01.png").write_bytes(PNG)
        self.attach = studio._make_attach_peeks(self.root)

    def tearDown(self) -> None:
        shutil.rmtree(self.root.parent, ignore_errors=True)

    def run_on(self, request: LlmRequest) -> LlmRequest:
        self.assertIsNone(asyncio.run(self.attach(None, request)), "it adds to the request; it never answers")
        return request

    def test_a_peek_puts_the_image_in_the_next_request(self):
        request = self.run_on(request_after(("peek", {"ok": True, "source": "artifacts/01.png"})))
        self.assertEqual(len(request.contents), 2)
        added = request.contents[-1]
        self.assertEqual(added.role, "user")
        self.assertIn("You are looking at it now", added.parts[0].text)
        self.assertEqual((added.parts[1].inline_data.mime_type, added.parts[1].inline_data.data), ("image/png", PNG))

    def test_two_peeks_in_one_turn_attach_both(self):
        (self.root / "artifacts" / "02.png").write_bytes(PNG)
        request = self.run_on(request_after(("peek", {"ok": True, "source": "artifacts/01.png"}),
                                            ("peek", {"ok": True, "source": "artifacts/02.png"})))
        self.assertEqual(sum(1 for p in request.contents[-1].parts if p.inline_data), 2)

    def test_nothing_is_attached_for_a_failed_peek_another_tool_or_a_path_outside(self):
        for responses in ((("peek", {"ok": False, "error": "no file"}),),
                          (("ExecuteScript", {"success": True}),),
                          (("peek", {"ok": True, "source": "../elsewhere.png"}),),
                          (("peek", {"ok": True, "source": "artifacts/missing.png"}),)):
            with self.subTest(responses=responses):
                self.assertEqual(len(self.run_on(request_after(*responses)).contents), 1)

    def test_peek_no_longer_sends_the_model_to_load_artifacts(self):
        class Context:
            invocation_id = "i"

            async def save_artifact(self, filename, part):
                return 0

        result = asyncio.run(studio._make_peek(self.root)("artifacts/01.png", Context()))
        self.assertTrue(result["ok"])
        self.assertNotIn("load_artifacts", result["note"])
        self.assertIn("next turn", result["note"])

    def test_every_agent_attaches_peeks(self):
        (self.root / "events").mkdir()
        (self.root / "project.json").write_text(json.dumps({"id": "acme", "workflow": "logo"}), encoding="utf-8")
        (self.root / "GEMINI.md").write_text("# instructions", encoding="utf-8")
        agent = studio.build(self.root)
        for a in [agent, *(getattr(agent, "sub_agents", None) or [])]:
            names = [getattr(c, "__name__", "") for c in a.canonical_before_model_callbacks]
            self.assertIn("attach_peeks", names, a.name)


if __name__ == "__main__":
    unittest.main()
