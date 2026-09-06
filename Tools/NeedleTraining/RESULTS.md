# What the runs measured

Eight training runs, 2026-09-02 to 2026-09-06, on a Ryzen 5 5600H (CPU, ~5 h for three epochs). Teacher
was FreeToken serving `Qwen3.6-35B-A3B-NVFP4`; it is only needed to build the corpus, not to train.

This file exists so the negative results are not repeated. Four of the six things tried made the adapter
worse, and none of that is visible from the pipeline's own gate.

## The two numbers, and why they disagree

**The gate** (`run_pipeline.ps1`, `results/latest.json`) scores `data-production/holdout.jsonl` - phrasings
written by the same teacher that wrote the training rows. It demands 99 %.

**The honest measure** is `NeedleBenchmark/cases.json`: 79 hand-written cases in four languages, whose
query strings `expected_assignments` deliberately keeps out of the corpus. `benchmark_human_cases.py`
renders them in the row format `NeedleSmoke benchmark` reads, so it runs offline, without the game.

Score them with `score_as_mod.py`, not with NeedleSmoke's raw exact-match. The mod does not compare the
model's argument to the canonical token literally - `NeedleArgument.TryResolveText` resolves it against
the value catalogue first, so "og kush" and near-misses still land. Raw scoring understates the shipped
adapter by seven end-to-end cases.

The two moved in opposite directions. The gate climbed from 59.5 % to 75.8 % across runs whose accuracy
on hand-written cases fell. Optimising against it is optimising against the teacher.

## The runs

End-to-end means both phases correct for a case: the router picked the command **and** the refinement
filled the arguments.

| | change from the run before | end-to-end | route | arguments |
|---|---|---|---|---|
| base | no adapter at all | 10/79 | 7.6 % | 27/73 |
| **run 3, 1 epoch** | **shipped** | **23/79** | **78.5 %** | 28/73 |
| run 5, 3 epochs | + query-relevant enums | 19/79 | 73.4 % | 23/73 |
| run 7, 1 epoch | + enums, number words | 18/79 | 63.3 % | 27/73 |
| run 6, 1 epoch | + spacing variants | 17/79 | 57.0 % | 26/73 |
| run 8, 1 epoch | route rows only | 15/79 | 55.7 % | 20/73 |

Runs 1, 2 and 4 are not in the table: they were measured against a stale `human-cases.jsonl` that still
carried the old stripped schemas, so their refine numbers are not comparable. That mistake is the reason
this file names the contract explicitly - re-run `benchmark_human_cases.py` after **any** change to
`route_tool` or `refinement_tool`, or the benchmark asks a question the training never answered.

## What helped

**Full tool schemas.** `refinement_tool` stripped `parameters.type`, `additionalProperties`, `required`
and every property's `type` and `description`, and `FULL_DESCRIPTION_CHARACTERS = 0` left the tool
description empty. The runtime sends all of them (`NeedleTool.Write`). Routing went from 54.4 % to 78.5 %.

**A system block.** `render_example` omits it entirely when a row has no `system` key, and no row had one,
while the mod always calls `Init(SystemFacts(), ...)`. Training now carries it across eight locales so the
adapter learns to ignore it rather than memorise one string.

**Query-relevant enums.** The first version took the first 80 characters of the catalogue alphabetically,
so `give` offered `acid, acunit, addy, airpot` and never `ogkush`; "give me 10 ogkush" came back as
`apron`. `NeedleTool.RelevantValues` ranks the values against what the player typed, and `enum_choices`
now mirrors it. Wrong values on holdout argument rows halved, 35 to 15.

## What did not

**More epochs.** Three epochs cost three end-to-end cases against one. Train and validation loss both
improve the whole way (0.185 -> 0.097) because validation is teacher-distributed and cannot see it. Three
epochs also produce runaway generations: 26 of 73 refine answers hit `tool call truncated: token budget
exhausted` against 4 after one epoch, although training targets are only 39 tokens at the median.

**Spelling variants for spacing.** "og kush" -> `ogkush` looked like an obvious gap - the corpus never
shows it. It is not a gap: `NeedleProtocol.Normal` strips non-alphanumerics from both sides before the
enum is ranked, so the runtime already resolves it. Teaching it cost five end-to-end cases, because the
split points were random and rows like `gran ddaddypurpleseed` are noise. Only the number words are kept,
which normalisation genuinely cannot reach: "zehn" never becomes 10.

**Training on route rows only.** The idea was to leave the refinement phase at base quality, since the
untuned base fills arguments as well as any adapter. It does not work - a LoRA drifts the phase it never
sees (arguments 20/73, worse than base's 27/73) and routing suffers from the smaller corpus.

**Two models.** Adapter routes, base fills arguments: 24/79 against 23/79. One case, and `needle_init`
and `needle_load` are global functions with no context handle, so it would mean reloading 23 MB per
query. Not worth it.

## What is left

The argument phase. The adapter (28/73) is barely ahead of the untuned base (27/73): fine-tuning buys
routing and costs extraction.

The corpus is the reason. The teacher prompt demands that "every numeric canonical argument must appear
exactly as digits in the request" and `grounds_literals` enforces it, so of 877 numeric training arguments
**not one** was ever written as a word. 49 of the 73 benchmark cases with arguments need exactly that
kind of mapping. The model never saw the problem it is being asked to solve.

Fixing it means relaxing `grounds_literals`, letting the teacher phrase arguments the way players do, and
re-running the data stage - roughly seven hours of teacher time. That is a data problem, not a training
one, and no amount of further training will reach it.
