#!/usr/bin/env python3
"""Render the native Needle holdout result as a self-contained UTF-8 HTML report."""

from __future__ import annotations

import argparse
import html
import json
import pathlib
import statistics
from collections import defaultdict
from datetime import datetime


def get(value: dict, name: str, default=None):
    return value.get(name, value.get(name[:1].upper() + name[1:], default))


def number(value: float) -> str:
    return f"{value:,.1f}".replace(",", "X").replace(".", ",").replace("X", ".")


def compact_json(value) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=pathlib.Path)
    parser.add_argument("--training", type=pathlib.Path)
    parser.add_argument("--manifest", type=pathlib.Path)
    parser.add_argument("--out", type=pathlib.Path, required=True)
    args = parser.parse_args()

    report = json.loads(args.report.read_text(encoding="utf-8-sig"))
    training = (json.loads(args.training.read_text(encoding="utf-8-sig"))
                if args.training and args.training.exists() else {})
    manifest = (json.loads(args.manifest.read_text(encoding="utf-8-sig"))
                if args.manifest and args.manifest.exists() else {})
    results = get(report, "results", []) or []
    cases = int(get(report, "cases", len(results)) or 0)
    exact = int(get(report, "exact", sum(bool(get(row, "exact", False)) for row in results)) or 0)
    accuracy = float(get(report, "accuracy", exact / cases if cases else 0.0) or 0.0)
    failures = cases - exact

    grouped: dict[str, dict[str, list[bool]]] = {
        "Sprache": defaultdict(list),
        "Phase": defaultdict(list),
        "Command": defaultdict(list),
    }
    for row in results:
        ok = bool(get(row, "exact", False))
        grouped["Sprache"][str(get(row, "language", "unbekannt") or "unbekannt")].append(ok)
        grouped["Phase"][str(get(row, "phase", "unbekannt") or "unbekannt")].append(ok)
        grouped["Command"][str(get(row, "command", "unbekannt") or "unbekannt")].append(ok)

    breakdown_sections = []
    for title, groups in grouped.items():
        rows = []
        for label, values in sorted(groups.items(), key=lambda item: (sum(item[1]) / len(item[1]), item[0])):
            passed = sum(values)
            rate = passed / len(values) if values else 0
            rows.append(
                f"<tr><td>{html.escape(label)}</td><td>{passed}/{len(values)}</td>"
                f"<td>{rate:.1%}</td></tr>"
            )
        breakdown_sections.append(
            f"<section class='panel'><h2>{title}</h2><table><thead><tr><th>Gruppe</th>"
            f"<th>Exakt</th><th>Quote</th></tr></thead><tbody>{''.join(rows)}</tbody></table></section>"
        )

    history_rows = []
    for epoch in get(training, "history", []) or []:
        history_rows.append(
            "<tr>"
            f"<td>{get(epoch, 'epoch', '')}</td>"
            f"<td>{float(get(epoch, 'trainLoss', 0)):.5f}</td>"
            f"<td>{float(get(epoch, 'validationLoss', 0)):.5f}</td>"
            f"<td>{number(float(get(epoch, 'seconds', 0)))} s</td>"
            "</tr>"
        )
    history = ("<section class='panel'><h2>Training</h2><table><thead><tr><th>Epoch</th>"
               "<th>Train Loss</th><th>Validation Loss</th><th>Dauer</th></tr></thead><tbody>"
               + "".join(history_rows) + "</tbody></table></section>") if history_rows else ""

    result_rows = []
    latencies = []
    for row in results:
        ok = bool(get(row, "exact", False))
        latency = float(get(row, "inferenceMs", 0) or 0)
        latencies.append(latency)
        expected = compact_json(get(row, "expected", []))
        actual = compact_json(get(row, "actual", []))
        searchable = " ".join((str(get(row, "id", "")), str(get(row, "command", "")),
                               str(get(row, "language", "")), str(get(row, "query", "")),
                               expected, actual)).casefold()
        result_rows.append(
            f"<tr data-ok='{str(ok).lower()}' data-search='{html.escape(searchable, quote=True)}'>"
            f"<td><span class='status {'pass' if ok else 'fail'}'>{'PASS' if ok else 'FAIL'}</span></td>"
            f"<td>{html.escape(str(get(row, 'id', '')))}</td>"
            f"<td>{html.escape(str(get(row, 'language', '')))}</td>"
            f"<td>{html.escape(str(get(row, 'phase', '')))}</td>"
            f"<td>{html.escape(str(get(row, 'command', '')))}</td>"
            f"<td class='query'>{html.escape(str(get(row, 'query', '')))}</td>"
            f"<td><code>{html.escape(expected)}</code></td>"
            f"<td><code>{html.escape(actual)}</code></td>"
            f"<td>{number(latency)} ms</td></tr>"
        )

    median = statistics.median(latencies) if latencies else 0.0
    generated = str(get(report, "generatedAtUtc", datetime.utcnow().isoformat() + "Z"))
    model = html.escape(str(manifest.get("model", "unbekannt")))
    command_count = int(manifest.get("commands", 0) or 0)
    best_loss = get(training, "bestValidationLoss", None)
    best_loss_text = f"{float(best_loss):.5f}" if best_loss is not None else "–"

    document = f"""<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Hash · Needle Benchmark</title>
<style>
:root {{ color-scheme: dark; --bg:#0a0d12; --panel:#111722; --line:#263145; --text:#eef4ff;
  --muted:#96a5bd; --accent:#61e8a7; --danger:#ff6b7a; --amber:#ffc857; }}
* {{ box-sizing:border-box }} body {{ margin:0; background:radial-gradient(circle at 15% 0,#16263a 0,
  var(--bg) 38rem); color:var(--text); font:14px/1.45 Inter,Segoe UI,system-ui,sans-serif }}
main {{ max-width:1600px; margin:auto; padding:36px 24px 72px }}
h1 {{ font-size:34px; margin:0 0 5px }} h2 {{ font-size:17px; margin:0 0 14px }}
.muted {{ color:var(--muted) }} .cards {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(170px,1fr));
  gap:12px; margin:24px 0 }} .card,.panel {{ background:rgba(17,23,34,.94); border:1px solid var(--line);
  border-radius:12px; box-shadow:0 14px 40px #0004 }} .card {{ padding:16px }} .card b {{ display:block;
  font-size:25px; margin-top:5px }} .good {{ color:var(--accent) }} .bad {{ color:var(--danger) }}
.grid {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(300px,1fr)); gap:12px; margin-bottom:12px }}
.panel {{ padding:17px; overflow:auto; margin-bottom:12px }} table {{ width:100%; border-collapse:collapse }}
th,td {{ border-bottom:1px solid #202a3a; padding:9px 10px; text-align:left; vertical-align:top }}
th {{ color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.05em }}
code {{ color:#d5e7ff; white-space:pre-wrap; overflow-wrap:anywhere; font:12px/1.35 Consolas,monospace }}
.status {{ font-weight:800; font-size:11px; letter-spacing:.05em }} .pass {{ color:var(--accent) }}
.fail {{ color:var(--danger) }} .toolbar {{ display:flex; gap:10px; flex-wrap:wrap; margin:0 0 14px }}
input,select {{ background:#0b111b; color:var(--text); border:1px solid var(--line); border-radius:8px;
  padding:9px 11px }} input {{ min-width:290px; flex:1 }} .results {{ font-size:12px }} .query {{ min-width:230px }}
.hidden {{ display:none }}
</style>
</head>
<body><main>
<h1>Hash · Needle Benchmark</h1>
<div class="muted">Native `.cact`-Ausführung · {html.escape(generated)} · Teacher {model}</div>
<div class="cards">
  <div class="card">Exakt korrekt<b class="{'good' if failures == 0 else 'bad'}">{exact}/{cases}</b></div>
  <div class="card">Exact-Match-Quote<b class="{'good' if failures == 0 else 'bad'}">{accuracy:.2%}</b></div>
  <div class="card">Fehler<b class="{'good' if failures == 0 else 'bad'}">{failures}</b></div>
  <div class="card">Median Inferenz<b>{number(float(get(report, 'medianInferenceMs', median) or median))} ms</b></div>
  <div class="card">P95 Inferenz<b>{number(float(get(report, 'p95InferenceMs', 0) or 0))} ms</b></div>
  <div class="card">Commands im Corpus<b>{command_count}</b></div>
  <div class="card">Beste Validation Loss<b>{best_loss_text}</b></div>
</div>
<div class="grid">{''.join(breakdown_sections[:2])}</div>
{history}
{breakdown_sections[2]}
<section class="panel">
  <h2>Alle {cases} Holdout-Fälle</h2>
  <div class="toolbar"><input id="search" placeholder="ID, Sprache, Command oder Text filtern">
    <select id="status"><option value="all">Alle Resultate</option><option value="false">Nur Fehler</option>
      <option value="true">Nur Treffer</option></select><span id="visible" class="muted"></span></div>
  <table class="results"><thead><tr><th>Status</th><th>ID</th><th>Sprache</th><th>Phase</th><th>Command</th>
    <th>Anfrage</th><th>Erwartet</th><th>Tatsächlich</th><th>Latenz</th></tr></thead>
    <tbody id="rows">{''.join(result_rows)}</tbody></table>
</section>
</main>
<script>
const search=document.getElementById('search'), status=document.getElementById('status');
function filter() {{ const q=search.value.trim().toLocaleLowerCase(); let count=0;
  document.querySelectorAll('#rows tr').forEach(row=>{{ const show=(status.value==='all'||row.dataset.ok===status.value)
    && (!q||row.dataset.search.includes(q)); row.classList.toggle('hidden',!show); if(show) count++; }});
  document.getElementById('visible').textContent=`${{count}} sichtbar`; }}
search.addEventListener('input',filter); status.addEventListener('change',filter); filter();
</script></body></html>"""
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(document, encoding="utf-8", newline="\n")
    print(f"HTML report: {args.out} ({exact}/{cases} exact)")


if __name__ == "__main__":
    main()
