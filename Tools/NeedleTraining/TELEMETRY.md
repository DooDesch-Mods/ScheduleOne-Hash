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

- Recording is unconditional and local. Sharing is opt-in, default off. Not opt-out.
- Nothing is uploaded until the player switches sharing on, and the file is cleared only after the server
  confirms it has the records.
- No identifiers of any kind, and no timestamp finer than the day the server files it under.
- The player can delete the file at any time and it is recreated empty.
- Worth stating in the release notes, not just the setting.
