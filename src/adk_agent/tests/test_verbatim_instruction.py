"""ADK templates a string instruction, and a JavaScript example in ours is a template variable to it."""

import asyncio
import json
import shutil
import tempfile
import unittest
from pathlib import Path

import studio

#: What the trailer template carried, verbatim: `{ path }` and `{ audio }` are bare identifiers in braces.
EXAMPLE = ("Carry a poster's shapes over: `comp.region({ path })` and `comp.outline({ path })`; join with "
           "`Motion.joinVideos(parts, 'output.mp4', { audio })`.")


class VerbatimInstructionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="polson-verbatim-")) / "acme"
        (self.root / "events").mkdir(parents=True)
        (self.root / "project.json").write_text(json.dumps({"id": "acme", "workflow": "trailer"}), encoding="utf-8")
        (self.root / "GEMINI.md").write_text(EXAMPLE, encoding="utf-8")

    def tearDown(self) -> None:
        shutil.rmtree(self.root.parent, ignore_errors=True)

    def test_a_code_example_in_braces_reaches_the_model_unchanged(self):
        """Before this, ADK raised `KeyError: Context variable not found: path` and the run never started."""
        agent = studio.build(self.root)
        text, bypass = asyncio.run(agent.canonical_instruction(None))
        self.assertTrue(bypass, "a string instruction is templated; a provider is not")
        self.assertIn("comp.region({ path })", text)
        self.assertIn("{ audio }", text)

    def test_the_instruction_can_still_be_read(self):
        agent = studio.build(self.root)
        self.assertIn("comp.region", agent.instruction)
        self.assertTrue(str(agent.instruction).startswith("Carry a poster's shapes"))


if __name__ == "__main__":
    unittest.main()
