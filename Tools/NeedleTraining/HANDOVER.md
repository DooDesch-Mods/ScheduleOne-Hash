# Training Needle for hash - handover

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

`Tools/NeedleBenchmark/results/latest.json`. The pipeline's release gate is 99 %. The gap between those
two numbers is this task.

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

Creates `.venv`, installs `cactus-needle==2.0.2` plus its JAX stack, downloads the 86 MB `needle2.pkl`
base checkpoint and the tokenizer, and fetches `Native/Hash.Needle.bin`. Idempotent; `-Force` rebuilds.

You also need the **.NET 8 SDK** for the benchmark stage (`dotnet --version`).

> **Do not install the Hugging Face wheel into `.venv`.** `Tools/fetch-needle.ps1` downloads
> `cactus_needle-2.0.2-py3-none-win_amd64.whl` from Hugging Face, and despite the identical version number
> that wheel is the slim engine build: no `finetune`, no `build`, no `tokenizer` module. Installing it over
> the PyPI package removes the training code and every import then fails with `unknown location`. The
> Hugging Face wheel is only ever unzipped for `libneedle.dll`, which is what `fetch-needle.ps1` does.

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

- **Data.** 123 commands x 4 languages x 17 variants, batched one command per teacher call: about 123 calls
  for the commands plus feature and off-topic batches, each asking for 68 phrasings. Replies are cached by
  content hash under `data-smoke8/teacher-cache`, so a re-run after a crash resumes rather than repeats.
  The cache key includes the teacher model name: changing the model invalidates every entry.
- **Training.** JAX runs on the **CPU** in this environment (`jax.default_backend()` is `cpu`). A measured
  probe on a Ryzen 9 7900X took 340 s for 166 rows over one epoch, most of it JIT compilation; the compiled
  kernels persist in `artifacts/jax-cache`, so later epochs are far cheaper than that first number suggests.
  Expect hours, not minutes, and plan for an overnight run.

If the full corpus turns out to be too slow on your machine, lower `-PerLanguage` (17 -> 10) before you
lower `-Epochs`. Fewer phrasings per command costs coverage linearly; fewer epochs can leave the adapter
undertrained and is harder to diagnose.

### If it stops

`artifacts/pipeline/state.json` names the stage and the error. The last run on the desktop stopped in the
`data` stage on 2026-08-21 with `status: running` - the machine was busy, not the code.

- `Required pipeline input is missing` - run `bootstrap.ps1`.
- The data stage hangs - the teacher server is not answering. Check `curl <base-url>/v1/models` or
  `ollama ps`.
- `response_format json_object/json_schema is not supported` - you are pointing `--api ollama` at FreeToken.
- `ambiguous duplicate teacher query` - two rows in the same split got identical phrasings with different
  arguments. Delete the offending cache entry under `data-smoke8/teacher-cache` and re-run the data stage.
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
