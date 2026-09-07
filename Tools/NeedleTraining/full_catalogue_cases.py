"""Render the hand-written cases the way the engine's own documentation describes: one call, whole catalogue.

`RESULTS.md` reports 78.5 % routing for the mod's route-then-refine split. A review of the Needle package
established that the split is our design and not the engine's - it documents a single call over every
declared tool, retrieving the few it needs itself. The obvious question is whether the adapter is teaching
the model the task or teaching it to cope with a routing declaration we made too thin.

That question needs a control this repository did not have. `benchmark_human_cases.py` hands the refinement
pass the command the case expects, so native retrieval never has to find anything. This renders each case
once, with every eligible command declared in full - real types, required fields, and the enum ranked
against the player's own words, exactly what the runtime sends for one command but for all of them.

    python full_catalogue_cases.py --out data-production/full-catalogue.jsonl
    NeedleSmoke.exe benchmark <engine> <weights> data-production/full-catalogue.jsonl <report.json>
    python score_full_catalogue.py <report.json>

It is a control, not a replacement benchmark: the mod does not call the engine this way, and a bad number
here is an argument about the engine's retrieval, not about what players get.
"""

from __future__ import annotations

import argparse
import json
import pathlib

import generate_data as g

ROOT = pathlib.Path(__file__).resolve().parent


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=pathlib.Path, default=ROOT / "data" / "tools.json")
    parser.add_argument("--values", type=pathlib.Path, default=ROOT / "data" / "values.json")
    parser.add_argument("--commands", type=pathlib.Path, default=ROOT / "data" / "commands.json")
    parser.add_argument("--cases", type=pathlib.Path,
                        default=ROOT.parent / "NeedleBenchmark" / "cases.json")
    parser.add_argument("--out", type=pathlib.Path,
                        default=ROOT / "data-production" / "full-catalogue.jsonl")
    parser.add_argument("--include-external-dev-sources", action="store_true")
    args = parser.parse_args()

    tools = json.loads(args.tools.read_text(encoding="utf-8"))
    values = json.loads(args.values.read_text(encoding="utf-8"))
    metadata = {entry["name"]: entry
                for entry in json.loads(args.commands.read_text(encoding="utf-8"))}

    if not args.include_external_dev_sources:
        # The same filter the pipeline trains under, so the two numbers are about the same catalogue.
        tools = [tool for tool in tools
                 if not ("-dev" in str(metadata.get(tool["name"], {}).get("source", "")).casefold()
                         and not str(metadata.get(tool["name"], {}).get("source", ""))
                         .casefold().startswith("hash"))]
    tools_by_name = {tool["name"]: tool for tool in tools}

    cases = json.loads(args.cases.read_text(encoding="utf-8"))["cases"]
    rows, skipped = [], []

    unscorable = [case for case in cases if case.get("unscorable")]
    cases = [case for case in cases if not case.get("unscorable")]

    for index, case in enumerate(cases):
        expected = case.get("expected", "")
        tokens = g.console_tokens(expected)

        if tokens and tokens[0] not in tools_by_name:
            # A command this catalogue does not carry would measure the snapshot, not the model.
            skipped.append((case["id"], expected))
            continue

        language = case.get("language", "en")
        # Every command, each with the schema the runtime would send for THIS query. That is the whole
        # point: the engine is being given everything it needs to retrieve and extract in one call.
        declared = [g.refinement_tool(tool, {}, values, case["query"]) for tool in tools]

        answers = []
        if tokens:
            command = tools_by_name[tokens[0]]
            properties = list(command["parameters"].get("properties", {}))
            arguments = {}
            for position, name in enumerate(properties):
                if position >= len(tokens) - 1:
                    break
                raw = (" ".join(tokens[position + 1:]) if position == len(properties) - 1
                       else tokens[position + 1])
                kind = command["parameters"]["properties"][name].get("type", "string")
                if kind in ("number", "integer"):
                    try:
                        raw = int(raw) if kind == "integer" or float(raw).is_integer() else float(raw)
                    except ValueError:
                        pass
                arguments[name] = raw
            answers = [{"name": tokens[0], "arguments": arguments}]

        rows.append({
            "id": case["id"], "command": tokens[0] if tokens else "", "language": language,
            "split": "full-catalogue", "phase": "single", "query": case["query"], "reasoning": "",
            "system": g.system_facts(language, index), "tools": declared, "answers": answers,
        })

    args.out.parent.mkdir(parents=True, exist_ok=True)
    g.write_jsonl(args.out, rows)

    positives = sum(1 for row in rows if row["answers"])
    print(f"{len(rows)} row(s) -> {args.out}")
    print(f"  {positives} with an expected call, {len(rows) - positives} off-topic, "
          f"{len(tools)} tools declared in every one")
    if unscorable:
        # Printed every run rather than deleted, because a case removed from the denominator is not a case
        # fixed. These demand a console line the game would refuse whatever the model answers.
        print(f"  {len(unscorable)} case(s) not scored - the expected answer is not a line the game takes:")
        for case in unscorable:
            print(f"    {case['id']}: {case['expected']} - {case['unscorable']}")
    if skipped:
        print(f"  {len(skipped)} skipped - the catalogue snapshot has no such command:")
        for identifier, expected in skipped:
            print(f"    {identifier}: {expected}")


if __name__ == "__main__":
    main()
