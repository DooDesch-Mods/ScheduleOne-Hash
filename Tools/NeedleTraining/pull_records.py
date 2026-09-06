"""Fetch the shared request records from hash.doomods.com.

This is the half of the loop that makes the other half worth anything. Players record every `# ` request
locally and upload it when they switch sharing on; without a way to read it back, the collection improves
nothing. `cases_from_records.py` turns what this writes into benchmark cases.

The token is a bearer secret and is deliberately not in this repository. Set it in the environment:

    HASH_TELEMETRY_TOKEN=<the EXPORT_TOKEN from the Dokploy app>  python pull_records.py

It writes JSONL, one record per line, appending only what it has not already seen - the day of the newest
record it holds is what it asks for next time, so a re-run costs one small request rather than the history.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent
DEFAULT_ENDPOINT = "https://hash.doomods.com/api/records"


def newest_day(path: pathlib.Path) -> str:
    """The latest day already on disk, so the next pull can start there rather than at the beginning."""
    if not path.exists():
        return ""

    day = ""
    for line in path.read_text(encoding="utf-8").split("\n"):
        if not line.strip():
            continue
        try:
            value = str(json.loads(line).get("day", ""))
        except json.JSONDecodeError:
            continue
        if value > day:
            day = value
    return day


def identity(record: dict) -> str:
    """What makes two records the same event.

    The mod puts a nonce on every record, so a batch uploaded twice - a lost response, a timed-out request -
    collapses here exactly. Content cannot do that job: two identical lines may be one retransmission or one
    player asking the same thing twice, and how often a request is asked is worth knowing.

    Records written before the mod carried an id fall back to content, which is the old behaviour and the
    old flaw; they are a fixed, shrinking set.
    """
    record_id = str(record.get("id", ""))
    if record_id:
        return "id:" + record_id

    return "content:" + json.dumps(
        [record.get("day", ""), record.get("query", ""), record.get("commands", []),
         record.get("outcome", ""), record.get("actual", "")],
        sort_keys=True, ensure_ascii=False)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint", default=os.environ.get("HASH_TELEMETRY_ENDPOINT", DEFAULT_ENDPOINT))
    parser.add_argument("--out", type=pathlib.Path, default=ROOT / "data-shared" / "records.jsonl")
    parser.add_argument("--token", default=os.environ.get("HASH_TELEMETRY_TOKEN", ""))
    parser.add_argument("--since", default="",
                        help="day to start at (YYYY-MM-DD); default is the newest day already held")
    parser.add_argument("--all", action="store_true", help="ask for the whole history rather than the tail")
    args = parser.parse_args()

    if not args.token:
        raise SystemExit("no token: set HASH_TELEMETRY_TOKEN (the EXPORT_TOKEN of the Dokploy app)")

    args.out.parent.mkdir(parents=True, exist_ok=True)

    since = "" if args.all else (args.since or newest_day(args.out))
    url = args.endpoint + (f"?since={since}" if since else "")

    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + args.token})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            body = response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", errors="replace")[:200]
        raise SystemExit(f"the server answered {error.code}: {detail}") from error
    except urllib.error.URLError as error:
        raise SystemExit(f"could not reach {args.endpoint}: {error.reason}") from error

    existing: dict[str, str] = {}
    if args.out.exists():
        for line in args.out.read_text(encoding="utf-8").split("\n"):
            if not line.strip():
                continue
            try:
                existing[identity(json.loads(line))] = line
            except json.JSONDecodeError:
                continue

    added = 0
    malformed = 0
    with args.out.open("a", encoding="utf-8") as handle:
        for line in body.split("\n"):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                malformed += 1
                continue
            key = identity(record)
            if key in existing:
                continue
            existing[key] = line
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
            added += 1

    print(f"pulled from {since or 'the beginning'}: {added} new, {len(existing)} held -> {args.out}")
    if malformed:
        print(f"WARNING: {malformed} line(s) were not JSON and were skipped")


if __name__ == "__main__":
    main()
