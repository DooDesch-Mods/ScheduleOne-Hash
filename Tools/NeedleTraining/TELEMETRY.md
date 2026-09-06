# Task: opt-in usage capture

**Status:** specified, not built. This is the job for whoever picks it up next.

## Why this is worth more than another training run

`RESULTS.md` records eight runs. The last three landed at 14, 16 and 18 end-to-end cases out of 79 - and
79 cases is too small to tell those apart. Every further change is being judged on differences that are
inside the noise. A few hundred real queries fix that before they train anything.

The training case is the same finding from the other side. The corpus is written by a teacher model told
to phrase requests; `RESULTS.md` shows where that distribution stops matching players. Real queries remove
the guess instead of improving it.

## What already exists

`Game/NeedleCommandTranslator.cs:268` already serialises exactly the right event:

```csharp
Core.Log?.Msg("Hash Needle event " + JsonSerializer.Serialize(new
{
    type = "query", query = work.Text, commands = result.Commands,
    confidence = result.Confidence, error = result.Error,
    proven = result.Proven, constrained, elapsedMs = inferenceMs,
}));
```

It is inside `#if DEBUG`, so a release build writes only "Hash translated in N ms." Nothing else in the
mod records what a player typed.

## What to build

**1. An explicit opt-in, default off.** A setting the player turns on, not a flag they have to discover to
turn off. Nothing is written until they do.

**2. Its own file, not the MelonLoader log.** `UserData/Hash/queries.jsonl`, one JSON object per line.
The MelonLoader log carries every other mod's output and whatever those mods print; asking a player to
send it means asking for things neither of us wants. A file whose whole content is their own queries is
one they can open and read before sending.

**3. Fields.** The event above is close to right. Drop `elapsedMs` (not useful here), keep the rest, and
add the two below. No player id, no session id, no save name, no timestamps finer than the day - none of
it helps the model and all of it makes the file harder to hand over.

**4. The correction signal - this is the valuable part.** A raw query has no ground truth; someone would
have to label every line by hand. What the mod can observe for free is whether its answer was right:

- the console rejected the command it produced (an error came back),
- the player typed a different command themselves within the next few seconds,
- the player re-phrased and asked again immediately.

Each of those marks a line as a failure and, in the second case, supplies the correct answer. A file of
labelled failures is worth more than ten times as many unlabelled queries. If only one thing from this
document gets built, build this.

Suggested shape:

```json
{"query": "gib mir 10 og kush", "commands": ["give ogkush 10"], "confidence": 0.98,
 "proven": true, "constrained": false, "outcome": "accepted"}
{"query": "bring mich nach hause", "commands": ["teleport mayor"], "confidence": 1.0,
 "proven": false, "constrained": false, "outcome": "corrected", "actual": "teleport #home"}
```

`outcome` is one of `accepted`, `rejected` (console error), `corrected` (player ran something else),
`retried` (player rephrased). `actual` only appears for `corrected`.

**5. Say what is recorded, in the opt-in text.** "Hash writes the requests you type into
`UserData/Hash/queries.jsonl` so they can be used to improve it. Nothing is sent anywhere - the file stays
on your machine until you choose to share it."

## How it feeds back

Two consumers, and the first matters more:

**Benchmark.** `NeedleBenchmark/cases.json` currently holds 79 hand-written cases. Corrected and rejected
lines are cases with a known right answer; append them. At a few hundred, differences that are currently
noise become measurable, and `benchmark_human_cases.py` needs no change - it reads that file.

**Corpus.** Accepted queries are phrasings players actually use, which is exactly what
`generate_data.py` cannot invent. They can join the training rows directly, with the same
`route_tool`/`refinement_tool` rendering everything else uses. Note the constraint in `RESULTS.md`:
`grounds_literals` currently forces every argument to appear verbatim, which is why the corpus has no
spelled-out numbers. Real queries are the reason to relax it, not the other way round.

## Boundaries

- Opt-in, default off. Not opt-out.
- Written locally only. The mod never uploads anything; sharing is the player choosing to send a file.
- No identifiers of any kind.
- The player can delete the file at any time and it is recreated empty.
- Worth stating in the release notes, not just the setting.
