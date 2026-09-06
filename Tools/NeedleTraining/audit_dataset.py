"""Audit generated Needle JSONL splits and emit report-friendly JSON."""

from __future__ import annotations

import argparse
import json
import statistics
from collections import Counter
from pathlib import Path
from typing import Any

from needle.model.finetune import render_example
from needle.model.tokenizer import get_tokenizer


def normalized(value: str) -> str:
    return " ".join(value.casefold().split())


def percentile(values: list[int], fraction: float) -> int:
    return sorted(values)[int((len(values) - 1) * fraction)]


def distribution(values: list[int]) -> dict[str, int | float]:
    return {
        "min": min(values),
        "median": statistics.median(values),
        "p95": percentile(values, 0.95),
        "max": max(values),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--samples", type=int, default=24)
    args = parser.parse_args()

    tokenizer = get_tokenizer()
    splits: dict[str, list[dict[str, Any]]] = {}
    for name in ("train", "validation", "holdout"):
        path = args.root / f"{name}.jsonl"
        splits[name] = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()
                        if line.strip()]

    query_sets = {name: {normalized(row["query"]) for row in rows}
                  for name, rows in splits.items()}
    overlaps = {}
    for left, right in (("train", "validation"), ("train", "holdout"),
                        ("validation", "holdout")):
        overlaps[f"{left}-{right}"] = len(query_sets[left] & query_sets[right])

    report: dict[str, Any] = {"root": str(args.root), "overlaps": overlaps, "splits": {}}
    for name, rows in splits.items():
        token_counts = []
        for row in rows:
            prompt, target = render_example(row)
            token_counts.append(len(tokenizer.encode(prompt)) + len(tokenizer.encode(target)) + 2)
        report["splits"][name] = {
            "rows": len(rows),
            "phases": dict(Counter(row["phase"] for row in rows)),
            "languages": dict(Counter(row["language"] for row in rows)),
            "commands": len({row["command"] for row in rows if row["command"]}),
            "query_words": distribution([len(row["query"].split()) for row in rows]),
            "rendered_tokens": distribution(token_counts),
        }

    holdout = [row for row in splits["holdout"] if row["phase"] == "refine"]
    holdout.sort(key=lambda row: (row["command"], row["language"]))
    report["holdout_samples"] = [{
        "language": row["language"],
        "query": row["query"],
        "answer": row["answers"][0],
    } for row in holdout[:args.samples]]
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
