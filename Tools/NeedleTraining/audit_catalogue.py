"""Measure worst-case Needle route prompts across the complete live command catalogue."""

from __future__ import annotations

import argparse
import copy
import json
import statistics
from pathlib import Path
from typing import Any

from needle.model.finetune import render_example
from needle.model.tokenizer import get_tokenizer


def compact_route(tool: dict[str, Any], cap: int) -> dict[str, str]:
    description = str(tool.get("description", "")).strip().rstrip(".")
    if len(description) > cap:
        first = description.split(".", 1)[0].strip()
        description = first if 0 < len(first) <= cap else description[:cap].rstrip(" ,;:")
    return {"name": str(tool["name"]), "description": description}


def percentile(values: list[int], fraction: float) -> int:
    return sorted(values)[int((len(values) - 1) * fraction)]


def dummy_arguments(tool: dict[str, Any]) -> dict[str, Any]:
    arguments: dict[str, Any] = {}
    parameters = tool.get("parameters", {})
    required = set(parameters.get("required", []))
    for name, schema in parameters.get("properties", {}).items():
        if name not in required:
            continue
        if schema.get("enum"):
            arguments[name] = schema["enum"][0]
        elif schema.get("type") == "boolean":
            arguments[name] = True
        elif schema.get("type") in ("integer", "number"):
            arguments[name] = 10
        else:
            arguments[name] = "sample"
    return arguments


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("tools", type=Path)
    parser.add_argument("--caps", default="32,40,48,56,64,72,96")
    parser.add_argument(
        "--query",
        default="please choose and run the console command that best matches this request",
    )
    parser.add_argument("--cases", type=Path)
    args = parser.parse_args()

    tools = json.loads(args.tools.read_text(encoding="utf-8"))
    tokenizer = get_tokenizer()
    query = args.query
    if args.cases:
        benchmark = json.loads(args.cases.read_text(encoding="utf-8-sig"))
        query = max(
            (str(case["query"]) for case in benchmark["cases"]),
            key=lambda value: len(tokenizer.encode(value)),
        )
    results: dict[str, Any] = {}
    for cap in (int(value) for value in args.caps.split(",")):
        routes = [compact_route(tool, cap) for tool in tools]
        sizes = [
            len(tokenizer.encode(json.dumps(route, separators=(",", ":"))))
            for route in routes
        ]
        worst = sorted(
            routes,
            key=lambda route: len(tokenizer.encode(json.dumps(route, separators=(",", ":")))),
            reverse=True,
        )[:5]
        row = {
            "query": query,
            "tools": worst,
            "answers": [{"name": worst[0]["name"], "arguments": {}}],
            "reasoning": "",
        }
        prompt, target = render_example(row)
        results[str(cap)] = {
            "route_tokens": {
                "min": min(sizes),
                "median": statistics.median(sizes),
                "p95": percentile(sizes, 0.95),
                "max": max(sizes),
            },
            "worst_five_names": [route["name"] for route in worst],
            "prompt_tokens": len(tokenizer.encode(prompt)),
            "target_tokens": len(tokenizer.encode(target)),
            "total_tokens": len(tokenizer.encode(prompt)) + len(tokenizer.encode(target)) + 2,
        }
        full_totals: list[tuple[int, str]] = []
        for source in tools:
            full = copy.deepcopy(source)
            description = str(full.get("description", "")).strip().rstrip(".")
            if len(description) > cap:
                description = description[:cap].rstrip(" ,;:")
            full["description"] = description
            full_row = {
                "query": query,
                "tools": [full],
                "answers": [{"name": full["name"], "arguments": dummy_arguments(full)}],
                "reasoning": "",
            }
            full_prompt, full_target = render_example(full_row)
            total = len(tokenizer.encode(full_prompt)) + len(tokenizer.encode(full_target)) + 2
            full_totals.append((total, full["name"]))
        full_totals.sort(reverse=True)
        results[str(cap)]["full_refinement"] = {
            "p95_tokens": percentile([item[0] for item in full_totals], 0.95),
            "max_tokens": full_totals[0][0],
            "max_tool": full_totals[0][1],
        }

    print(json.dumps({
        "tool_count": len(tools),
        "query": query,
        "query_tokens": len(tokenizer.encode(query)),
        "caps": results,
    }, indent=2))


if __name__ == "__main__":
    main()
