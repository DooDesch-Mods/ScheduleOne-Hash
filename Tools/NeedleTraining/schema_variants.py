"""Mutate the frozen full-catalogue dataset one schema field at a time.

The base model, given every command with a full schema and a query-ranked enum, routes 43 of 79 cases and
gets 24 exactly right. Nineteen correctly routed cases still fail on their arguments, and reading the
report the failures fall into three families that are all about what we declare rather than what the model
can do:

  five   the quantity is simply missing - we declare it as "Optionally specify a quantity", call it arg2,
         describe it in one word and leave it out of `required`
  ~ten   settime gets the wrong number - we say "24-hour time" and "hhmm" but never pass the game's own
         `settime 1530`, the one thing that shows what a valid value looks like
  four   the granddaddy cases, where the ranked enum does not contain the right value at all

Each variant below changes exactly ONE of those things, against the frozen rendering, so a difference has
one candidate cause. Nothing here consults the query or the expected answer: a schema that leaks the answer
would prove nothing.

    python schema_variants.py --out data-production/variants
"""

from __future__ import annotations

import argparse
import copy
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent

# The one command in the catalogue with an optional numeric parameter, which is what the quantity variants
# are about. Chosen from the baseline schema, without looking at any query or answer.
QUANTITY_COMMAND = "give"

# Generic and mechanical: the same two sentences appended to any optional numeric parameter. It says to keep
# a value the player gave, and not to invent one - it names no item, no number and no command.
EXTRACTION_HINT = (" Include the value specified in the user request. Omit this argument only when the "
                   "request does not specify a value for it.")


def tool_of(row: dict, name: str) -> dict | None:
    return next((tool for tool in row["tools"] if tool["name"] == name), None)


def variant_names(rows: list[dict]) -> list[dict]:
    """N - positional keys become the labels the signature table already carries."""
    out = copy.deepcopy(rows)
    for row in out:
        tool = tool_of(row, QUANTITY_COMMAND)
        if tool is None:
            continue
        properties = tool["parameters"]["properties"]
        renamed = {}
        for key, value in properties.items():
            renamed["item" if key == "arg1" else "quantity" if key == "arg2" else key] = value
        tool["parameters"]["properties"] = renamed
        tool["parameters"]["required"] = ["item" if key == "arg1" else key
                                          for key in tool["parameters"].get("required", [])]

        # The expected call is addressed by the same keys, or the scorer would count every correct answer
        # as wrong. It is the same call: only the names the schema uses for its slots changed.
        for answer in row.get("answers", []):
            if answer.get("name") != QUANTITY_COMMAND:
                continue
            answer["arguments"] = {
                ("item" if key == "arg1" else "quantity" if key == "arg2" else key): value
                for key, value in (answer.get("arguments") or {}).items()
            }
    return out


def variant_description(rows: list[dict]) -> list[dict]:
    """D - the one-word parameter description gains a generic extraction instruction."""
    out = copy.deepcopy(rows)
    for row in out:
        tool = tool_of(row, QUANTITY_COMMAND)
        if tool is None:
            continue
        prop = tool["parameters"]["properties"].get("arg2")
        if prop is not None:
            prop["description"] = str(prop.get("description", "")).rstrip(". ") + "." + EXTRACTION_HINT
    return out


def variant_optional(rows: list[dict]) -> list[dict]:
    """O - the word "Optionally" leaves the tool description, the clause stays."""
    out = copy.deepcopy(rows)
    for row in out:
        tool = tool_of(row, QUANTITY_COMMAND)
        if tool is None:
            continue
        text = str(tool.get("description", ""))
        tool["description"] = text.replace("Optionally specify", "Specify").replace("optionally specify",
                                                                                    "specify")
    return out


def variant_required(rows: list[dict]) -> list[dict]:
    """R - diagnostic only. It misrepresents [quantity] as mandatory and is not a shipping fix."""
    out = copy.deepcopy(rows)
    for row in out:
        tool = tool_of(row, QUANTITY_COMMAND)
        if tool is None:
            continue
        properties = list(tool["parameters"]["properties"])
        tool["parameters"]["required"] = properties
    return out


def variant_example(rows: list[dict], usage: dict[str, str]) -> list[dict]:
    """E - every command's own registered usage line is appended to its description.

    Separate from the four quantity variants and aimed at a different failure: `settime` is told "24-hour
    time" and "hhmm" and still answers noon with 0, while the console has been registering `settime 1530`
    all along and we never pass it on. Mechanical for every command, and it names no answer - `settime 1530`
    does not say what noon is.
    """
    out = copy.deepcopy(rows)
    for row in out:
        for tool in row["tools"]:
            example = usage.get(tool["name"], "")
            if not example:
                continue
            text = str(tool.get("description", "")).rstrip(". ")
            tool["description"] = f"{text}. For example: {example}"
    return out


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", type=pathlib.Path,
                        default=ROOT / "data-production" / "full-catalogue.jsonl")
    parser.add_argument("--commands", type=pathlib.Path, default=ROOT / "data" / "commands.json")
    parser.add_argument("--out", type=pathlib.Path, default=ROOT / "data-production" / "variants")
    args = parser.parse_args()

    rows = [json.loads(line) for line in args.dataset.read_text(encoding="utf-8").split("\n") if line.strip()]
    usage = {entry["name"]: str(entry.get("usage", "")).strip()
             for entry in json.loads(args.commands.read_text(encoding="utf-8"))}

    args.out.mkdir(parents=True, exist_ok=True)
    built = {
        "N-names": variant_names(rows),
        "D-description": variant_description(rows),
        "O-optional": variant_optional(rows),
        "R-required": variant_required(rows),
        "E-example": variant_example(rows, usage),
    }

    for name, variant in built.items():
        path = args.out / f"{name}.jsonl"
        path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in variant),
                        encoding="utf-8")

        changed = sum(1 for a, b in zip(rows, variant) if a != b)
        print(f"{name:<16} {path}  ({changed}/{len(rows)} rows differ from the baseline)")

    sample = tool_of(built["D-description"][0], QUANTITY_COMMAND)
    print("\nD sample:", json.dumps(sample["parameters"]["properties"]["arg2"], ensure_ascii=False))
    print("O sample:", json.dumps(tool_of(built["O-optional"][0], QUANTITY_COMMAND)["description"],
                                  ensure_ascii=False))
    settime = tool_of(built["E-example"][0], "settime")
    if settime:
        print("E sample:", json.dumps(settime["description"], ensure_ascii=False))


if __name__ == "__main__":
    main()
