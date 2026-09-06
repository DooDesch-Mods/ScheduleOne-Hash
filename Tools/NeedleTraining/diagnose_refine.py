"""Does the trained adapter fill arguments before quantisation?

The native benchmark scores hash-production.cact, the 4-bit export. Route survives it at 84% while
refine collapses to 3% on rows that need arguments, and 91% of those answers are the empty object that
a route answer uses. Two very different causes produce that, and they need opposite fixes: either the
LoRA never learned argument grounding, or the export lost it. This runs the unquantised LoRA in JAX on
the same holdout rows, so the answer is a measurement rather than a guess.

    python diagnose_refine.py [--limit 40]
"""
import argparse
import json
import pathlib
import pickle
import re

from needle.model.architecture import SimpleAttentionNetwork
from needle.model.finetune import merge_lora, render_example, get_tokenizer
from needle.model.run import generate, load_checkpoint

ROOT = pathlib.Path(__file__).resolve().parent


def tool_call_arguments(raw: str):
    """The JAX model emits Needle's own <think>/<tool_call> text; the engine is what turns it into the
    {"function_calls": ...} JSON the native benchmark reads. Parsing it as that JSON returns nothing at
    all, which looks exactly like a model that answers nothing."""
    match = re.search(r"<tool_call>(.*?)</tool_call>", raw, re.DOTALL)
    if not match:
        return None
    try:
        calls = json.loads(match.group(1))
    except json.JSONDecodeError:
        return None
    return calls[0].get("arguments") if calls else None


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=pathlib.Path, default=ROOT / "artifacts" / "hash-production.pkl")
    parser.add_argument("--holdout", type=pathlib.Path, default=ROOT / "data-production" / "holdout.jsonl")
    parser.add_argument("--limit", type=int, default=40)
    args = parser.parse_args()

    payload = pickle.loads(args.adapter.read_bytes())
    lora = {tuple(key.split("/")): value for key, value in payload["lora"].items()}
    print(f"adapter: rank={payload['rank']} scale={payload['scale']} groups={len(lora)} base={payload['base']}")

    params, config = load_checkpoint(payload["base"])
    model = SimpleAttentionNetwork(config)
    merged = merge_lora(params, lora, payload["scale"])
    tokenizer = get_tokenizer(config.vocab_size)

    rows = [json.loads(line) for line in args.holdout.open(encoding="utf-8")]
    needs_arguments = [row for row in rows
                       if row["phase"] == "refine" and row["answers"] and row["answers"][0]["arguments"]]
    sample = needs_arguments[:args.limit]
    print(f"holdout refine rows needing arguments: {len(needs_arguments)}, testing {len(sample)}\n")

    exact = empty = 0
    for row in sample:
        prompt, _ = render_example(row)
        raw = generate(model, merged, tokenizer, prompt, max_new_tokens=96, stream=False)
        produced = tool_call_arguments(raw)
        wanted = row["answers"][0]["arguments"]
        if produced == wanted:
            exact += 1
        elif produced == {}:
            empty += 1
            print(f"  EMPTY  {row['query'][:60]!r} wanted {json.dumps(wanted, ensure_ascii=False)}")
        else:
            print(f"  WRONG  {row['query'][:60]!r}")
            print(f"         wanted {json.dumps(wanted, ensure_ascii=False)}")
            print(f"         got    {json.dumps(produced, ensure_ascii=False)}")

    print(f"\nunquantised LoRA on {len(sample)} argument rows: {exact} exact, {empty} answered {{}}")
    print(f"the 4-bit export scored 16 of 536 ({16 / 536:.1%}) on the same kind of row")


if __name__ == "__main__":
    main()
