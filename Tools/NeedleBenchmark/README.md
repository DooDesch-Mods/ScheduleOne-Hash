# Hash Needle benchmark

This is a read-only, live-catalogue benchmark for Hash's natural-command translator. It never enters `Session`
and never submits a console command.

Run it against the dedicated Debug test slot after the world and Hash app are available:

```powershell
.\Hash\Tools\NeedleBenchmark\benchmark.ps1 -Bridge http://127.0.0.1:6139
```

Each case gets at most three meaningful deterministic passes:

1. the complete live command catalogue as routing-only tools (real names/descriptions, no arguments);
2. only the command selected by pass 1, with current provider values embedded as enums;
3. the same selected command with all arguments required.

The expected command is used only for scoring. It is never sent to Needle and never changes the selected command.
Consequently, a wrong or empty first-pass route remains a visible failure instead of becoming an oracle-assisted pass.

The JSON report records every proposed command, base-model confidence, error, pass latency, the first exact pass,
and summaries by language. Fine-tuned weights return no confidence by design; exact command accuracy remains the
success metric.

Every run also writes a standalone `results/latest.html`. It contains aggregate cards, language/category pass-rate
charts, latency metrics, and a searchable/filterable table with the expected command and every inference pass. It
has no external dependencies and can be opened directly in a browser.

Use `-CaseId give-speedgrow-en-1` for a smoke test, or override `-Output` / `-HtmlOutput` when comparing runs.
