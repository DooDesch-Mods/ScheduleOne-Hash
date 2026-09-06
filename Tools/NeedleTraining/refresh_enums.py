"""Rewrite the refinement tools in a finished corpus so their enum is the one the runtime would send.

The enum depends only on the row's query and the value catalogue, so it can be recomputed in place -
regenerating the whole corpus would need the teacher online for any cache miss, and none of the teacher's
work changes here. Route rows carry no enum and are left untouched.

    python refresh_enums.py
"""
import json
import pathlib

import generate_data as g

ROOT = pathlib.Path(__file__).resolve().parent


def main() -> None:
    tools_by_name = {tool["name"]: tool
                     for tool in json.loads((ROOT / "data" / "tools.json").read_text(encoding="utf-8"))}
    values = json.loads((ROOT / "data" / "values.json").read_text(encoding="utf-8"))
    tokenizer = g.get_tokenizer()

    for split in ("train", "validation", "holdout"):
        path = ROOT / "data-production" / f"{split}.jsonl"
        rows = [json.loads(line) for line in path.open(encoding="utf-8")]
        changed = trimmed = dropped = 0
        kept = []
        for row in rows:
            if row["phase"] == "refine" and row["command"] in tools_by_name:
                before = json.dumps(row["tools"], sort_keys=True)
                row["tools"] = [g.refinement_tool(tools_by_name[row["command"]], {}, values, row["query"])]
                if json.dumps(row["tools"], sort_keys=True) != before:
                    changed += 1
                # The same budget rule generate_data applies: a derivation is worth less than the call.
                if g.rendered_tokens(row, tokenizer) > g.TOKEN_BUDGET and row.get("reasoning"):
                    row["reasoning"] = ""
                    trimmed += 1
                if g.rendered_tokens(row, tokenizer) > g.TOKEN_BUDGET:
                    dropped += 1
                    continue
            kept.append(row)
        g.write_jsonl(path, kept)
        print(f"{split}: {changed} refinement tools rewritten, {trimmed} lost their derivation, "
              f"{dropped} rows dropped over budget -> {len(kept)} rows")


if __name__ == "__main__":
    main()
