# Usage capture

**Status:** built and shipped with the `# ` feature. Recording is unconditional; uploading is off by default.
What is left is the analysis side - turning the collected records into benchmark cases and training rows.

## Why this is worth more than another training run

`RESULTS.md` records eight runs. The last three landed at 14, 16 and 18 end-to-end cases out of 79 - and
79 cases is too small to tell those apart. Every further change is being judged on differences that are
inside the noise. A few hundred real queries fix that before they train anything.

The training case is the same finding from the other side. The corpus is written by a teacher model told
to phrase requests; `RESULTS.md` shows where that distribution stops matching players. Real queries remove
the guess instead of improving it.

## What the mod writes

`Terminal/UsageCapture.cs` appends one JSON object per `# ` request to `UserData/Hash/queries.jsonl`, always.
Recording and sharing are two separate questions and were one setting for exactly one commit, which answered both
wrongly: a player who turns sharing on has nothing to share, because the recording starts at the same moment, and
a player who leaves it off never sees what they would have been sending.

`Game/UsageReport.cs` is the only thing that moves the file, and only while `NeedleShareUsage` (default `false`)
is on. It uploads to `POST https://hash.doomods.com/api/telemetry` when the terminal closes and clears what the
server confirmed - a 503, a timeout or a broken connection leaves the file exactly where it was. While sharing is
off the file is still trimmed to the newest 5,000 records, because a file nobody sends must not grow forever.

The player is asked once, the first time they use `# `, and answers with `share on` or `share off` in the terminal
(`Terminal/UsageCapture.cs` remembers that the question was put, in `UserData/Hash/share-asked`). The setting
itself stays in `MelonPreferences.cfg`, so a consent given in the terminal can be withdrawn without opening it.

The service is `DooDesch-Mods/ScheduleOne-HashTelemetry`, Dokploy project **Hash**, records on the `/data` volume
as `records/hash-<version>-<day>.jsonl`. `https://hash.doomods.com` shows the counts, the outcome split and the
correction pairs - command words only, never what a player typed. One line looks like:

```json
{"query":"gib mir 10 og kush","commands":["give ogkush 10"],"confidence":0.98,"proven":true,
 "constrained":false,"error":"","outcome":"accepted"}
{"query":"bring mich nach hause","commands":["teleport mayor"],"confidence":1.0,"proven":false,
 "constrained":false,"error":"","outcome":"corrected","actual":"teleport #home"}
```

`outcome` is one of:

| | what closed the record |
|---|---|
| `accepted` | the commands ran and the console did not complain |
| `rejected` | nothing ran, or the console returned an error; `error` says which |
| `corrected` | the commands ran, and the player typed a command line themselves straight afterwards |
| `retried` | the request failed and the player asked again |

`actual` is the line the player typed, and appears whenever one closed the record - so a `rejected` line
usually carries the right answer beside the wrong one.

**`corrected` is a hint, not a verdict.** The next line a player runs may simply be the next thing they
wanted. That is why `actual` is always written: the judgement belongs to whoever reads the file.

No player id, no session id, no save name, no timestamp. The file is in order, which is all the model
needs, and every field that is not needed is a field that has to be explained before someone hands it over.

## How the loop turns

Four commands. Each one is a script in this folder, and each says what it dropped and why.

```powershell
$env:HASH_TELEMETRY_TOKEN = "<EXPORT_TOKEN from the Dokploy app 'hash-telemetry'>"

python pull_records.py                  # -> data-shared/records.jsonl, appends only what is new
python cases_from_records.py            # what it would add to the benchmark, and why the rest was dropped
python cases_from_records.py --write    # append them to NeedleBenchmark/cases.json
python benchmark_human_cases.py         # re-render the scorable set - REQUIRED after any case change
pwsh -File run_pipeline.ps1 -ForceData  # rebuild the corpus, train, export, score
```

`-ForceData` is not optional after cases change. A new case removes its phrasing from training, and the
corpus on disk still contains it until it is rebuilt.

### Why a case is worth more than a query

A raw request has no ground truth and would have to be labelled by hand. Three kinds of record carry one
already, and `cases_from_records.py` takes exactly those:

| record | the case it makes |
|---|---|
| `corrected` | the request, answered by the line the player typed instead |
| `rejected` with `actual` | the same, after a failure - the answer that was missing |
| `accepted` with one command | a request that demonstrably worked |

Everything else is dropped and counted: an ambiguous multi-command answer, a rejection nobody followed up,
a phrasing already in the benchmark. The count of `unknown command` is the useful one - it means players
are on a newer game or carry mods the snapshot in `data/` has never seen, and `pull_schema.py` is due.

### The guard that makes this safe to repeat

Adding a case removes its phrasing from training, mechanically. `expected_assignments` builds
`excluded_queries` from every case in `cases.json` and `validate_dataset` drops any row that collides,
with a reason. Without it the benchmark would measure memorisation a little more with every cycle, and the
numbers would climb while the model got worse - which is exactly what the pipeline's own 99 % gate already
does, and why `RESULTS.md` tells you to ignore it.

That guard is the reason cases go into `cases.json` rather than into a second file. Anything that collects
evaluation data somewhere the corpus builder cannot see is a leak waiting to happen.

### Comparing two runs a year apart

`finetune_hash.py` writes a `provenance` block into its report: the commit, whether the tree was dirty, and
content digests of the train split, the validation split, the base checkpoint and `cases.json`. A row in
`RESULTS.md` is checkable against it - same `casesDigest` means the two runs were scored on the same
benchmark, and a different `commit` means the renderer or the grounding rule may have moved underneath the
comparison. Runs 1, 2 and 4 are absent from that table because nobody could reconstruct what they measured.

### Rolling back

The adapter is one file in a release. A run that scores worse than the shipped one is not published:
`Hash.Needle.cact` stays at the previous version and the reports go in `RESULTS.md` as a negative result.
Four of the first eight runs were exactly that.

### What is not built yet

**Turning observed failures into training rows.** Cases fix the measurement; they do not by themselves fix
the model, because 79 - or 300 - rows are too few to train on. The step after this one is to feed the
observed failure *patterns* back as teacher seeds, the way the published flywheel work does: group the
corrections, and have the teacher write many phrasings around each real one. `augment_train.py` is the
place that already appends derived rows to a finished corpus.

**A regression gate.** `run_pipeline.ps1` gates on the generated holdout, which `RESULTS.md` shows is the
wrong number. It should gate on the hand-written and shared cases instead, and refuse to export weights
that score below the shipped adapter.

## Practices this follows, and where they come from

Checked against the published work in September 2026 rather than assumed:

- **Implicit signals beat explicit feedback.** The adaptive-flywheel study got 26 usable routing errors out
  of 495 thumbs-down samples ([arXiv:2510.27051](https://arxiv.org/abs/2510.27051)). Our `corrected` and
  `retried` outcomes need no button, and `actual` carries the correct answer rather than only a complaint.
- **Keep the evaluation set out of the training data, mechanically.** Contamination audits put leakage at
  1-45 % across popular benchmarks and 29 % for MMLU, worth 13 points on a clean re-test. A guard that a
  person has to remember is a guard that fails.
- **Hold the benchmark fixed while comparing, and record which version each run used.** Otherwise a
  growing case set makes every historical number incomparable.
- **Parameter-efficient tuning for frequent updates.** LoRA on a 45M base is already the cheapest possible
  version of this - one epoch, hours on a CPU, a 23 MB artefact.
- **Staged rollout with a rollback path.** Ours is coarse but real: the adapter ships in a versioned release
  and the previous one is a file away.

## Boundaries

- Recording is unconditional and local. Sharing is opt-in, default off. Not opt-out.
- Nothing is uploaded until the player switches sharing on, and the file is cleared only after the server
  confirms it has the records.
- No identifiers of any kind, and no timestamp finer than the day the server files it under.
- The player can delete the file at any time and it is recreated empty.
- Worth stating in the release notes, not just the setting.
