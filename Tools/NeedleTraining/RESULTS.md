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

## What a review of the engine's own documentation changed (2026-09-06)

Four claims in this file and in the mod were checked against the Needle package and the shipped archive.
Two survived, two did not, and one defect turned up that had been costing every run since.

**The two-pass flow is ours, not Needle's.** The engine documents one call over the whole catalogue: it
embeds the schemas, retrieves the top few itself, and generates a complete call. Comments in
`NeedleProtocol.cs` describing the split as "recommended by Needle" were unsupported and are corrected.
Keeping the split is still defensible - one call over all 78 full schemas scored 20/79 on the untuned base
and 19/79 on the tuned archive, well short of 78.5 % routing - but it is our design and has to earn its
keep as one.

**"Constrained retry" never turned anything on.** Both passes call the same native function; only the
toolset differs. The `--no-constrained` flag in the Python CLI is not read by the path it belongs to and
says nothing about this engine call. The log line said otherwise and is reworded.

**Fine-tuning did not remove the confidence head.** Both heads are present in the shipped archive; the mod
nulls the score itself in `WithoutConfidence()`. Preserved is not calibrated - upstream now suppresses
tuned confidence for that reason - but "the gate runs on nothing" is wrong: auto-run needs `Proven` plus
80 %, and every native prediction still needs a `#`.

**The base is understated here.** The 7.6 % routing figure requires matching an empty argument object.
Counting correct tool names and refusals over the committed responses gives 13/79, or 16.5 %.

### The defect that had been costing every run

`generate_data.py` built every refinement training row **without passing the query**, so `enum_choices`
ranked nothing and the enum came out **empty** - while the benchmark and the runtime both send a list
ranked against what the player typed, with the answer usually first. The corpus taught the model to fill an
argument from no candidates and then handed it candidates at inference.

This is the same failure the "query-relevant enums" entry above claims to have fixed. It was fixed in
`refinement_tool`, in the benchmark and in `refresh_enums.py` - and never in the one path that builds the
training rows, which is why that repair script exists and why the pipeline never calls it. Fixed now; the
next corpus is the first one to carry it.

## The schema ablation (2026-09-06)

Every run below is the same 79 hand-written cases as ONE call over all 78 eligible commands, each with its
full schema and an enum ranked against the player's words - the contract the engine documents, rather than
the route-then-refine split this mod invented. Untuned base at 4 bits unless stated. `full_catalogue_cases.py`
builds it, `schema_variants.py` mutates one field at a time, `score_full_catalogue.py` scores the whole call
array rather than only the first call.

| run | change | routed | exact |
|---|---|---:|---:|
| base | - | 43/79 | 24/79 |
| base, replayed | nothing | 43/79 | 24/79 |
| N | `arg1`/`arg2` renamed to `item`/`quantity` | 36/79 | 23/79 |
| D | a generic extraction sentence on the optional number | **45/79** | **27/79** |
| O | "Optionally specify" becomes "Specify" | 40/79 | 23/79 |
| R | the optional number made `required` | 43/79 | **28/79** |
| E | each command's own usage example appended | 41/79 | 21/79 |
| tuned adapter | - | 43/79 | 21/79 |

The replay is the control: identical input, identical score, so a difference between variants is real.

**The adapter is behind the untuned base on this contract**, 21 against 24, with identical routing. Together
with the earlier probe - full schemas but no ranked enums scored 10/79 - the ordering is clear: what we
declare is worth about fourteen cases, and the fine-tune is worth nothing here and possibly less.

**D is the only shipping-safe gain**, and it is mechanically derivable: the same two sentences appended to
any optional numeric parameter, no per-command prose. But it does not do what it was designed to do. Of the
15 `give` cases that state a quantity, the number is present in 3 under the base and **1** under D. Its
gain is routing, including on the give cases with no quantity at all (2 routed to 5). Reading the total
alone would have told the wrong story.

**The quantity failure is a decision, not an inability.** R recovers 11 of those 15 - so the value is
available to the model, it simply does not emit an optional field. R is not a fix: on the 7 cases with no
stated quantity it invents one. The repair belongs at runtime, where the mod can see that the player said a
number and the answer carries none, and is not something another corpus will teach.

**N and O are dead ends.** Renaming the parameters cost seven routed cases; removing "Optionally" cost three
and gained nothing.

### About a fifth of this benchmark cannot be won

- **settime: 0 of 11 correct in every condition** - base, D, E and the tuned adapter alike. "noon" is 1200
  and "eight" is 800, and no wording reaches it. The declaration is already honest ("24-hour time", `hhmm`),
  the usage example did not help, and E made routing worse. This is not a model to train; it is a value
  provider the mod should own, the way it already resolves item names.
- **The granddaddy cases: 4, and the answer is not on the list.** For "give me 4 grandaddy seed" the ranked
  enum is `cocaseed, addy, ogkushseed, ...` - `granddaddypurpleseed` is absent while `addy` is present,
  because it is a substring of "gr-addy". Every model scores these wrong by construction. SUSPECTED cause:
  `candidate_score` lets a short substring outrank the intended value.

Fifteen of 79 cases therefore measure our own value handling rather than the model. Any headline number
from this set has that ceiling built into it.

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
