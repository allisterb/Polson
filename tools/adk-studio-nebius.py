"""Serves the studio on the ADK runtime with Nemotron on Nebius Token Factory as the agents' model.

    py -3.13 tools/adk-studio-nebius.py <projects-dir> [--port 8011] [--model nebius/nvidia/Nemotron-3.5-Super-VL]

The same `src/adk_agent/serve.py` as `tools/adk-studio.cmd`, on the `python-nebius-adk/` venv (LiteLLM; see
`src/adk_agent/requirements-nebius.in`) and on port 8011, so it can run beside the Qloo studio on 8010.

**The key never passes through a command line or the console.** It is read here from `bin/cli/appsettings.json`
(`ApiKeys:NebiusTokenFactory`, `NebiusTokenFactory:BaseUrl`) into the child's environment as `NEBIUS_API_KEY` and
`NEBIUS_API_BASE`, which is what LiteLLM's `nebius/` provider and `studio.model_for` read. Values already set in the
environment win. Stop with Ctrl+C, or `tools/adk-studio-stop.cmd 8011`.
"""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PYTHON = ROOT / "python-nebius-adk" / "Scripts" / "python.exe"
SETTINGS = ROOT / "bin" / "cli" / "appsettings.json"
DEFAULT_MODEL = "nebius/nvidia/Nemotron-3.5-Super-VL"


def settings() -> dict:
    """bin/cli's settings, read the way .NET does: comments and trailing commas allowed."""
    text = SETTINGS.read_text(encoding="utf-8-sig")
    text = re.sub(r"^\s*//.*$", "", text, flags=re.M)
    return json.loads(re.sub(r",(\s*[}\]])", r"\1", text))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("projects", help="the projects folder, e.g. ../PolsonRuns")
    parser.add_argument("--port", default="8011")
    parser.add_argument("--model", default=os.environ.get("POLSON_MODEL", "").strip() or DEFAULT_MODEL)
    args = parser.parse_args()

    if not PYTHON.is_file():
        print(f"No python-nebius-adk venv at {PYTHON.parent.parent}. See src/adk_agent/requirements-nebius.in.")
        return 1
    env = dict(os.environ, PYTHONUTF8="1", POLSON_MODEL=args.model, POLSON_VERB="tools/adk-studio-nebius.py")
    # A larger token cap than the 2M local default, which is sized for Gemini: with the manuals inlined and a model
    # that reasons at length, nemologo4 was halted by the breaker at 7 of its 20 minutes. At Token Factory's
    # $0.30 per million input tokens, a run that spends all of it costs about $2.40. The environment still wins.
    env.setdefault("POLSON_BUDGET_TOKENS", "8000000")
    try:
        s = settings()
        env.setdefault("NEBIUS_API_KEY", s["ApiKeys"]["NebiusTokenFactory"])
        env.setdefault("NEBIUS_API_BASE", s["NebiusTokenFactory"]["BaseUrl"])
    except (OSError, KeyError, ValueError) as exc:
        # Names only: never the settings' values.
        print(f"Could not read ApiKeys:NebiusTokenFactory and NebiusTokenFactory:BaseUrl from {SETTINGS} "
              f"({type(exc).__name__}).")
        return 1

    print(f"studio on port {args.port}, model {args.model}, projects {args.projects}")
    serve = [str(PYTHON), str(ROOT / "src" / "adk_agent" / "serve.py"), args.projects, "--port", args.port]
    try:
        return subprocess.call(serve, env=env)
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    sys.exit(main())
