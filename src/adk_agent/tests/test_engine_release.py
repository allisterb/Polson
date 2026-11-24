"""A finished run's engine process is stopped, not left running (the 2026-11-24 out-of-memory kill).

ADK caches one runner per app until shutdown and every request is its own app, so every finished run kept its
`Polson.CLI server` — ~1 GB each on Cloud Run, three runs to fill 4 GiB. This drives real runs through ADK's own API
server and `intake.launch`, with a scripted model (no spend) and the real engine, then counts the dotnet processes left.
Without `studio.release` in `launch` it finds one per run.
"""

import asyncio
import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
import uuid
from pathlib import Path

ADK = Path(__file__).resolve().parent.parent
REPO = ADK.parent.parent
sys.path.insert(0, str(ADK))

CLI = REPO / "bin" / "cli" / "Polson.CLI.dll"

# One generated-style app: `build_app` on a bare project, with the model swapped for one that runs a script once
# (stashing 64 MB of bitmaps in Session, as a real run's stages do) and then stops.
AGENT = textwrap.dedent('''
    import json
    from pathlib import Path
    from google.adk.models.base_llm import BaseLlm
    from google.adk.models.llm_response import LlmResponse
    from google.genai import types
    import studio

    SCRIPT = ("for (let i = 0; i < 4; i++) { const c = createCanvas(2000, 2000); "
              "c.getContext('2d').fillRect(0, 0, 2000, 2000); Session['b' + i] = c.toBitmap(); }")
    #: ExecuteScript's own `success`, once per run: the proof that the engine was actually reached.
    ANSWERS = []

    class Scripted(BaseLlm):
        model: str = "scripted"

        async def generate_content_async(self, request, stream=False):
            answered = [p.function_response for c in request.contents for p in (c.parts or []) if p.function_response]
            if answered:
                ANSWERS.append(json.loads(answered[-1].response["content"][0]["text"])["success"])
                part = types.Part(text="done")
            else:
                part = types.Part(function_call=types.FunctionCall(name="ExecuteScript", args={"script": SCRIPT, "render": False}))
            yield LlmResponse(content=types.Content(role="model", parts=[part]))

    here = Path(__file__).resolve().parent
    project = here / "project"
    project.mkdir(exist_ok=True)
    (project / "GEMINI.md").write_text("Run one script and stop.", encoding="utf-8")
    # Swapped only while this app is built: the module is shared with every other test in the process.
    original, studio.model_for = studio.model_for, lambda name: Scripted()
    try:
        app = studio.build_app(project, name=here.name, cli_dll=r"CLI_DLL")
    finally:
        studio.model_for = original
''')


def engines() -> list[int]:
    """The pids of dotnet processes descended from this one."""
    if os.name == "nt":
        out = subprocess.run(["powershell", "-NoProfile", "-Command",
                              "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name | ConvertTo-Json"],
                             capture_output=True, text=True, check=True).stdout
        procs = [(p["ProcessId"], p["ParentProcessId"], p["Name"]) for p in json.loads(out)]
    else:
        out = subprocess.run(["ps", "-eo", "pid=,ppid=,comm="], capture_output=True, text=True, check=True).stdout
        procs = [(int(a), int(b), c) for a, b, c in (line.split(None, 2) for line in out.splitlines())]
    parent = {pid: ppid for pid, ppid, _ in procs}

    def ours(pid: int) -> bool:
        seen = set()
        while pid and pid not in seen:
            if pid == os.getpid():
                return True
            seen.add(pid)
            pid = parent.get(pid)
        return False

    return [pid for pid, ppid, name in procs if name.lower().startswith("dotnet") and ours(ppid)]


@unittest.skipUnless(CLI.is_file(), f"needs the engine at {CLI}: ./build")
class EngineReleaseTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        import httpx  # noqa: F401 - the launcher's transport; fail here, not mid-run
        from google.adk.cli.fast_api import get_fast_api_app
        import intake

        self.intake = intake
        self.agents = Path(tempfile.mkdtemp(prefix="polson-release-"))
        # Fresh names per test: ADK imports each app as a top-level module, and a reused name would load the last
        # test's copy from sys.modules.
        tag = uuid.uuid4().hex[:8]
        self.first, self.second = f"first_{tag}", f"second_{tag}"
        for name in (self.first, self.second):
            (self.agents / name).mkdir()
            (self.agents / name / "__init__.py").write_text("from . import agent\n", encoding="utf-8")
            (self.agents / name / "agent.py").write_text(AGENT.replace("CLI_DLL", str(CLI)), encoding="utf-8")
        self.app = get_fast_api_app(agents_dir=str(self.agents), session_service_uri="memory://", web=False)

    async def asyncTearDown(self):
        import studio
        for name in (self.first, self.second):
            await studio.release(name)
        shutil.rmtree(self.agents, ignore_errors=True)

    def answers(self, name: str) -> list[bool]:
        return sys.modules[f"{name}.agent"].ANSWERS

    async def test_a_finished_run_leaves_no_engine_running(self):
        await self.intake.launch(self.app, self.first, "go")
        await self.intake.launch(self.app, self.second, "go")
        await asyncio.sleep(0.5)

        self.assertEqual(self.answers(self.first), [True], "the script never ran, so nothing was tested")
        self.assertEqual(self.answers(self.second), [True])
        self.assertEqual(engines(), [], "a finished run's Polson.CLI server is still running")

    async def test_the_same_request_runs_again_on_a_fresh_engine(self):
        await self.intake.launch(self.app, self.first, "go")
        await self.intake.launch(self.app, self.first, "go")
        await asyncio.sleep(0.5)

        self.assertEqual(self.answers(self.first), [True, True], "a released toolset did not reopen for the next run")
        self.assertEqual(engines(), [])


if __name__ == "__main__":
    unittest.main()
