"""Score a benchmark report the way the mod sees it, not the way the raw model answers.

NeedleSmoke compares the model's arguments literally against the canonical console token. The mod does
not: NeedleArgument.TryResolveText matches what the model produced against the command's value catalogue
with the same normalisation and fuzzy rules that rank the enum, so "og kush", a near-miss or a differently
cased token still resolves. Scoring the raw output therefore understates what a player actually gets.

    python score_as_mod.py results/num-run3ep1.json [...]
"""
import collections
import json
import pathlib
import sys

import generate_data as g
import time_words

ROOT = pathlib.Path(__file__).resolve().parent
RESULTS = ROOT.parent / "NeedleBenchmark" / "results"
TOOLS = {tool["name"]: tool
         for tool in json.loads((ROOT / "data" / "tools.json").read_text(encoding="utf-8"))}
VALUES = json.loads((ROOT / "data" / "values.json").read_text(encoding="utf-8"))


def resolve(command: str, index: int, produced) -> str:
    """What TryResolveText would make of this value: the catalogue entry it matches, or the text itself."""
    if produced is None:
        return None
    text = str(produced)
    properties = list(TOOLS.get(command, {}).get("parameters", {}).get("properties", {}).values())
    if index < len(properties) and time_words.owns(properties[index].get("description", "")):
        # The mod converts a word or a bare hour to the console's own reading, so scoring the raw answer
        # counts two right answers wrong: "noon", and the 8 the model correctly reads out of "8am".
        return time_words.token(text) or text
    slots = VALUES.get(command, [])
    if index >= len(slots) or not slots[index]:
        return text
    best, score = text, 0
    for value in slots[index]:
        candidate = g.candidate_score(value, g.normal(text))
        if candidate > score:
            best, score = value, candidate
    return best if score > 0 else text


def matches(command: str, expected: dict, produced) -> bool:
    if produced is None or set(produced) != set(expected):
        return False
    properties = list(TOOLS[command]["parameters"].get("properties", {}))
    for name, want in expected.items():
        got = produced.get(name)
        if str(got) == str(want):
            continue
        try:
            if float(got) == float(want):
                continue
        except (TypeError, ValueError):
            pass
        index = properties.index(name) if name in properties else 0
        if resolve(command, index, got) == str(want):
            continue
        return False
    return True


def main() -> None:
    for name in sys.argv[1:]:
        report = json.loads((RESULTS / name).read_text(encoding="utf-8"))
        by = collections.defaultdict(dict)
        for row in report["results"]:
            case, phase = row["Id"].rsplit("|", 1)
            expected = row["Expected"][0]["arguments"] if row["Expected"] else None
            produced = row["Actual"][0]["arguments"] if row["Actual"] else None
            if not row["Expected"]:
                ok = not row["Actual"]
            elif phase == "route":
                ok = bool(row["Actual"]) and row["Actual"][0]["name"] == row["Expected"][0]["name"]
            else:
                ok = bool(row["Actual"]) and row["Actual"][0]["name"] == row["Expected"][0]["name"] \
                     and matches(row["Expected"][0]["name"], expected, produced)
            by[case][phase] = ok
        end = sum(1 for phases in by.values() if all(phases.values()))
        refine = [ok for case, phases in by.items() for phase, ok in phases.items() if phase == "refine"]
        print(f"{name:22} refine {sum(refine)}/{len(refine)}   end-to-end {end}/{len(by)}")


if __name__ == "__main__":
    main()
