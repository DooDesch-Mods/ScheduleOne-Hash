param(
    [string]$Bridge = "http://127.0.0.1:6139",
    [string]$Cases = "$PSScriptRoot\cases.json",
    [string]$Output = "$PSScriptRoot\results\latest.json",
    [string]$HtmlOutput = "",
    [string]$CaseId = "",
    [int]$PollTimeoutMs = 30000,
    [int]$PollIntervalMs = 75
)

$ErrorActionPreference = "Stop"

function Invoke-Bridge([string]$Command, [hashtable]$Arguments, [int]$TimeoutMs = 5000) {
    $body = @{
        id = [guid]::NewGuid().ToString("N")
        command = $Command
        args = $Arguments
        timeoutMs = $TimeoutMs
    } | ConvertTo-Json -Depth 20 -Compress

    $reply = Invoke-RestMethod -Method Post -Uri ($Bridge.TrimEnd('/') + "/command") `
        -ContentType "application/json" -Body $body -TimeoutSec ([math]::Ceiling($TimeoutMs / 1000) + 5)
    if (-not $reply.ok) {
        throw "$Command failed: $($reply.error.code): $($reply.error.message)"
    }
    return $reply.result
}

function Convert-HostJson([object]$Value) {
    $current = $Value
    for ($i = 0; $i -lt 2; $i++) {
        if ($current -isnot [string]) { return $current }
        try { $current = $current | ConvertFrom-Json }
        catch { return $current }
    }
    return $current
}

function Invoke-Hash([string]$Handler, [string]$Argument) {
    $handlerJson = $Handler | ConvertTo-Json -Compress
    $argumentJson = $Argument | ConvertTo-Json -Compress
    $probe = Invoke-Bridge "sideload_eval" @{
        appId = "hash"
        code = "s1.call($handlerJson,$argumentJson)"
    }
    if ($probe.failed) { throw "Hash probe failed: $($probe.value)" }
    return Convert-HostJson $probe.value
}

function Invoke-Diagnostic([string]$StartHandler, [string]$Payload) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $started = Invoke-Hash $StartHandler $Payload
    if (-not $started.started) { throw "diagnostic did not start: $($started.error)" }

    while ($clock.ElapsedMilliseconds -lt $PollTimeoutMs) {
        $result = Invoke-Hash "needle-diagnose-poll" ""
        if ($result.ready) {
            $clock.Stop()
            return [ordered]@{
                commands = @($result.commands)
                confidence = $result.confidence
                error = [string]$result.error
                reasoning = [string]$result.reasoning
                prefillTps = $result.prefillTps
                decodeTps = $result.decodeTps
                peakRamMb = $result.peakRamMb
                elapsedMs = $clock.ElapsedMilliseconds
            }
        }
        if (-not $result.pending) { throw "diagnostic stopped without a result" }
        Start-Sleep -Milliseconds $PollIntervalMs
    }
    throw "diagnostic timed out after $PollTimeoutMs ms"
}

function Normalize-Command([string]$Command) {
    if ($null -eq $Command) { return "" }
    return (($Command.Trim() -replace '\s+', ' ').ToLowerInvariant())
}

function Is-Exact([object[]]$Commands, [string]$Expected) {
    if ((Normalize-Command $Expected).Length -eq 0) { return $Commands.Count -eq 0 }
    return $Commands.Count -eq 1 -and (Normalize-Command $Commands[0]) -eq (Normalize-Command $Expected)
}

function Candidate-Word([object[]]$Commands) {
    if ($Commands.Count -eq 0) { return "" }
    $match = [regex]::Match([string]$Commands[0], '^\s*([^\s]+)')
    return $(if ($match.Success) { $match.Groups[1].Value.Trim('"') } else { "" })
}

function Write-HtmlReport([object]$Report, [string]$Path) {
    $reportJson = $Report | ConvertTo-Json -Depth 30 -Compress
    # Prevent benchmark text from prematurely terminating the embedded script element.
    $reportJson = $reportJson.Replace('</', '<\/')
    $template = @'
<!doctype html>
<html lang="de">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Hash Needle Benchmark</title>
  <style>
    :root { color-scheme: dark; --bg:#0d1411; --panel:#15201b; --line:#2b4237; --text:#e8f4ed; --muted:#91aa9d; --good:#55d68b; --bad:#ff7d78; --warn:#f6c66b; --accent:#84bfa1; }
    * { box-sizing:border-box; }
    body { margin:0; background:var(--bg); color:var(--text); font:14px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif; }
    main { width:min(1600px,calc(100% - 32px)); margin:28px auto 60px; }
    h1 { margin:0; font-size:28px; letter-spacing:-.02em; }
    .sub { color:var(--muted); margin:5px 0 24px; }
    .cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(155px,1fr)); gap:10px; margin-bottom:18px; }
    .card,.panel { background:var(--panel); border:1px solid var(--line); border-radius:10px; }
    .card { padding:14px 16px; }
    .card span { color:var(--muted); display:block; font-size:12px; text-transform:uppercase; letter-spacing:.07em; }
    .card strong { display:block; font-size:25px; margin-top:4px; }
    .good { color:var(--good); } .bad { color:var(--bad); } .warn { color:var(--warn); }
    .breakdowns { display:grid; grid-template-columns:repeat(auto-fit,minmax(280px,1fr)); gap:10px; margin-bottom:18px; }
    .panel { padding:14px 16px; overflow:hidden; }
    .panel h2 { font-size:14px; margin:0 0 10px; color:var(--muted); text-transform:uppercase; letter-spacing:.06em; }
    .bar-row { display:grid; grid-template-columns:minmax(90px,1fr) 3fr 60px; gap:10px; align-items:center; margin:7px 0; }
    .bar { height:8px; background:#26392f; border-radius:99px; overflow:hidden; }
    .bar i { display:block; height:100%; background:var(--good); }
    .bar-row b { text-align:right; font-variant-numeric:tabular-nums; }
    .filters { display:grid; grid-template-columns:minmax(220px,2fr) repeat(3,minmax(130px,1fr)); gap:10px; margin-bottom:10px; }
    input,select { width:100%; color:var(--text); background:#101a15; border:1px solid var(--line); border-radius:7px; padding:9px 10px; }
    .table-wrap { overflow:auto; border:1px solid var(--line); border-radius:10px; }
    table { width:100%; border-collapse:collapse; min-width:1100px; background:var(--panel); }
    th { color:var(--muted); background:#101a15; position:sticky; top:0; text-align:left; font-size:12px; text-transform:uppercase; letter-spacing:.05em; }
    th,td { padding:10px 11px; border-bottom:1px solid #22342b; vertical-align:top; }
    tr:last-child td { border-bottom:0; }
    code { color:#d5f0df; background:#0f1814; padding:2px 5px; border-radius:4px; white-space:pre-wrap; }
    .pill { display:inline-block; border:1px solid currentColor; border-radius:99px; padding:1px 7px; font-size:12px; }
    details { min-width:280px; } summary { color:var(--accent); cursor:pointer; }
    .stage { margin:7px 0 0 12px; color:var(--muted); }
    .empty { padding:28px; color:var(--muted); text-align:center; }
    footer { color:var(--muted); margin-top:12px; font-size:12px; }
    @media (max-width:800px) { main { width:min(100% - 18px,1600px); } .filters { grid-template-columns:1fr 1fr; } }
  </style>
</head>
<body><main>
  <h1>Hash Needle Benchmark</h1>
  <p class="sub" id="meta"></p>
  <section class="cards" id="cards"></section>
  <section class="breakdowns"><div class="panel"><h2>Nach Sprache</h2><div id="languages"></div></div><div class="panel"><h2>Nach Kategorie</h2><div id="categories"></div></div></section>
  <section class="filters">
    <input id="search" type="search" placeholder="ID, Eingabe, Soll- oder Ist-Command filtern">
    <select id="status"><option value="">Alle Ergebnisse</option><option value="perfect">Perfekt</option><option value="unresolved">Nicht gelöst</option><option value="no-route">Keine Route</option><option value="error">Fehler</option></select>
    <select id="language"><option value="">Alle Sprachen</option></select>
    <select id="category"><option value="">Alle Kategorien</option></select>
  </section>
  <div class="table-wrap"><table><thead><tr><th>ID</th><th>Sprache</th><th>Kategorie</th><th>Eingabe</th><th>Erwartet</th><th>Ergebnis</th><th>Passes</th><th>Gesamt</th></tr></thead><tbody id="rows"></tbody></table><div class="empty" id="empty" hidden>Keine passenden Fälle.</div></div>
  <footer>Read-only Diagnose: Kein vorgeschlagener Konsolen-Command wurde ausgeführt. Erfolg bedeutet exakt normalisierte Übereinstimmung mit dem Gold-Command.</footer>
</main>
<script>
const report = __REPORT_JSON__;
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const pct = value => `${(Number(value || 0) * 100).toLocaleString('de-DE',{maximumFractionDigits:1})}%`;
const results = report.results || [];
const stagesText = item => (item.stages || []).flatMap(s => (s.result?.commands || [])).join(' ');
const group = key => Object.values(results.reduce((all,item) => { const name=item[key] || '–'; all[name] ||= {name,total:0,perfect:0}; all[name].total++; if(item.status==='perfect') all[name].perfect++; return all; },{})).sort((a,b) => a.name.localeCompare(b.name));
const bars = values => values.map(x => `<div class="bar-row"><span>${esc(x.name)}</span><div class="bar"><i style="width:${100*x.perfect/Math.max(1,x.total)}%"></i></div><b>${x.perfect}/${x.total}</b></div>`).join('');
document.getElementById('meta').textContent = `${new Date(report.generatedAtUtc).toLocaleString('de-DE')} · ${report.bridge} · ${report.cases} Fälle`;
document.getElementById('cards').innerHTML = [
  ['Exakt perfekt',`${report.perfect}/${report.cases}`,report.passRate === 1 ? 'good' : 'warn'],
  ['Trefferquote',pct(report.passRate),report.passRate === 1 ? 'good' : 'warn'],
  ['Direkt in Pass 1',report.perfectOnRouting,''],
  ['Perfekt nach Pass 2',report.perfectAfterSelectedCommand,''],
  ['Perfekt nach Pass 3',report.perfectAfterRequiredArguments,''],
  ['Nicht gelöst',report.unresolved,report.unresolved ? 'bad' : 'good'],
  ['Fehler',report.errors,report.errors ? 'bad' : 'good'],
  ['Median pro Fall',`${report.medianCaseMs} ms`,''],
  ['P95 pro Fall',`${report.p95CaseMs} ms`,'']
].map(([label,value,klass]) => `<div class="card"><span>${label}</span><strong class="${klass}">${value}</strong></div>`).join('');
document.getElementById('languages').innerHTML = bars(group('language'));
document.getElementById('categories').innerHTML = bars(group('category'));
for (const [id,key] of [['language','language'],['category','category']]) {
  const select=document.getElementById(id);
  for (const x of group(key)) select.insertAdjacentHTML('beforeend',`<option value="${esc(x.name)}">${esc(x.name)}</option>`);
}
const stageHtml = item => (item.stages || []).map(stage => {
  const commands=stage.result?.commands || [];
  const actual=commands.length ? commands.join(' | ') : '∅';
  const confidence=stage.result?.confidence;
  const confidenceText=confidence === null || confidence === undefined ? '–' : pct(confidence);
  const error=stage.error || stage.result?.error || '';
  const perf=stage.result?.decodeTps == null ? '' : ` · ${Number(stage.result.decodeTps).toLocaleString('de-DE',{maximumFractionDigits:0})} decode tok/s · ${Number(stage.result.peakRamMb || 0).toLocaleString('de-DE',{maximumFractionDigits:1})} MB`;
  const reasoning=stage.result?.reasoning ? `<div>${esc(stage.result.reasoning)}</div>` : '';
  return `<div class="stage"><b>${stage.iteration}. ${esc(stage.kind)}</b>: <code>${esc(actual)}</code> · Konf. ${confidenceText} · ${stage.result?.elapsedMs ?? '–'} ms${perf}${error ? ` · <span class="bad">${esc(error)}</span>` : ''}${reasoning}</div>`;
}).join('');
function render() {
  const needle=document.getElementById('search').value.trim().toLowerCase();
  const wantedStatus=document.getElementById('status').value;
  const wantedLanguage=document.getElementById('language').value;
  const wantedCategory=document.getElementById('category').value;
  const shown=results.filter(item => (!wantedStatus || item.status===wantedStatus) && (!wantedLanguage || item.language===wantedLanguage) && (!wantedCategory || item.category===wantedCategory) && (!needle || `${item.id} ${item.query} ${item.expected} ${stagesText(item)}`.toLowerCase().includes(needle)));
  document.getElementById('rows').innerHTML=shown.map(item => {
    const perfect=item.status==='perfect';
    const pass=item.firstPerfectIteration ?? 'nie';
    return `<tr><td>${esc(item.id)}</td><td>${esc(item.language)}</td><td>${esc(item.category)}</td><td>${esc(item.query)}</td><td><code>${esc(item.expected || '∅')}</code></td><td><span class="pill ${perfect?'good':'bad'}">${esc(item.status)}</span> · Pass ${pass}</td><td><details><summary>${item.stages?.length || 0} anzeigen</summary>${stageHtml(item)}</details></td><td>${item.totalMs} ms</td></tr>`;
  }).join('');
  document.getElementById('empty').hidden=shown.length !== 0;
}
for (const id of ['search','status','language','category']) document.getElementById(id).addEventListener(id==='search'?'input':'change',render);
render();
</script></body></html>
'@
    $html = $template.Replace('__REPORT_JSON__', $reportJson)
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $html, [Text.UTF8Encoding]::new($false))
}

$health = Invoke-RestMethod -Uri ($Bridge.TrimEnd('/') + "/health") -TimeoutSec 5
if (-not $health.ok) { throw "bridge is not healthy" }

# Eval requires a mounted Hash page. Opening it changes only UI visibility; every benchmark handler remains
# DEBUG-only and isolated from Session, so none of the returned command lines can execute.
try { Invoke-Bridge "sideload_open_app" @{ appId = "hash"; open = $true } | Out-Null }
catch { Write-Warning "Hash app could not be raised; continuing with its mounted diagnostic page: $($_.Exception.Message)" }

$suite = [IO.File]::ReadAllText($Cases) | ConvertFrom-Json
$casesToRun = @($suite.cases)
if ($CaseId.Length -gt 0) {
    $casesToRun = @($casesToRun | Where-Object id -eq $CaseId)
    if ($casesToRun.Count -eq 0) { throw "case not found: $CaseId" }
}
$results = [System.Collections.Generic.List[object]]::new()
$number = 0

foreach ($case in $casesToRun) {
    $number++
    $stages = [System.Collections.Generic.List[object]]::new()
    $firstPerfect = $null
    $candidate = ""
    $status = "unresolved"
    $caseClock = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $routing = Invoke-Diagnostic "needle-diagnose-start" ([string]$case.query)
        $stages.Add([ordered]@{ iteration = 1; kind = "routing"; result = $routing })
        if (Is-Exact $routing.commands ([string]$case.expected)) {
            $firstPerfect = 1
            $status = "perfect"
        } else {
            $candidate = Candidate-Word $routing.commands
        }

        if ($null -eq $firstPerfect -and $candidate.Length -gt 0) {
            $payload = @{ query = [string]$case.query; command = $candidate; strict = $false } |
                ConvertTo-Json -Compress
            $narrow = Invoke-Diagnostic "needle-diagnose-refine-start" $payload
            $stages.Add([ordered]@{ iteration = 2; kind = "selected-command"; result = $narrow })
            if (Is-Exact $narrow.commands ([string]$case.expected)) {
                $firstPerfect = 2
                $status = "perfect"
            }

            if ($null -eq $firstPerfect) {
                $payload = @{ query = [string]$case.query; command = $candidate; strict = $true } |
                    ConvertTo-Json -Compress
                $strict = Invoke-Diagnostic "needle-diagnose-refine-start" $payload
                $stages.Add([ordered]@{ iteration = 3; kind = "required-arguments"; result = $strict })
                if (Is-Exact $strict.commands ([string]$case.expected)) {
                    $firstPerfect = 3
                    $status = "perfect"
                }
            }
        } elseif ($null -eq $firstPerfect) {
            $status = "no-route"
        }
    } catch {
        $status = "error"
        $stages.Add([ordered]@{ iteration = $stages.Count + 1; kind = "harness"; error = $_.Exception.Message })
    }

    $caseClock.Stop()
    $results.Add([ordered]@{
        id = [string]$case.id
        language = [string]$case.language
        category = [string]$case.category
        query = [string]$case.query
        expected = [string]$case.expected
        selectedCommand = $candidate
        status = $status
        firstPerfectIteration = $firstPerfect
        totalMs = $caseClock.ElapsedMilliseconds
        stages = $stages
    })

    $iterationText = $(if ($null -eq $firstPerfect) { "never" } else { [string]$firstPerfect })
    Write-Host ("[{0}/{1}] {2} {3}: iteration {4}" -f $number, $casesToRun.Count,
        $case.language, $case.id, $iterationText)
}

$resultObjects = @($results | ForEach-Object { [pscustomobject]$_ })
$perfect = @($resultObjects | Where-Object status -eq "perfect")
$summaryByLanguage = @($resultObjects | Group-Object language | ForEach-Object {
    $group = @($_.Group)
    [ordered]@{
        language = $_.Name
        cases = $group.Count
        perfect = @($group | Where-Object status -eq "perfect").Count
        passRate = [math]::Round(@($group | Where-Object status -eq "perfect").Count / $group.Count, 4)
    }
})
$summaryByCategory = @($resultObjects | Group-Object category | ForEach-Object {
    $group = @($_.Group)
    [ordered]@{
        category = $_.Name
        cases = $group.Count
        perfect = @($group | Where-Object status -eq "perfect").Count
        passRate = [math]::Round(@($group | Where-Object status -eq "perfect").Count / $group.Count, 4)
    }
})
$durations = @($resultObjects | ForEach-Object { [long]$_.totalMs } | Sort-Object)
$averageCaseMs = $(if ($durations.Count) { [math]::Round(($durations | Measure-Object -Average).Average, 1) } else { 0 })
$medianCaseMs = $(if ($durations.Count -eq 0) { 0 } elseif ($durations.Count % 2) {
    $durations[[math]::Floor($durations.Count / 2)]
} else {
    [math]::Round(($durations[$durations.Count / 2 - 1] + $durations[$durations.Count / 2]) / 2, 1)
})
$p95CaseMs = $(if ($durations.Count) { $durations[[math]::Max(0, [math]::Ceiling($durations.Count * .95) - 1)] } else { 0 })

$report = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    bridge = $Bridge
    cases = $resultObjects.Count
    perfect = $perfect.Count
    passRate = [math]::Round($perfect.Count / [math]::Max(1, $resultObjects.Count), 4)
    perfectOnRouting = @($resultObjects | Where-Object firstPerfectIteration -eq 1).Count
    perfectAfterSelectedCommand = @($resultObjects | Where-Object firstPerfectIteration -eq 2).Count
    perfectAfterRequiredArguments = @($resultObjects | Where-Object firstPerfectIteration -eq 3).Count
    unresolved = @($resultObjects | Where-Object status -in @("unresolved", "no-route")).Count
    errors = @($resultObjects | Where-Object status -eq "error").Count
    averageCaseMs = $averageCaseMs
    medianCaseMs = $medianCaseMs
    p95CaseMs = $p95CaseMs
    byLanguage = $summaryByLanguage
    byCategory = $summaryByCategory
    results = $resultObjects
}

$parent = Split-Path -Parent $Output
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Output -Encoding utf8
if ($HtmlOutput.Length -eq 0) { $HtmlOutput = [IO.Path]::ChangeExtension($Output, ".html") }
Write-HtmlReport $report $HtmlOutput
([pscustomobject]$report) | Select-Object cases, perfect, passRate, perfectOnRouting, perfectAfterSelectedCommand,
    perfectAfterRequiredArguments, unresolved, errors, averageCaseMs, medianCaseMs, p95CaseMs | Format-List
Write-Host "JSON report: $Output"
Write-Host "HTML report: $HtmlOutput"
