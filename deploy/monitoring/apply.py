"""Recreates the studio's log-based metrics and alert policies in a Google Cloud project.

    python deploy/monitoring/apply.py <project> <notification channel>

The channel is the full name of one that already exists in that project, `projects/<project>/notificationChannels/<id>`
(`gcloud beta monitoring channels list`, or the console). Metrics are created first because three alerts read them.
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


def main() -> None:
    if len(sys.argv) != 3 or "/notificationChannels/" not in sys.argv[2]:
        raise SystemExit(__doc__)
    project, channel = sys.argv[1], sys.argv[2]
    token = subprocess.run("gcloud auth print-access-token", shell=True, check=True, capture_output=True,
                           text=True).stdout.strip()

    for path in sorted((HERE / "metrics").glob("*.json")):
        metric = json.loads(path.read_text(encoding="utf-8"))
        post(f"https://logging.googleapis.com/v2/projects/{project}/metrics", metric, token)
        print("metric", metric["name"])

    for path in sorted((HERE / "alerts").glob("*.json")):
        policy = json.loads(path.read_text(encoding="utf-8"))
        policy["notificationChannels"] = [channel if c == "NOTIFICATION_CHANNEL" else c
                                          for c in policy.get("notificationChannels", [])]
        made = post(f"https://monitoring.googleapis.com/v3/projects/{project}/alertPolicies", policy, token)
        print("alert ", made["displayName"])


if __name__ == "__main__":
    main()
