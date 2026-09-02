"""Audit rendered Needle fine-tuning examples against the inference token budget."""

from __future__ import annotations

import argparse
import copy
import json
import statistics
from collections import defaultdict
from pathlib import Path
from typing import Any

from needle.model.finetune import render_example
from needle.model.tokenizer import get_tokenizer


def tool_name(tool: dict[str, Any]) -> str:
    return str(tool.get("name") or tool.get("function", {}).get("name") or "")


def percentile(values: list[int], fraction: float) -> int:
    return sorted(values)[int((len(values) - 1) * fraction)]


def describe(values: list[int]) -> dict[str, int | float]:
    return {
        "min": min(values),
        "median": statistics.median(values),
        "p95": percentile(values, 0.95),
        "max": max(values),
    }


def load_rows(root: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for path in sorted(root.glob("*.jsonl")):
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                rows.append(json.loads(line))
    return rows


def compact_tool(tool: dict[str, Any], route_only: bool = False) -> dict[str, Any]:
    compact = copy.deepcopy(tool)
    description = str(compact.get("description", ""))
    description = description.split(" Console command:", 1)[0].strip().rstrip(".")
    if route_only and len(description) > 48:
        sentence = description.split(".", 1)[0].strip()
        description = sentence if 0 < len(sentence) <= 48 else description[:48].rstrip(" ,;:")
    compact["description"] = description
    parameters = compact.get("parameters", {})
    properties = parameters.get("properties", {})
    if route_only:
        compact.pop("parameters", None)
        return compact

    for schema in properties.values():
        description = str(schema.get("description", ""))
        marker = "; #=current" if "use # when" in description else ""
        schema["description"] = description.split(", the ", 1)[0].split("; use # when", 1)[0] + marker
    return compact


def encoded(tokenizer: Any, row: dict[str, Any]) -> tuple[int, int, int]:
    prompt, target = render_example(row)
    prompt_tokens = len(tokenizer.encode(prompt))
    target_tokens = len(tokenizer.encode(target))
    return prompt_tokens, target_tokens, prompt_tokens + target_tokens + 2


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--budget", type=int, default=256)
    args = parser.parse_args()

    tokenizer = get_tokenizer()
    rows = load_rows(args.root)
    by_schema_count: dict[int, list[tuple[int, int, int]]] = defaultdict(list)
    reduced: dict[int, list[tuple[int, int, int]]] = defaultdict(list)
    compact_groups: dict[int, list[tuple[int, int, int]]] = defaultdict(list)
    compact_without_reasoning: dict[int, list[tuple[int, int, int]]] = defaultdict(list)
    compact_routes: dict[int, list[tuple[int, int, int]]] = defaultdict(list)
    compact_routes_five: dict[int, list[tuple[int, int, int]]] = defaultdict(list)
    compact_reduced: dict[int, list[tuple[int, int, int]]] = defaultdict(list)

    for row in rows:
        tool_count = len(row.get("tools", []))
        by_schema_count[tool_count].append(encoded(tokenizer, row))

        compact = dict(row)
        compact["tools"] = [compact_tool(tool) for tool in row.get("tools", [])]
        compact_groups[tool_count].append(encoded(tokenizer, compact))
        no_reasoning = dict(compact)
        no_reasoning["reasoning"] = ""
        compact_without_reasoning[tool_count].append(encoded(tokenizer, no_reasoning))
        route = dict(no_reasoning)
        route["tools"] = [compact_tool(tool, route_only=True) for tool in row.get("tools", [])]
        compact_routes[tool_count].append(encoded(tokenizer, route))
        if row.get("tools"):
            existing = {tool_name(tool) for tool in row["tools"]}
            extra = next((tool for tool in rows[0].get("tools", []) if tool_name(tool) not in existing), None)
            if extra is None:
                extra = row["tools"][0]
            route_five = dict(route)
            route_five["tools"] = route["tools"] + [compact_tool(extra, route_only=True)]
            compact_routes_five[len(route_five["tools"])].append(encoded(tokenizer, route_five))

        answers = row.get("answers", [])
        if not answers:
            continue
        gold = str(answers[0].get("name", ""))
        tools = row.get("tools", [])
        selected = [tool for tool in tools if tool_name(tool) == gold]
        negatives = [tool for tool in tools if tool_name(tool) != gold]
        if not selected:
            continue
        for negative_count in range(len(negatives) + 1):
            candidate = dict(row)
            candidate["tools"] = selected + negatives[:negative_count]
            reduced[negative_count + 1].append(encoded(tokenizer, candidate))
            candidate["tools"] = [compact_tool(tool) for tool in candidate["tools"]]
            candidate["reasoning"] = ""
            compact_reduced[negative_count + 1].append(encoded(tokenizer, candidate))

    def summarize(groups: dict[int, list[tuple[int, int, int]]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for count, values in sorted(groups.items()):
            totals = [value[2] for value in values]
            result[str(count)] = {
                "rows": len(values),
                "prompt": describe([value[0] for value in values]),
                "target": describe([value[1] for value in values]),
                "total": describe(totals),
                "fits_budget": sum(value <= args.budget for value in totals),
            }
        return result

    print(json.dumps({
        "root": str(args.root),
        "budget": args.budget,
        "rows": len(rows),
        "as_generated": summarize(by_schema_count),
        "gold_plus_negatives": summarize(reduced),
        "compact_as_generated": summarize(compact_groups),
        "compact_without_reasoning": summarize(compact_without_reasoning),
        "compact_route_without_reasoning": summarize(compact_routes),
        "compact_route_five_without_reasoning": summarize(compact_routes_five),
        "compact_gold_plus_negatives_without_reasoning": summarize(compact_reduced),
    }, indent=2))


if __name__ == "__main__":
    main()
