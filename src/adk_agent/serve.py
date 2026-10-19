"""Serves the studio on the ADK runtime, as the container does: `main.py`, with the studio mounted at /studio.

    python src/adk_agent/serve.py <projects-dir> [--port 8010] [--host 127.0.0.1]

**The main way to run the studio** (decided 2026-10-18), locally as well as deployed: what you test is what a
visitor gets. `polson studio`, `tools\\adk-studio.cmd` and the browser pane's `adk-studio` launch config all come
here, so the checks and defaults live once. The Antigravity-driven studio remains as `polson agy-studio`
(`python -m studio`), on the `python/` venv that holds the Antigravity standalone harness, which cannot share
`python-adk/`.

`<projects-dir>` is required and must exist, as for `python -m studio <root>`. It is where projects are listed
and watched, Claude Code and Antigravity runs included, and where the composer creates ADK projects. Keep it
outside the repository, so a run never inherits the repository's CLAUDE.md; usually `../PolsonRuns`.

Port 8010 by default, because 8000 is where `python -m studio` serves.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]

DEFAULT_PORT = 8010

#: The runaway cap a local run gets unless the environment says otherwise: cache-adjusted, and the same
#: number `studio.DEFAULT_TOKEN_CAP` uses. Set POLSON_BUDGET_TOKENS=0 for none.
DEFAULT_TOKENS = "2000000"

#: How long Ctrl-C waits for open responses before cutting them; see the `uvicorn.run` call.
SHUTDOWN_SECONDS = 2


def parse(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.environ.get("POLSON_VERB") or "serve.py",
        description="Serve the Polson studio on the ADK runtime, as the container runs it.")
    parser.add_argument("root", help="directory holding the project directories, usually ../PolsonRuns; "
                                     "it must already exist")
    parser.add_argument("--host", default="127.0.0.1",
                        help="interface to bind (default 127.0.0.1); think before going wider: a reachable "
                             "studio is an open cost surface")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"port (default {DEFAULT_PORT})")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse(argv)

    root = Path(args.root).resolve()
    if not root.is_dir():
        print(f"error: no such directory: {root}", file=sys.stderr)
        return 2
    cli = REPO / "bin" / "cli" / "Polson.CLI.dll"
    if not cli.is_file():
        print(f"error: no {cli.relative_to(REPO)}. Run build.cmd (./build elsewhere) from the repository root first.",
              file=sys.stderr)
        return 1

    # Before `main` is imported: `newproject` reads the projects directory at import, and the studio is
    # mounted over the same one.
    os.environ["POLSON_PROJECTS_DIR"] = str(root)
    os.environ.setdefault("POLSON_BUDGET_TOKENS", DEFAULT_TOKENS)
    os.chdir(REPO)

    print(f"Polson studio (ADK): http://localhost:{args.port}/studio/home")
    print(f"  projects  {root}")
    print(f"  token cap {os.environ['POLSON_BUDGET_TOKENS']}")
    sys.stdout.flush()

    import uvicorn  # noqa: PLC0415 - only once the arguments are known to be good

    # Ctrl-C hung at "Shutting down" with no limit: uvicorn waits for every response still being sent, and a
    # browser holds one open indefinitely once a <video> has buffered enough and stops reading. Measured
    # 2026-10-19 with a client that stopped reading final_a.mp4: "Waiting for connections to close" until that
    # client let go 86 s later. The event streams are not it; sse-starlette ends those itself within half a
    # second. Two seconds, then whatever is still open is cut, as `python -m studio` already does.
    uvicorn.run("main:app", host=args.host, port=args.port, app_dir=str(HERE),
                timeout_graceful_shutdown=SHUTDOWN_SECONDS)
    return 0


if __name__ == "__main__":
    sys.exit(main())
