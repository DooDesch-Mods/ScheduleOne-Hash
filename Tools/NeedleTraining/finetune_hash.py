"""Fast, validation-driven Hash LoRA training on the pinned Needle 2.0.2 model/export format.

The optimizer and numerical safeguards mirror cactus-needle 2.0.8 while retaining the installed 2.0.2
checkpoint loader and adapter format required by Hash's bundled native engine.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import pathlib
import pickle
import time

import numpy as np

from needle.model.finetune import (
    merge_lora,
    render_example,
)
from needle.model.tokenizer import BOS_ID, EOS_ID, PAD_ID, get_tokenizer

LORA_TARGETS = ("q_proj", "k_proj", "v_proj", "gate_proj", "out_proj")


def fitted_length(paths: list[pathlib.Path], tokenizer, cap: int) -> tuple[int, int]:
    longest = 0
    for path in paths:
        with path.open(encoding="utf-8") as handle:
            for line in handle:
                if not line.strip():
                    continue
                prompt, target = render_example(json.loads(line))
                longest = max(longest, len(tokenizer.encode(prompt)) + len(tokenizer.encode(target)) + 2)
    bucket = 128
    while bucket < min(longest, cap):
        bucket *= 2
    return min(bucket, cap), longest


def load_buckets(path: pathlib.Path, tokenizer, cap: int) -> tuple[dict[int, tuple[np.ndarray, np.ndarray]], int]:
    """Encode without truncation and pad each row only to its 128/256 power-of-two bucket."""
    rows: dict[int, list[tuple[list[int], list[float]]]] = {}
    longest = 0
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            if not line.strip():
                continue
            prompt, target = render_example(json.loads(line))
            prompt_ids = tokenizer.encode(prompt)
            target_ids = tokenizer.encode(target)
            ids = [BOS_ID] + prompt_ids + target_ids + [EOS_ID]
            mask = [0.0] * (1 + len(prompt_ids)) + [1.0] * (len(target_ids) + 1)
            longest = max(longest, len(ids))
            bucket = 128
            while bucket < len(ids):
                bucket *= 2
            if bucket > cap:
                raise ValueError(f"{path}: {len(ids)} tokens exceed max length {cap}")
            pad = bucket - len(ids)
            rows.setdefault(bucket, []).append((ids + [PAD_ID] * pad, mask + [0.0] * pad))
    return ({bucket: (np.asarray([row[0] for row in values], np.int32),
                      np.asarray([row[1] for row in values], np.float32))
             for bucket, values in rows.items()}, longest)


def save_adapter(path: pathlib.Path, lora, scale: float, base: pathlib.Path, rank: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as handle:
        pickle.dump({
            "lora": {"/".join(key): {"A": np.asarray(value["A"]), "B": np.asarray(value["B"])}
                     for key, value in lora.items()},
            "scale": float(scale),
            "base": str(base),
            "rank": rank,
        }, handle)


def host_lora_target_paths(params) -> list[tuple[str, ...]]:
    """The official target filter without compiling a JAX max reduction for every base tensor."""
    from flax.traverse_util import flatten_dict
    flat = flatten_dict(params)
    return [path for path, value in flat.items()
            if path[-1] == "kernel"
            and "stack" in path
            and "layers" in path
            and any(target in path for target in LORA_TARGETS)
            and np.max(np.abs(value)) > 1e-6]


def host_init_lora(params, paths: list[tuple[str, ...]], rank: int, seed: int):
    """Initialize the same A-normal/B-zero adapter without per-shape JAX random compilations."""
    from flax.traverse_util import flatten_dict
    flat = flatten_dict(params)
    rng = np.random.default_rng(seed)
    lora = {}
    for path in paths:
        weight = flat[path]
        in_dim, out_dim = weight.shape[-2], weight.shape[-1]
        lead = weight.shape[:-2]
        lora[path] = {
            "A": rng.standard_normal(lead + (in_dim, rank)).astype(np.float32) / rank,
            "B": np.zeros(lead + (rank, out_dim), np.float32),
        }
    return lora


def host_adamw_state(lora, optax):
    """Construct clip+AdamW's public state using NumPy zeros, avoiding per-leaf JAX dispatch."""
    import jax
    zeros = jax.tree_util.tree_map(np.zeros_like, lora)
    adam = optax.ScaleByAdamState(count=np.asarray(0, np.int32), mu=zeros,
                                  nu=jax.tree_util.tree_map(np.zeros_like, lora))
    return (optax.EmptyState(), adam, optax.EmptyState())


def cosine_learning_rate(step: int, total_steps: int, warmup: int, peak: float) -> np.float32:
    """Optax-compatible 0 -> peak warmup followed by cosine decay to zero."""
    if step < warmup:
        return np.float32(peak * step / max(1, warmup))
    progress = min(1.0, (step - warmup) / max(1, total_steps - warmup))
    return np.float32(peak * 0.5 * (1.0 + math.cos(math.pi * progress)))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("train", type=pathlib.Path)
    parser.add_argument("--validation", type=pathlib.Path, required=True)
    parser.add_argument("--checkpoint", type=pathlib.Path, required=True)
    parser.add_argument("--out", type=pathlib.Path, required=True)
    parser.add_argument("--log", type=pathlib.Path, default=None)
    parser.add_argument("--epochs", type=int, default=12)
    parser.add_argument("--patience", type=int, default=2)
    parser.add_argument("--min-delta", type=float, default=1e-4)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--lr", type=float, default=1e-4)
    parser.add_argument("--lora-rank", type=int, default=32)
    parser.add_argument("--lora-alpha", type=float, default=32.0)
    parser.add_argument("--max-len", type=int, default=256)
    parser.add_argument("--seed", type=int, default=1701)
    parser.add_argument("--jax-cache", type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parent / "artifacts" / "jax-cache")
    parser.add_argument("--remat", action="store_true",
                        help="retain activation rematerialization on CPU (less memory, slower compile/steps)")
    parser.add_argument("--inspect-only", action="store_true",
                        help="load and validate checkpoint/data shapes without compiling a training step")
    args = parser.parse_args()

    if args.epochs < 1 or args.batch_size < 1 or args.lora_rank < 1:
        raise SystemExit("epochs, batch-size and lora-rank must be positive")

    args.jax_cache.mkdir(parents=True, exist_ok=True)
    os.environ.setdefault("JAX_COMPILATION_CACHE_DIR", str(args.jax_cache.resolve()))
    import jax
    import jax.numpy as jnp
    import optax
    jax.config.update("jax_persistent_cache_min_compile_time_secs", 0)
    jax.config.update("jax_persistent_cache_min_entry_size_bytes", -1)
    jax.config.update("jax_compilation_cache_max_size", 8 * 1024 * 1024 * 1024)
    from needle.model import architecture as needle_architecture
    from needle.model.run import load_checkpoint

    started = time.perf_counter()
    params, config = load_checkpoint(str(args.checkpoint))
    config.dtype = "float32"
    params = jax.tree_util.tree_map(lambda value: np.asarray(value).astype(np.float32), params)
    paths = host_lora_target_paths(params)
    lora = host_init_lora(params, paths, args.lora_rank, args.seed)
    params = jax.device_put(params)
    tokenizer = get_tokenizer(config.vocab_size)
    train_buckets, train_longest = load_buckets(args.train, tokenizer, args.max_len)
    val_buckets, val_longest = load_buckets(args.validation, tokenizer, args.max_len)
    train_rows = sum(len(seqs) for seqs, _ in train_buckets.values())
    val_rows = sum(len(seqs) for seqs, _ in val_buckets.values())
    longest = max(train_longest, val_longest)
    if not train_rows or not val_rows:
        raise SystemExit("training and validation datasets must both contain examples")

    if args.inspect_only:
        print(json.dumps({"backend": jax.default_backend(), "trainRows": train_rows,
                          "validationRows": val_rows,
                          "trainBuckets": {str(key): len(value[0]) for key, value in train_buckets.items()},
                          "validationBuckets": {str(key): len(value[0]) for key, value in val_buckets.items()},
                          "maxLen": max(train_buckets | val_buckets),
                          "longest": longest, "dtype": config.dtype}, ensure_ascii=False))
        return

    backend = jax.default_backend()
    remat_enabled = backend != "cpu" or args.remat
    if backend == "cpu" and not remat_enabled and args.batch_size > 8:
        raise SystemExit("CPU no-remat is memory-safe only through batch size 8; use --batch-size 8 or --remat")
    if not remat_enabled:
        # Needle 2.0.2 hardcodes nn.remat around its scanned body. Current Needle makes this a config knob;
        # bypassing the transform on CPU keeps identical parameters while trading RAM for much faster training.
        needle_architecture.nn.remat = lambda target, *unused_args, **unused_kwargs: target
    model = needle_architecture.SimpleAttentionNetwork(config)
    scale = args.lora_alpha / args.lora_rank
    steps_per_epoch = sum(math.ceil(len(seqs) / args.batch_size)
                          for seqs, _ in train_buckets.values())
    total_steps = args.epochs * steps_per_epoch
    warmup = min(max(1, total_steps // 20), max(1, total_steps - 1))
    optimizer = optax.chain(optax.clip_by_global_norm(1.0), optax.scale_by_adam(),
                            optax.add_decayed_weights(1e-4))
    opt_state = host_adamw_state(lora, optax)
    lora, opt_state = jax.device_put((lora, opt_state))

    def loss_parts(current_lora, ids, mask):
        logits = model.apply({"params": merge_lora(params, current_lora, scale)}, ids)
        logits, targets, target_mask = logits[:, :-1], ids[:, 1:], mask[:, 1:]
        ce = optax.softmax_cross_entropy_with_integer_labels(logits, targets)
        return (ce * target_mask).sum(), target_mask.sum()

    def loss_fn(current_lora, ids, mask):
        total, tokens = loss_parts(current_lora, ids, mask)
        return total / jnp.maximum(tokens, 1.0)

    @jax.jit
    def train_step(current_lora, current_state, ids, mask, learning_rate):
        loss, grads = jax.value_and_grad(loss_fn)(current_lora, ids, mask)
        updates, current_state = optimizer.update(grads, current_state, current_lora)
        updates = jax.tree_util.tree_map(lambda update: -learning_rate * update, updates)
        return optax.apply_updates(current_lora, updates), current_state, loss

    eval_step = jax.jit(loss_parts)

    def validation_loss(current_lora) -> float:
        total_loss = 0.0
        total_tokens = 0.0
        for bucket, (sequences, masks) in val_buckets.items():
            for start in range(0, len(sequences), args.batch_size):
                ids = sequences[start:start + args.batch_size]
                target_masks = masks[start:start + args.batch_size]
                if len(ids) < args.batch_size:
                    missing = args.batch_size - len(ids)
                    ids = np.concatenate((ids, np.full((missing, bucket), PAD_ID, np.int32)))
                    target_masks = np.concatenate((target_masks,
                                                   np.zeros((missing, bucket), np.float32)))
                loss_sum, token_count = eval_step(
                    current_lora, jnp.asarray(ids), jnp.asarray(target_masks))
                total_loss += float(loss_sum)
                total_tokens += float(token_count)
        return total_loss / max(1.0, total_tokens)

    train_bucket_counts = {str(key): len(value[0]) for key, value in train_buckets.items()}
    val_bucket_counts = {str(key): len(value[0]) for key, value in val_buckets.items()}
    print(f"backend={backend} train={train_rows} validation={val_rows} ",
          f"buckets={train_bucket_counts} longest={longest} batch={args.batch_size} "
          f"remat={str(remat_enabled).lower()}", flush=True)
    print(f"lora_rank={args.lora_rank} alpha={args.lora_alpha:g} groups={len(paths)} ",
          f"steps={total_steps} warmup={warmup} cosine=true clip=1", flush=True)

    history = []
    best_val = float("inf")
    bad_epochs = 0
    rng = np.random.default_rng(args.seed)
    global_step = 0
    for epoch in range(args.epochs):
        epoch_started = time.perf_counter()
        batches = []
        orders = {}
        for bucket, (sequences, _) in train_buckets.items():
            order = rng.permutation(len(sequences))
            pad = (-len(order)) % args.batch_size
            if pad:
                order = np.concatenate((order, order[:pad]))
            orders[bucket] = order
            batches.extend((bucket, start) for start in range(0, len(order), args.batch_size))
        rng.shuffle(batches)
        losses = []
        report_every = max(1, len(batches) // 10)
        for step, (bucket, start) in enumerate(batches, 1):
            sequences, masks = train_buckets[bucket]
            index = orders[bucket][start:start + args.batch_size]
            learning_rate = cosine_learning_rate(global_step, total_steps, warmup, args.lr)
            lora, opt_state, loss = train_step(
                lora, opt_state, jnp.asarray(sequences[index]), jnp.asarray(masks[index]),
                jnp.asarray(learning_rate))
            global_step += 1
            losses.append(float(loss))
            if step % report_every == 0 or step == len(batches):
                print(f"epoch={epoch + 1} step={step}/{len(batches)} bucket={bucket} "
                      f"loss={losses[-1]:.5f} seconds={time.perf_counter() - epoch_started:.1f}",
                      flush=True)
        val = validation_loss(lora)
        elapsed = time.perf_counter() - epoch_started
        record = {"epoch": epoch + 1, "trainLoss": float(np.mean(losses)),
                  "validationLoss": val, "seconds": elapsed}
        history.append(record)
        print(f"epoch={epoch + 1}/{args.epochs} train={record['trainLoss']:.5f} "
              f"validation={val:.5f} seconds={elapsed:.1f}", flush=True)
        if val < best_val - args.min_delta:
            best_val = val
            bad_epochs = 0
            save_adapter(args.out, lora, scale, args.checkpoint, args.lora_rank)
            print(f"best={best_val:.5f} saved={args.out}", flush=True)
        else:
            bad_epochs += 1
            if bad_epochs >= args.patience:
                print(f"early_stop=true patience={args.patience}", flush=True)
                break

    report = {
        "trainer": "Hash SOTA-compatible Needle trainer",
        "needleRuntimePackage": "2.0.2",
        "optimizerRecipe": "cactus-needle 2.0.8 warmup-cosine + global-norm clip 1",
        "train": str(args.train), "validation": str(args.validation),
        "checkpoint": str(args.checkpoint), "adapter": str(args.out),
        "jaxCache": str(args.jax_cache.resolve()),
        "backend": backend, "remat": remat_enabled, "trainRows": train_rows,
        "validationRows": val_rows, "trainBuckets": train_bucket_counts,
        "validationBuckets": val_bucket_counts,
        "maxLen": max(train_buckets | val_buckets), "longest": longest,
        "batchSize": args.batch_size, "rank": args.lora_rank, "alpha": args.lora_alpha,
        "learningRate": args.lr, "maxEpochs": args.epochs, "patience": args.patience,
        "bestValidationLoss": best_val, "history": history,
        "seconds": time.perf_counter() - started,
    }
    if args.log:
        args.log.parent.mkdir(parents=True, exist_ok=True)
        args.log.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
