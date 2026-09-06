# Opt-in usage capture

**Status:** built, off by default, shipped with the `# ` feature. What is left is the analysis side - turning a
returned `queries.jsonl` into benchmark cases and training rows.

## Why this is worth more than another training run

`RESULTS.md` records eight runs. The last three landed at 14, 16 and 18 end-to-end cases out of 79 - and
79 cases is too small to tell those apart. Every further change is being judged on differences that are
inside the noise. A few hundred real queries fix that before they train anything.

The training case is the same finding from the other side. The corpus is written by a teacher model told
to phrase requests; `RESULTS.md` shows where that distribution stops matching players. Real queries remove
the guess instead of improving it.

## What the mod writes

`MelonPreferences.cfg`, section `Hash`, entry `NeedleUsageCapture`, default `false`. While it is on,
`Terminal/UsageCapture.cs` appends one JSON object per `# ` request to `UserData/Hash/queries.jsonl`:

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

## How it feeds back

Two consumers, and the first matters more:

**Benchmark.** `NeedleBenchmark/cases.json` currently holds 79 hand-written cases. Corrected and rejected
lines are cases with a known right answer; append them. At a few hundred, differences that are currently
noise become measurable, and `benchmark_human_cases.py` needs no change - it reads that file.

**Corpus.** Accepted queries are phrasings players actually use, which is exactly what
`generate_data.py` cannot invent. They can join the training rows directly, with the same
`route_tool`/`refinement_tool` rendering everything else uses. `grounds_literals` no longer demands that
every argument appear verbatim, so a real phrasing is admissible as it stands - but it is still checked
against the enum ranking the mod uses, and a query whose argument nothing resolves is still dropped.

## Boundaries

- Opt-in, default off. Not opt-out.
- Written locally only. The mod never uploads anything; sharing is the player choosing to send a file.
- No identifiers of any kind.
- The player can delete the file at any time and it is recreated empty.
- Worth stating in the release notes, not just the setting.
