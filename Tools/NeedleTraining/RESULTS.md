# What the runs measured

Nine training runs, 2026-09-02 to 2026-09-06, on a Ryzen 5 5600H (CPU, ~5 h for three epochs). Teacher
was FreeToken serving `Qwen3.6-35B-A3B-NVFP4`; it is only needed to build the corpus, not to train.

This file exists so the negative results are not repeated, and so the guesses that were corrected stay
corrected. Most of what was tried made the adapter worse, and none of that is visible from the pipeline's
own gate.

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
| run 9, 1 epoch | + grounding relaxed | 18/79 | 63.3 % | **30/73** |
| run 9, 3 epochs | same corpus | 20/79 | 75.9 % | 26/73 |

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

**Two models.** Adapter routes, base fills arguments: 24/79 against 23/79. One case. The reload it would
need is cheap - see below, 3 ms - so the reason not to build it is the single case, not the cost.

## The two phases pull against each other

Run 9 relaxed `grounds_literals` so the teacher could write "gib mir zehn og kush" instead of being forced
to spell every number as digits. It worked, exactly where it was aimed: 30 of 73 argument cases after one
epoch, the best any adapter has managed and the first time one beat the untuned base. Routing fell to
63.3 % in the same run, and after three epochs the two swapped - routing back to 75.9 %, arguments down to
26. Three runs now show the same shape: whenever one phase improves, the other gives way.

So the phases want different adapters, which was dismissed earlier on the grounds that `needle_init` and
`needle_load` are global functions and the mod would have to reload 23 MB between the two phases of every
query. That was a guess, and it was wrong: `probe_load_cost.py` measures one `needle_load` at **3 ms**,
against roughly 100 ms of inference per phase. The design is cheap.

It is also not worth building. Best router with best refiner reaches 24/79 against 23/79 for the shipped
adapter alone - one case. The mod's own value resolution already recovers most of what a second adapter
would add, so the complexity buys nothing.

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

### Four cases that were called unwinnable, and were ours

- **The granddaddy cases: 4, and the answer was not on the list.** For "give me 4 grandaddy seed" the ranked
  enum read `cocaseed, addy, ogkushseed, ...` and `granddaddypurpleseed` was absent, so every model scored
  them wrong by construction. This section used to end there, with "these four cases remain unwinnable".

  It was wrong twice over, and both corrections are ours rather than the model's. `addy` was a divergence
  between the corpus builder and the mod, since removed. What actually kept `granddaddypurpleseed` off the
  list was the filler span "me", a prefix of `meth` and `megabean` at 4000 - see the 2026-09-07 triage below.
  With that skipped the enum for the same query reads `granddaddypurple, granddaddypurpleseed,
  b2s_granddaddykush, b2s_granddaddykush_seed`, and the model answers `granddaddypurple`: the bud rather than
  the seed, which is a model error and a fair one to measure.

Two cases in the set are genuinely unreachable, and they are marked `unscorable` in `cases.json` rather than
deleted: `historical-supergrow` expects an item the game does not have, and `historical-teleport-major` a
teleport target that is not among the 129 live ones. Both are printed on every render, because a case removed
from the denominator is not a case fixed.

### Every number in this file is a harness number

The mod's second pass - the one that fills in the arguments - never ran in a shipped build. `Translate`
asked `result.Error == null`, and `NaturalCommandTranslation` stores `error ?? ""`, so the test was false
for every answer the engine has ever returned. It has been that way since the feature was written.

The benchmark renders both passes explicitly and never went through that code, so nothing here measured it.
What players got instead was the routing answer on its own: "make it noon" came back as `settime`, with no
argument, and the console refused it. Only a request beginning with a command word was ever answered in
full, because that one is resolved without the model at all.

So the argument numbers in this file describe a pipeline that was live for the first time on 2026-09-06.
The same three requests, in game, before and after: `settime` / `settime` / `triggerlightning` became
`settime 1200` / `settime 1200` / `triggerlightning`.

## The triage of 2026-09-07, and the ruler it replaced

Forty-one agents took the 48 remaining failures of `full-timewords2` apart, ten families at a time, and every
diagnosis was then attacked by three adversarial lenses - one checking the evidence, one hunting collateral
damage, one re-deriving the claimed gain. **Every one of the ten was refuted by at least two of three.** What
survived is what the refutations themselves carried, and the most important finding was not in any diagnosis.

### The scorer was not a mirror

`score_as_mod.py` exists because the raw model answer is not what a player gets - the mod resolves it first -
and `generate_data.candidate_score` renders the enum the mod would send. Both had drifted from the C# they
claim to mirror, and the drift was not small:

- `candidate_score` scored a mid-word hit at `4000 - length difference` where the runtime gives
  `2000 - offset`, kept a reverse-containment band the runtime does not have, and imposed a four-character
  floor the runtime does not have. **321 of 906 rendered enum slots differed from what the mod would send**,
  across 68 of the 79 rows.
- `resolve` had no exact-match short-circuit, so it credited `mixingstationmk2` as `mixingstation` and
  `ogkushseed` as `ogkush`.
- It had no notion of ambiguity, where the runtime refuses a tie outright: it credited `rain` as `heavyrain`,
  which the mod answers with "'rain' is ambiguous".

Both are now ports rather than approximations, and one divergence is left on purpose and documented in the
file: `values.json` cannot say whether an empty slot has a provider, so the scorer cannot reproduce the
refusal the mod issues for a slot that is owned and empty.

### Tools/NeedleReplay: measure the mod, not a description of it

A mirror that has to be kept in step will drift again. `Tools/NeedleReplay` compiles `Terminal/` directly -
which is possible because `Terminal/` has no engine references - rebuilds the refinement toolset for a case's
query and routed command, feeds it the model's own answer, and prints the console line the player would have
been offered or the refusal they would have seen. There is nothing to keep in step.

It cannot see marks: `#home` is live world state and the committed snapshot has none, so the seven mark cases
are reported and left out of its count rather than scored as failures.

    NeedleReplay data/commands.json data/values.json ../NeedleBenchmark/cases.json <report.json>

### One letter, and thirteen filler words, were choosing the enum

The runtime ranks every one-to-six-word span of the query against the catalogue. Two kinds of span rank
nothing and displace everything, because a prefix scores 4000 whatever its length:

- **Filler.** "me" is a prefix of `meth` and `megabean`, "a" of `albert_hoover`. The mod already declares the
  list of words that carry nothing (`NaturalFiller`, used by `TryBuildDirect`) and did not consult it here.
  For "give me 4 grandaddy seed" the offered values were `meth, megabean, metalsign, ...`; for
  "llevame a los muelles" they were `albert_hoover, alison_knight, austin_steiner, ...`. Those are exact
  catalogue values, so `TryResolveText` accepts them in silence: **the player asking for the docks was
  teleported to a dealer's house, with no error anywhere.**
- **A single letter.** "I" is a prefix of `iodine`, which ties with "mixing" against `mixingstation` at 4000
  and wins the tie for being shorter. `FuzzyMatcher` already refuses a one-character subsequence for exactly
  this reason.

Both spans are now skipped. `RESULTS.md` claimed the four granddaddy cases were unwinnable because the ranked
enum could not contain the answer; that was true, and it was our doing. The enum for that query now reads
`granddaddypurple, granddaddypurpleseed, b2s_granddaddykush, b2s_granddaddykush_seed`.

### The quantity repair, built at last

Six cases where the model names the item and drops the count the player stated. `RESULTS.md` has said since
the ablation that "the repair belongs at runtime, where the mod can see that the player said a number and the
answer carries none"; `NeedleTool.RepairQuantity` is that repair. Three gates, each one a defect found before
it shipped:

- **One call only.** Filling per call turns two identical answers into two identical lines, and the session
  runs both - a player who asked for ten would be given twenty.
- **Only a declared signature.** For a command in `UsageExample.Known`, "[quantity]" describes the game. For
  every other command both the brackets and the label come from one heuristic over a free-text example, and
  `b2ssow`'s own `[amount]` is a 0..1 fraction whose absence means the maximum - the opposite of a count.
- **Never an overwrite, and never two candidates.** The words the resolved values occupy are claimed first,
  the way `TryBuildDirect` claims them, so "give me 5 og kush seed" does not read the 5 out of an item name.

It also drops a count of nothing that nobody asked for: `give mixingstation 0` is a line the console accepts
and that does nothing.

### What all of it measured

Two rulers, because they answer different questions. The single-call benchmark says what the model does with
a declaration; `NeedleReplay` says what a player would have been handed.

| | replay, player-visible | single call, exact |
|---|---|---|
| before this change set | **31 of 70** | 31 of 79 |
| the resolver alone, on the identical report | **37 of 70** | - |
| final, untuned base | 36 of 70 | 32 of 77 |
| final, shipped adapter | 31 of 70 | 29 of 77 |

**The quantity repair is +6 and costs nothing.** Measured by replaying one unchanged report through the
resolver before and after: six cases move from wrong to right, none the other way, and they are exactly the
six quantity cases. `give` is the only declared command with an optional numeric slot - the other five in the
live catalogue all belong to BreedToSeed and are gated out by the declared-signature rule, which is what that
rule was written for.

**The ranking fixes do not move the total, and are worth doing anyway.** 37 to 36 on the base model: the
model reshuffles its answers when the offered list changes, and at this sample size that is noise - this file
has said since run 9 that 20, 23 and 24 of 79 are not distinguishable. What they fix is not a score. A player
asking for the docks was being teleported to a dealer's house without an error, because "a" matched
`albert_hoover` and an exact catalogue value is accepted in silence. That is worth fixing at zero measured
gain.

**The time family is 10 of 11** on the replay - `time-noon-es` is the last one, where the model answers 0 with
`mediodia` on the list. It was 1 of 11 before any of this.

**The shipped adapter is behind the untuned base on both rulers** (31 against 36, 29 against 32), which is
the tenth independent measurement saying the same thing.

### The time family, finished

The core words were offered beside a reading the player had written, and won: given
`["800", "midnight", "dawn", ...]`, "set the time to 8am" was answered `midnight`. They are now suppressed
when the query carries a clock marker - am, pm, uhr, a separator, or the console's own four digits. A bare
number is not a marker, so "give me 5" is still not five o'clock. Table words are also offered longest-first,
so "nachmittag" is no longer beaten by the "mittag" inside it.

### The time family was ours to lose, and we were losing it

The eleven `settime` cases scored 0 of 11 in every condition, which read as a ceiling. It was not one. That
number was measured on the raw model output, and the mod does not run the raw model output - it resolves it.
Two things were missing, and both belong to the mod:

- **The hour was already there.** For "set the time to 8am" the base answers `8`, correctly, and the game
  wants `800`. Converting a bare hour to a full reading is arithmetic, not language, and doing it takes the
  same base run from 1 of 11 to **5 of 11** with no vocabulary and no training.
- **noon is a value, not a phrasing.** Nothing in the request says 1200, so the model answers 0 whatever it
  is told. Declaring the slot as a word it can pick, the way item and weather slots already work, gets noon
  in English, German and French and both `day` cases: **6 of 11**.

Offering only words then cost the hour cases - handed midnight and dawn and nothing numeric, "set the time
to 8am" is answered midnight. So the clock reading the player wrote goes on the list first, ahead of the
words. **8 of 11**, and the whole set at **31 of 79 exact and 46 routed**, against 25 and 43 for the same
base model on the same cases.

Same measurement, four renderings:

| Rendering | settime | exact | routed |
|---|---|---|---|
| number, `hhmm`, no enum | 1/11 | 25/79 | 43/79 |
| the enum fill, still a number | 5/11 | 28/79 | 43/79 |
| words only | 6/11 | 29/79 | 46/79 |
| the player's own reading first, then words | **8/11** | **31/79** | 46/79 |

`Terminal/TimeWords.cs` holds the table and the reader, `Tools/NeedleTraining/time_words.py` mirrors it line
for line, and both were checked against the same 26 values and 5 queries. Two consequences beyond the
benchmark: `# settime noon` is answered without asking the model at all, by the same local path that already
handled `# settime 1200`, and the corpus builder's grounding rule no longer demands that the digits appear
in the request - which had excluded every natural phrasing of the one command whose value nobody speaks as a
number. The teacher is told it may write a time as a time; `TIME_PHRASING_VERSION` makes the batches that
carry a time slot, and only those, pay for the new wording. Run 9 was built before that rule, so its corpus
still carries no natural `settime` phrasing at all.

### Two changes that came out of it

**A small vocabulary is now offered whole**, in the mod and in the corpus builder. Values that matched no
word in the query were dropped entirely, so "make it sunny" was offered a choice of `heavyrain` and
`lightrain` and never `clear` - one of setweather's three possible values. Measured over the benchmark's
53 catalogue-backed arguments, the expected value is present in the enum in **40 cases, up from 32**. The
fill stops where ranking starts to matter: a thousand-item catalogue is not padded with whatever sorts
first, which is the original bug that once offered "acid, acunit, addy, airpot" and never `ogkush`.

**The corpus builder's extra match direction is gone**, so it ranks the way the runtime ranks. Recall is
unchanged at 40/53 and one wrong answer stops being offered.

### A caveat on the quantity family

`NeedleToolset.Prepare` answers "give me 10 ogkush" deterministically, without asking the model at all -
there is a test for it. The benchmark rows go straight to inference and skip that path, so the dropped
quantity is a smaller problem for a player than these numbers suggest. It is still worth understanding,
because a request the direct path cannot parse falls through to exactly this behaviour.

## What is left

The argument phase. The adapter (28/73) is barely ahead of the untuned base (27/73): fine-tuning buys
routing and costs extraction.

The corpus is the reason. The teacher prompt demands that "every numeric canonical argument must appear
exactly as digits in the request" and `grounds_literals` enforces it, so of 877 numeric training arguments
**not one** was ever written as a word. 49 of the 73 benchmark cases with arguments need exactly that
kind of mapping. The model never saw the problem it is being asked to solve.

That was the reason to relax `grounds_literals`, and run 9 did it: arguments reached 30/73. It did not
carry through to more end-to-end cases, because routing paid for it.

Nine runs in, the honest read is that this corpus and this 45M model sit at roughly 23-24 of 79, and the
benchmark cannot see smaller differences than that - runs scoring 20, 23 and 24 are not distinguishable
on 79 cases. Further tuning against it is fitting noise.

`TELEMETRY.md` is the unblocking step, and it is a measurement problem before it is a training one. A few
hundred real queries make the differences visible; the corrected ones are training rows no teacher can
invent. Until then the ceiling is not a model limit anyone has demonstrated - it is the resolution of the
ruler.
