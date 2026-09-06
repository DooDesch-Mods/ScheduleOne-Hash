"""Turn shared player records into benchmark cases.

`RESULTS.md` records eight training runs whose last three scored 14, 16 and 18 of 79 hand-written cases -
differences inside the noise of a set that small. This is how the set grows without anyone writing cases by
hand: a record where the player corrected hash carries the right answer beside the wrong one, and a record
the console accepted carries a request that demonstrably worked.

Adding a case also removes its phrasing from training. `generate_data.expected_assignments` builds
`excluded_queries` from every case in `cases.json`, and `validate_dataset` drops any row that collides with
one - so a case can never become something the adapter memorised. That guard is the reason this writes to
`cases.json` rather than to a second file nothing else reads.

    python pull_records.py
    python cases_from_records.py                 # what it would add, and why the rest was dropped
    python cases_from_records.py --write         # actually append them

Every candidate is checked against the committed catalogue snapshot in `data/`. A record naming a command
the snapshot does not have is not a bad record - it is a player on a newer game or with a mod we have not
indexed, and the count of those is the signal that `pull_schema.py` needs re-running.
"""

from __future__ import annotations

import argparse
import collections
import json
import pathlib

import generate_data as g

ROOT = pathlib.Path(__file__).resolve().parent
CASES = ROOT.parent / "NeedleBenchmark" / "cases.json"

# A locale the mod sent, mapped to the language the corpus knows. Anything else is recorded as it came:
# the language decides the system block a case is rendered with, and guessing it wrong makes the case
# measure a contract no player got.
LANGUAGE_OF_LOCALE = {"en": "en", "de": "de", "es": "es", "fr": "fr"}


def language_of(record: dict) -> str:
    locale = str(record.get("locale", ""))
    return LANGUAGE_OF_LOCALE.get(locale.split("-", 1)[0].lower(), "en")


def known_answer(record: dict) -> str | None:
    """The console line this request should have produced, when the record actually establishes one.

    Three kinds of record do:

      corrected  the commands ran and the player immediately typed a different one - `actual` is the answer
      rejected   nothing ran or the console refused, and the player then typed one - same
      accepted   the commands ran and nothing contradicted them, so the command itself is the answer

    `accepted` is the weakest of the three and is included because it is also the most common: a benchmark
    made only of failures measures how well the model recovers, not how well it works.
    """
    outcome = str(record.get("outcome", ""))
    actual = str(record.get("actual", "")).strip()

    if outcome in ("corrected", "rejected"):
        return actual or None
    if outcome == "accepted":
        commands = [c for c in record.get("commands", []) if isinstance(c, str) and c.strip()]
        return commands[0].strip() if len(commands) == 1 else None
    return None


def resolvable(expected: str, tools_by_name: dict, values: dict) -> str | None:
    """Why this expected line cannot be a case, or None when it can.

    The same three checks the mod applies before it runs anything: the command has to exist, it has to take
    the number of arguments given, and a value has to be one the catalogue offers. A case whose answer the
    game would refuse would score every model wrong forever.
    """
    tokens = g.console_tokens(expected)
    if not tokens:
        return "empty"

    tool = tools_by_name.get(tokens[0])
    if tool is None:
        return f"unknown command {tokens[0]!r}"

    properties = list(tool["parameters"].get("properties", {}))
    supplied = len(tokens) - 1
    if supplied > len(properties):
        return f"{tokens[0]} takes {len(properties)} argument(s), the line has {supplied}"

    required = sum(1 for name in properties
                   if name in tool["parameters"].get("required", properties))
    if supplied < min(required, len(properties)):
        return f"{tokens[0]} needs {required} argument(s), the line has {supplied}"

    return None


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--records", type=pathlib.Path, default=ROOT / "data-shared" / "records.jsonl")
    parser.add_argument("--cases", type=pathlib.Path, default=CASES)
    parser.add_argument("--tools", type=pathlib.Path, default=ROOT / "data" / "tools.json")
    parser.add_argument("--values", type=pathlib.Path, default=ROOT / "data" / "values.json")
    parser.add_argument("--write", action="store_true", help="append the accepted candidates to cases.json")
    parser.add_argument("--limit", type=int, default=0, help="stop after this many new cases")
    args = parser.parse_args()

    if not args.records.exists():
        raise SystemExit(f"no records at {args.records} - run pull_records.py first")

    tools = json.loads(args.tools.read_text(encoding="utf-8"))
    tools_by_name = {tool["name"]: tool for tool in tools}
    values = json.loads(args.values.read_text(encoding="utf-8"))

    document = json.loads(args.cases.read_text(encoding="utf-8"))
    cases = document["cases"]
    seen_queries = {g.normalized(case.get("query", "")) for case in cases}
    seen_ids = {case.get("id", "") for case in cases}

    dropped: collections.Counter[str] = collections.Counter()
    added: list[dict] = []

    for line in args.records.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            dropped["not JSON"] += 1
            continue

        query = str(record.get("query", "")).strip()
        if not query:
            dropped["no request"] += 1
            continue

        normal = g.normalized(query)
        if normal in seen_queries:
            dropped["already a case"] += 1
            continue

        expected = known_answer(record)
        if expected is None:
            dropped["no established answer"] += 1
            continue

        reason = resolvable(expected, tools_by_name, values)
        if reason is not None:
            dropped[reason] += 1
            continue

        word = g.console_tokens(expected)[0]
        language = language_of(record)
        index = 1
        while f"shared-{word}-{language}-{index}" in seen_ids:
            index += 1
        case_id = f"shared-{word}-{language}-{index}"

        seen_ids.add(case_id)
        seen_queries.add(normal)
        added.append({"id": case_id, "language": language, "category": word,
                      "query": query, "expected": expected, "source": "shared"})

        if args.limit and len(added) >= args.limit:
            break

    print(f"{len(added)} new case(s) from {args.records}")
    for case in added[:20]:
        print(f"  {case['id']:<28} {case['query'][:44]!r} -> {case['expected']}")
    if len(added) > 20:
        print(f"  ... and {len(added) - 20} more")

    if dropped:
        print("\ndropped:")
        for reason, count in dropped.most_common():
            print(f"  {count:>5}  {reason}")

    unknown = sum(count for reason, count in dropped.items() if reason.startswith("unknown command"))
    if unknown:
        print(f"\n{unknown} record(s) named a command the snapshot in data/ does not have. That is players on "
              "a newer game or with mods we have not indexed - re-run pull_schema.py against a live instance.")

    if not args.write:
        print("\nNothing written. Re-run with --write to append these to cases.json.")
        return

    document["cases"] = cases + added
    args.cases.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"\nwrote {len(document['cases'])} case(s) to {args.cases}")
    print("Re-run benchmark_human_cases.py before scoring, and rebuild the corpus before training: these "
          "phrasings are now excluded from it.")


if __name__ == "__main__":
    main()
