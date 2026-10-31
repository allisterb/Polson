"""Recreates the studio's log-based metrics, alert policies and uptime check in a Google Cloud project.

    python deploy/monitoring/apply.py <project> <service> <notification channel> [<host>]

`service` is the Cloud Run service to watch (`$S` in deploy/README.md); every filter is written for it, so the exported
files name no service of their own. The channel is the full name of one that already exists in that project,
`projects/<project>/notificationChannels/<id>` (`gcloud beta monitoring channels list`, or the console). `host`, when
given, is the service's own host name (no scheme or path): it adds an uptime check on `/studio/home` and an alert on
it. Metrics are created first because three alerts read them.

Uses the active gcloud login. Run it once: the APIs do not deduplicate, so a second run makes a second copy of each.
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys
import urllib.error
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent


def post(url: str, body: dict, token: str) -> dict:
    request = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"), method="POST", headers={
        "Authorization": f"Bearer {token}", "Content-Type": "application/json; charset=utf-8"})
    try:
        with urllib.request.urlopen(request) as answer:
            return json.load(answer)
    except urllib.error.HTTPError as exc:
        raise SystemExit(f"{url} refused it: {exc.code} {exc.read().decode('utf-8', 'replace')[:400]}") from exc


def load(path: pathlib.Path, **fill: str) -> dict:
    """An exported file with its placeholders filled: SERVICE_NAME, PROJECT_ID, SERVICE_HOST, CHECK_ID."""
    text = path.read_text(encoding="utf-8")
    for placeholder, value in fill.items():
        text = text.replace(placeholder, value)
    return json.loads(text)


def with_channel(policy: dict, channel: str) -> dict:
    policy["notificationChannels"] = [channel if c == "NOTIFICATION_CHANNEL" else c
                                      for c in policy.get("notificationChannels", [])]
    return policy


def main() -> None:
    if len(sys.argv) not in (4, 5) or "/notificationChannels/" not in sys.argv[3]:
        raise SystemExit(__doc__)
    project, service, channel = sys.argv[1:4]
    host = sys.argv[4] if len(sys.argv) == 5 else None
    token = subprocess.run("gcloud auth print-access-token", shell=True, check=True, capture_output=True,
                           text=True).stdout.strip()

    for path in sorted((HERE / "metrics").glob("*.json")):
        metric = load(path, SERVICE_NAME=service)
        post(f"https://logging.googleapis.com/v2/projects/{project}/metrics", metric, token)
        print("metric", metric["name"])

    for path in sorted((HERE / "alerts").glob("*.json")):
        made = post(f"https://monitoring.googleapis.com/v3/projects/{project}/alertPolicies",
                    with_channel(load(path, SERVICE_NAME=service), channel), token)
        print("alert ", made["displayName"])

    if host:
        check = post(f"https://monitoring.googleapis.com/v3/projects/{project}/uptimeCheckConfigs",
                     load(HERE / "uptime" / "studio-home-page.json", PROJECT_ID=project, SERVICE_HOST=host), token)
        print("uptime", check["displayName"])
        down = load(HERE / "uptime" / "studio-home-page-is-down.json", CHECK_ID=check["name"].rsplit("/", 1)[-1])
        made = post(f"https://monitoring.googleapis.com/v3/projects/{project}/alertPolicies", with_channel(down, channel), token)
        print("alert ", made["displayName"])


if __name__ == "__main__":
    main()
