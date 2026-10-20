"""A call to a tool that does not exist is answered, not fatal (nemologo1: Nemotron called `Stage.begin` as a tool)."""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import studio  # noqa: E402

AVAILABLE = "ExecuteScript, ReadDoc, read_file, peek, ask_director"


def missing(name: str) -> ValueError:
    """The error ADK's `_get_tool` raises, in its own words."""
    return ValueError(f"Tool '{name}' not found.\nAvailable tools: {AVAILABLE}\n\nPossible causes:\n  1. LLM hallucinated")


class Tool:
    def __init__(self, name):
        self.name = name


class UnknownToolTests(unittest.TestCase):
    def test_an_sdk_call_made_as_a_tool_is_sent_to_execute_script(self):
        reply = studio._unknown_tool(Tool("Stage.begin"), {"stage": "Brief"}, None, missing("Stage.begin"))
        self.assertIn("JavaScript SDK", reply["error"])
        self.assertIn('script: "Stage.begin(...);"', reply["remedy"])
        self.assertIn(AVAILABLE, reply["remedy"])

    def test_a_bare_sdk_global_is_recognised_too(self):
        self.assertIn("JavaScript SDK", studio._unknown_tool(Tool("Snap"), {}, None, missing("Snap"))["error"])

    def test_any_other_unknown_name_lists_the_real_tools(self):
        reply = studio._unknown_tool(Tool("draw_logo"), {}, None, missing("draw_logo"))
        self.assertIn("no tool called 'draw_logo'", reply["error"])
        self.assertIn(AVAILABLE, reply["remedy"])

    def test_other_errors_keep_their_own_handling(self):
        self.assertIsNone(studio._unknown_tool(Tool("ExecuteScript"), {}, None, RuntimeError("engine died")))
        self.assertIsNone(studio._unknown_tool(Tool("ExecuteScript"), {}, None, ValueError("bad width")))

    def test_every_agent_has_it(self):
        root = Path(__file__).resolve().parent / "_unknown_tool_project" / "acme"
        (root / "events").mkdir(parents=True, exist_ok=True)
        (root / "project.json").write_text('{"id": "acme", "workflow": "logo"}', encoding="utf-8")
        (root / "GEMINI.md").write_text("# instructions", encoding="utf-8")
        try:
            agent = studio.build(root)
            for a in [agent, *(getattr(agent, "sub_agents", None) or [])]:
                self.assertIn(studio._unknown_tool, a.canonical_on_tool_error_callbacks, a.name)
        finally:
            import shutil
            shutil.rmtree(root.parent, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
