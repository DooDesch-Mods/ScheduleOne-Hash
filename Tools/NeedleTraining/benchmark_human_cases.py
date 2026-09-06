"""Turn the hand-written NeedleBenchmark cases into a scorable dataset.

The pipeline's own gate scores data-production/holdout.jsonl, which the same teacher wrote that produced
the training rows. Passing it says the adapter learned the teacher's phrasing distribution - not that it
understands what a player types. NeedleBenchmark/cases.json is the only human-written set, generate_data
deliberately keeps its query strings out of the corpus (expected_assignments -> excluded_queries), and
NeedleSmoke's benchmark mode runs entirely offline. So the honest number is measurable here, without the
game, by rendering those cases in the row format NeedleSmoke reads.

    python benchmark_human_cases.py --out data-production/human-cases.jsonl
    NeedleSmoke.exe benchmark <engine> <weights> data-production/human-cases.jsonl results/human.json
"""
import argparse
import json
import pathlib
import random

import generate_data as g

ROOT = pathlib.Path(__file__).resolve().parent


def case_call(case: dict, tools_by_name: dict) -> tuple[str, dict] | None:
    """The command and arguments a case expects, or None when it must not call anything.

    Same mapping as expected_assignments: positional console tokens fill the tool's properties in order,
    and a case may stop early - 'give ogkush' fills only the first.
    """
    tokens = g.console_tokens(case.get("expected", ""))
    if not tokens or tokens[0] not in tools_by_name:
        return None
    schema = tools_by_name[tokens[0]]["parameters"].get("properties", {})
    properties = list(schema)
    values = tokens[1:]
    arguments = {}
    for index, prop in enumerate(properties):
        if index >= len(values):
            break
        raw = " ".join(values[index:]) if index == len(properties) - 1 else values[index]
        # A numeric slot holds a number, the way the corpus and the engine both carry it. Comparing the
        # model's 10 against a string "10" counted five correct answers as failures.
        if schema[prop].get("type") in ("number", "integer"):
            try:
                arguments[prop] = float(raw) if "." in raw else int(raw)
                continue
            except ValueError:
                pass
        arguments[prop] = raw
    return tokens[0], arguments


def self_check() -> None:
    """The console-token mapping is the whole correctness of this file; everything else is plumbing."""
    tools_by_name = {"give": {"parameters": {"properties": {"item": {}, "quantity": {}}}},
                     "teleport": {"parameters": {"properties": {"destination": {}}}}}
    assert case_call({"expected": "give ogkush 10"}, tools_by_name) == ("give", {"item": "ogkush", "quantity": "10"})
    # A case may stop early: "give me some ogkush" expects the item and deliberately no quantity.
    assert case_call({"expected": "give ogkush"}, tools_by_name) == ("give", {"item": "ogkush"})
    # The last property soaks up the remaining tokens, so a multi-word destination survives.
    assert case_call({"expected": "teleport the docks"}, tools_by_name) == ("teleport", {"destination": "the docks"})
    # Off-topic and ambiguous cases must call nothing, and an untrained command is not a negative.
    assert case_call({"expected": ""}, tools_by_name) is None
    assert case_call({}, tools_by_name) is None
    assert case_call({"expected": "breedtoseed 3"}, tools_by_name) is None
    print("case mapping ok")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=pathlib.Path, default=ROOT / "data" / "tools.json")
    parser.add_argument("--values", type=pathlib.Path, default=ROOT / "data" / "values.json")
    parser.add_argument("--commands", type=pathlib.Path, default=ROOT / "data" / "commands.json")
    parser.add_argument("--cases", type=pathlib.Path,
                        default=ROOT.parent / "NeedleBenchmark" / "cases.json")
    parser.add_argument("--out", type=pathlib.Path, default=ROOT / "data-production" / "human-cases.jsonl")
    parser.add_argument("--seed", type=int, default=20260821)
    parser.add_argument("--include-external-dev-sources", action="store_true")
    parser.add_argument("--self-check", action="store_true", help="run the mapping assertions and exit")
    args = parser.parse_args()
    if args.self_check:
        self_check()
        return

    tools = json.loads(args.tools.read_text(encoding="utf-8"))
    values = json.loads(args.values.read_text(encoding="utf-8"))
    metadata = {entry["name"]: entry for entry in json.loads(args.commands.read_text(encoding="utf-8"))}
    # Mirror the pipeline's default so the adapter is asked only about commands it was trained on.
    if not args.include_external_dev_sources:
        tools = [tool for tool in tools
                 if not ("-dev" in str(metadata.get(tool["name"], {}).get("source", "")).casefold()
                         and not str(metadata.get(tool["name"], {}).get("source", ""))
                         .casefold().startswith("hash"))]
    tools_by_name = {tool["name"]: tool for tool in tools}
    tokenizer = g.get_tokenizer()
    rng = random.Random(args.seed)

    context_by_command = {}
    for tool in tools:
        context = [g.route_tool(tool)] + [g.route_tool(near) for near in g.nearest_tools(tool, tools)]
        rng.shuffle(context)
        context_by_command[tool["name"]] = context

    cases = json.loads(args.cases.read_text(encoding="utf-8"))["cases"]
    rows, untrained = [], []
    for case in cases:
        identifier = case["id"]
        language = case.get("language", "en")
        common = {"language": language, "split": "human", "query": case["query"], "reasoning": "",
                  "system": g.system_facts(language, len(rows))}
        call = case_call(case, tools_by_name)
        if call is None:
            if g.console_tokens(case.get("expected", "")):
                # Expects a command this build does not train on; scoring it would measure the catalogue,
                # not the adapter.
                untrained.append((identifier, case["expected"]))
                continue
            # Off-topic and ambiguous cases. An ambiguous case is only a real test when the command it
            # is fishing for is actually on the table: offering "give me some seeds" five random tools
            # that exclude give lets the adapter pass by having no way to fail. So when a query token
            # names a command, that command's own route context is used, exactly as for a positive.
            probe = {token.strip(".,!?;:") for token in case["query"].casefold().split()
                     if len(token.strip(".,!?;:")) >= 3}
            baited = next((tool["name"] for tool in tools
                           if any(token in tool["name"] for token in probe)), None)
            context = (list(context_by_command[baited]) if baited
                       else [g.route_tool(tool) for tool in rng.sample(tools, min(5, len(tools)))])
            rows.append(g.fit_route_budget({**common, "id": identifier + "|route", "command": "",
                                            "phase": "route", "answers": [], "tools": context},
                                           tokenizer))
            continue
        command, arguments = call
        rows.append(g.fit_route_budget({**common, "id": identifier + "|route", "command": command,
                                        "phase": "route",
                                        "tools": list(context_by_command[command]),
                                        "answers": [{"name": command, "arguments": {}}]}, tokenizer))
        refine = {**common, "id": identifier + "|refine", "command": command, "phase": "refine",
                  "tools": [g.refinement_tool(tools_by_name[command], arguments, values, case["query"])],
                  "reasoning": g.grounding_reasoning(tools_by_name[command], arguments),
                  "answers": [{"name": command, "arguments": arguments}]}
        if g.rendered_tokens(refine, tokenizer) > g.TOKEN_BUDGET:
            refine["reasoning"] = ""
        rows.append(refine)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    g.write_jsonl(args.out, rows)
    phases = {phase: sum(1 for row in rows if row["phase"] == phase) for phase in ("route", "refine")}
    print(f"{len(cases)} cases -> {len(rows)} rows ({phases['route']} route, {phases['refine']} refine), "
          f"{sum(1 for row in rows if not row['command'])} negatives -> {args.out}")
    for identifier, expected in untrained:
        print(f"  skipped {identifier}: expects untrained command {expected!r}")


if __name__ == "__main__":
    main()
