"""Add the spoken argument forms to an existing training split.

generate_data grows these rows too, but only while rebuilding the whole corpus, which needs the teacher
online for any cache miss. The variants are derived from rows the teacher already wrote, so they can be
appended to a finished train.jsonl instead - the evaluation splits are deliberately left alone so the
holdout and the hand-written benchmark stay independent measures of whether this helped.

    python augment_train.py [--seed 20260821]
"""
import argparse
import json
import pathlib
import random

import generate_data as g

ROOT = pathlib.Path(__file__).resolve().parent


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--train", type=pathlib.Path, default=ROOT / "data-production" / "train.jsonl")
    parser.add_argument("--seed", type=int, default=20260821)
    args = parser.parse_args()

    rows = [json.loads(line) for line in args.train.open(encoding="utf-8")]
    before = len(rows)
    rows = [row for row in rows if "|spoken" not in row["id"]]
    if len(rows) != before:
        print(f"dropped {before - len(rows)} variants from an earlier pass")

    rng = random.Random(args.seed)
    tokenizer = g.get_tokenizer()
    seen = {g.normalized(row["query"]) for row in rows}
    added = []
    for row in rows:
        if row["phase"] != "refine" or not row["answers"] or not row["answers"][0]["arguments"]:
            continue
        variants = g.spoken_variants(row, rng)
        if not variants:
            continue
        # Both forms, not one at random: a row like "give me 10 ogkush" carries a number and a value, and
        # the benchmark needs both mappings. Picking one left the spacing form on 92 rows against 798.
        for index, variant in enumerate(variants):
            spoken = {**row, "id": f"{row['id']}|spoken{index}", "query": variant}
            # A collision would be dropped by validate_dataset anyway; skipping keeps the count honest.
            if g.normalized(variant) in seen:
                continue
            if g.rendered_tokens(spoken, tokenizer) > g.TOKEN_BUDGET:
                continue
            seen.add(g.normalized(variant))
            added.append(spoken)

    rng.shuffle(rows := rows + added)
    g.write_jsonl(args.train, rows)
    spelled = sum(1 for row in added if row["id"].endswith("|spoken0")
                  and not any(c.isdigit() for c in row["query"]))
    spaced = len(added) - spelled
    print(f"{len(added)} spoken variants added -> {len(rows)} training rows")
    print(f"  spelled-out numbers: {spelled}   values written with a space: {spaced}")
    for row in added[:5]:
        print(f"  {row['query']!r} -> {json.dumps(row['answers'][0]['arguments'], ensure_ascii=False)}")


if __name__ == "__main__":
    main()
