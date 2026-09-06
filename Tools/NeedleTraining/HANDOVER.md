# Training Needle for hash - handover

## Status after the first full pass (2026-09-06)

An adapter is trained and shipped. `Hash.Needle.cact` is committed at the repo root and the release
workflow packages it into all three archives - unlike `Hash.Needle.bin`, which is fetched from a published
wheel, this file exists nowhere but here. Until that change every player ran the untuned fallback.

Measured on the 79 hand-written cases in `NeedleBenchmark/cases.json`, scored the way the mod resolves
values (`score_as_mod.py`, mirroring `NeedleArgument.TryResolveText`):

| | end-to-end | route | arguments |
|---|---|---|---|
| no adapter | 10/79 | 7.6 % | 27/73 |
| shipped adapter | **23/79** | **78.5 %** | 28/73 |

The gate on the generated holdout is 73.5 % (`results/latest.json`) and does **not** reach the 99 % this
pipeline asks for. Do not read that gate as an in-game figure either way: it scores phrasings written by
the same teacher that wrote the training rows, and it kept climbing (59.5 -> 75.8 %) across runs whose
in-game accuracy did not.

Three things below turned out to be wrong and are corrected in place: the note about the desktop run
stopping because "the machine was busy" (it was unbounded recursion), the assumption that the corpus
renders what the engine reads (it did not, in three ways), and the 99 % gate as a goal.

`RESULTS.md` has the run-by-run record, including the four changes that made it worse.
`TELEMETRY.md` specifies the opt-in usage capture that would fix the measurement problem
underneath all of it - 79 hand-written cases cannot separate the last three runs.

What is left is the argument phase, where the adapter (28/73) is barely ahead of the untuned base
(27/73). Fine-tuning buys routing and costs extraction. The corpus is the reason: `grounds_literals`
forces every numeric argument to appear as digits and every value to appear verbatim, so of 877 numeric
training arguments not one was ever written as a word, while 49 of the 73 benchmark cases with arguments
need exactly that. Fixing it means relaxing that rule and re-running the teacher.

---

You are picking up an unfinished job. hash already ships the natural-language `# ` prefix and the
Cactus Needle 2.0.2 engine; what is missing is the fine-tuned adapter that makes it accurate. Everything
needed to produce that adapter is in this branch. The game is **not** required at any point.

## What you are producing

One file: **`Hash.Needle.cact`**, the quantised export of a LoRA adapter trained on top of the 45M-parameter
`needle2` base model. Dropped beside `Hash.dll` in a game's `Mods` folder, hash loads it automatically and
switches from the base model to the tuned one (`Game/NeedleCommandTranslator.cs:532`).

## Why it is needed

The untuned base model was benchmarked against the live command catalogue on 2026-08-21:

| | value |
|---|---|
| cases | 65 |
| exact matches | 7 |
| pass rate | **10.8 %** |

`Tools/NeedleBenchmark/results/latest.json`. That run needed a running game; the pipeline's own gate is a
different, offline measurement - exact match on the generated holdout split, and it demands **99 %**. The
two numbers are not directly comparable, but a base model that routes one request in ten is the reason
this work exists.

## The shape of the work

```
data      generate_data.py   teacher writes natural phrasings   -> data-production/*.jsonl
training  finetune_hash.py   LoRA on needle2, JAX on CPU        -> artifacts/hash-production.pkl
export    needle build       merge + 4-bit quantise             -> artifacts/hash-production.cact
benchmark NeedleSmoke.exe    exact-match on the holdout split   -> Tools/NeedleBenchmark/results/latest.json
report    render_native_report.py                               -> results/latest.html
```

`run_pipeline.ps1` runs all five, records progress in `artifacts/pipeline/state.json`, and skips any stage
whose output is newer than its inputs. It is safe to re-run after an interruption.

The teacher only ever writes **phrasings**. Tool names, arguments, splits, negatives and answers are
assembled and validated locally, so a teacher hallucination cannot become training ground truth. That is
why a mid-sized local model is good enough here.

## Step 1 - install

```powershell
pwsh -File Tools/NeedleTraining/bootstrap.ps1
```

Creates `.venv`, installs `cactus-needle==2.0.3` plus its JAX stack, downloads the 86 MB `needle2.pkl`
base checkpoint and the tokenizer, fetches `Native/Hash.Needle.bin`, and checks that the exporter and the
engine agree on the `.cact` format. Idempotent; `-Force` rebuilds.

You also need the **.NET 8 SDK** for the benchmark stage (`dotnet --version`).

### Two traps around the version number

Both were hit while preparing this branch. `bootstrap.ps1` now guards against both, but they explain the
pins and the checks it performs.

**The training package is `cactus-needle==2.0.3`, not 2.0.2.** The exporter writes a four-byte format tag
into every `.cact`. PyPI 2.0.0 through 2.0.2 write `0x05E12A82`; 2.0.3 and later write `0x05E12A83`, and
`0x05E12A83` is the only one of the two that appears anywhere in the shipped `Native/Hash.Needle.bin`.
An adapter exported by 2.0.2 therefore dies at load with `needle_load failed with code -1`, hours after the
training that produced it. The checkpoint format is 2 across every release, so `needle2.pkl` is unaffected
by the bump. `bootstrap.ps1` proves the agreement by scanning the engine for the exporter's tag before you
start; if that check ever fails, either pin a version whose `needle/model/export.py` `TAG` matches, or move
the engine forward in `Tools/fetch-needle.ps1`.

**Do not install the Hugging Face wheel into `.venv`.** `Tools/fetch-needle.ps1` downloads
`cactus_needle-2.0.2-py3-none-win_amd64.whl` from Hugging Face, and that wheel is not the PyPI package of
the same version: it is the slim engine build, with no `finetune`, no `build` and no `tokenizer` module.
Installing it over the PyPI package removes the training code and every import then fails with
`unknown location`. The Hugging Face wheel is only ever unzipped for `libneedle.dll`, which is exactly what
`fetch-needle.ps1` does - and that `libneedle.dll` is newer than PyPI 2.0.2 despite the shared version
number, which is the root of the trap above.

## Step 2 - pick the teacher

```powershell
pwsh -File Tools/NeedleTraining/probe_machine.ps1
```

It reads RAM, GPU, free disk and installed tools from the machine, asks the Hugging Face API for each
candidate checkpoint's real download size, and prints one recommendation. Nothing is downloaded.

Two routes exist, and the GPU decides:

**FreeToken** (<https://www.flashml.ai/>, Windows desktop app) serves large MoE checkpoints by keeping the
experts in host RAM, so system RAM is the binding limit rather than VRAM. It supports **NVIDIA RTX 30/40/50
only**. `ft serve --model <hf-id>` listens on `http://127.0.0.1:1919` and speaks the OpenAI API at
`/v1/chat/completions`.

**Ollama** runs anywhere, on any GPU or on the CPU. `ollama pull qwen3:8b`. This is the route the corpus
was originally designed against.

The difference that matters: **FreeToken has no constrained decoding.** It answers `response_format`
`json_object`/`json_schema` with an error
([`openai_api.py:159`](https://github.com/FlashML-org/FreeToken/blob/main/python/freetoken/server/openai_api.py)),
so `--api openai` states the schema in the prompt and parses the reply defensively - fenced blocks, a
leading sentence and a trailing note are all recovered. A reply that still cannot be parsed counts as one
of three attempts and is retried. Ollama's `--api ollama` path keeps the hard schema and is therefore the
lower-risk route; FreeToken buys a much stronger teacher.

## Step 3 - run the pipeline

Ollama:

```powershell
ollama pull qwen3:8b
pwsh -File Tools/NeedleTraining/run_pipeline.ps1
```

FreeToken (start `ft serve` first and wait for `API server is ready to serve on 127.0.0.1:1919`):

```powershell
pwsh -File Tools/NeedleTraining/run_pipeline.ps1 `
  -TeacherApi openai `
  -TeacherBaseUrl http://127.0.0.1:1919 `
  -TeacherModel <the id from /v1/models>
```

Confirm the served id first - it is the basename of `--model`, not the full repo path:

```powershell
curl http://127.0.0.1:1919/v1/models
```

Run it detached and read the log rather than blocking a terminal for hours:

```powershell
Start-Process pwsh -ArgumentList '-NoProfile','-File','Tools/NeedleTraining/run_pipeline.ps1' `
  -RedirectStandardOutput Tools/NeedleTraining/artifacts/pipeline/background.stdout.log `
  -RedirectStandardError  Tools/NeedleTraining/artifacts/pipeline/background.stderr.log
Get-Content Tools/NeedleTraining/artifacts/pipeline/state.json | ConvertFrom-Json
```

### Cost, measured not guessed

- **Data.** The snapshot holds 123 commands, of which `--exclude-external-dev-sources` (the pipeline's
  default) keeps **78** - it drops the 45 commands that came from an unreleased BreedToSeed dev build, so
  the adapter is not taught commands no player has. 78 commands x 4 languages x 17 variants, batched one
  command per teacher call, is 78 calls of 68 phrasings each, plus the feature and off-topic batches, and
  yields roughly 9,400 training rows. Replies are cached by content hash under
  `data-smoke8/teacher-cache`, so a re-run after a crash resumes rather than repeats. The cache key
  includes the teacher model name: changing the model invalidates every entry.
- **Training.** JAX runs on the **CPU** in this environment (`jax.default_backend()` is `cpu`). Measured on
  a Ryzen 9 7900X with a warm `artifacts/jax-cache`: **640 rows, one epoch, 440 s - 0.69 s per row.** At
  9,400 rows that is about 1 h 50 per epoch, so the default three epochs land near **5.5 hours on a
  12-core desktop**. A laptop CPU will be slower; plan an overnight run and start it detached.
  The very first run pays JIT compilation on top (the first probe here spent most of 340 s on 166 rows
  compiling); those kernels persist in `artifacts/jax-cache` and are not paid again.

If the full corpus turns out to be too slow on your machine, lower `-PerLanguage` (17 -> 10) before you
lower `-Epochs`. Fewer phrasings per command costs coverage linearly; fewer epochs can leave the adapter
undertrained and is harder to diagnose.

### If it stops

`artifacts/pipeline/state.json` names the stage and the error. The desktop run that appeared to stop in
the `data` stage with `status: running` was not a busy machine: `teacher_batch` set `allow_alternatives`
but never passed it through its two split branches, so a single row cycled through
`-alternatives-variation-N` forever. A Windows path limit ended it; on Linux nothing would have. Fixed,
with `test_teacher_parsing.py::test_recursion_terminates` pinning it.

- `Required pipeline input is missing` - run `bootstrap.ps1`.
- The data stage hangs - the teacher server is not answering. Check `curl <base-url>/v1/models` or
  `ollama ps`.
- `response_format json_object/json_schema is not supported` - you are pointing `--api ollama` at FreeToken.
- `ambiguous duplicate teacher query` - no longer fatal. "Please enable terrain" is a real collision
  between `enable terrain` and `enableterrain`; the first mapping wins, the later row is dropped and
  counted. One occurrence in the whole corpus.
- `needle_load failed with code -1` - the exporter and the engine disagree on the `.cact` format. Re-run
  `bootstrap.ps1`; its export-format check names the mismatch.
- Accuracy below the gate - the pipeline writes `artifacts/pipeline/state.json` with
  `status: needs-iteration` and keeps the HTML report. Read
  `Tools/NeedleBenchmark/results/latest.html`; it lists every failed case with the proposed command.

## Step 4 - hand the result back

The gate passes at >= 99 % exact match on the holdout split. Then:

```powershell
Copy-Item Tools/NeedleTraining/artifacts/hash-production.cact <game>/Mods/Hash.Needle.cact
```

Report back with:

- `Tools/NeedleBenchmark/results/latest.json` (accuracy, exact, cases)
- `Tools/NeedleBenchmark/results/latest.html`
- `artifacts/hash-production-training.json` (loss history, epochs, seconds)
- `artifacts/hash-production.cact` itself

Commit the artifacts you want to keep on this branch. `artifacts/`, `checkpoints/`, `.venv/`,
`data-production/` and the teacher caches are gitignored on purpose - they are large and reproducible - so
attach the `.cact` and the two reports rather than assuming a `git add` captured them.

## What is deliberately not here

- **The game.** `pull_schema.py` refreshes `data/*.json` from a running instance over the mod bridge. That
  snapshot is already committed (123 commands, taken 2026-08-21, fingerprint in `data/catalogue.json`), so
  the whole pipeline runs offline. Only re-run `pull_schema.py` after a game update changes the catalogue.
- **`rtk`.** The pipeline used to require it. It now runs each stage command directly when `rtk` is not on
  PATH and says so once at startup.
- **A GPU trainer.** `cactus-needle[gpu]` pulls `jax[cuda12]` and would need an NVIDIA card. Worth trying if
  the CPU run is unbearable, but it is untested here and the adapter format is what matters, not the device.
