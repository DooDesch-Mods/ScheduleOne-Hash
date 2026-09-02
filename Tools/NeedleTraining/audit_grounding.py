"""Report command arguments that lack live/schema values for supervised grounding."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("tools", type=Path)
    parser.add_argument("values", type=Path)
    args = parser.parse_args()

    tools = json.loads(args.tools.read_text(encoding="utf-8"))
    values = json.loads(args.values.read_text(encoding="utf-8"))
    report = []
    for tool in tools:
        slots = values.get(tool["name"], [])
        unbacked = []
        for index, (name, schema) in enumerate(tool["parameters"].get("properties", {}).items()):
            live = slots[index] if index < len(slots) else []
            if schema.get("type") == "string" and not schema.get("enum") and not live:
                unbacked.append(name)
        if unbacked:
            report.append({
                "name": tool["name"],
                "unbacked": unbacked,
                "quoted": re.findall(r"'([^']+)'", tool.get("description", "")),
                "description": tool.get("description", ""),
            })
    print(json.dumps({
        "unbacked_tools": len(report),
        "with_quoted_literals": sum(bool(item["quoted"]) for item in report),
        "tools": report,
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
