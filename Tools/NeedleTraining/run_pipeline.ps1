[CmdletBinding()]
param(
    [ValidateRange(1, 50)][int]$Epochs = 3,
    [ValidateRange(1, 20)][int]$Patience = 1,
    [ValidateSet(4, 8)][int]$BatchSize = 8,
    [ValidateRange(5, 100)][int]$PerLanguage = 17,
    [ValidateRange(1, 20)][int]$EvalPerLanguage = 2,
    [ValidateRange(0.0, 1.0)][double]$MinimumAccuracy = 0.99,
    [ValidateSet("ollama", "openai")][string]$TeacherApi = "ollama",
    [string]$TeacherBaseUrl = "",
    [string]$TeacherModel = "qwen3:8b",
    [string]$FallbackTeacherModel = "",
    [switch]$IncludeExternalDevSources,
    [switch]$ForceData,
    [switch]$ForceTrain,
    [switch]$ForceExport,
    [switch]$ForceBenchmark
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$TrainingRoot = $PSScriptRoot
$HashRoot = (Resolve-Path (Join-Path $TrainingRoot "..\..")).Path
$BenchmarkRoot = Join-Path $HashRoot "Tools\NeedleBenchmark"
$SmokeRoot = Join-Path $HashRoot "Tools\NeedleSmoke"
$Artifacts = Join-Path $TrainingRoot "artifacts"
$PipelineRoot = Join-Path $Artifacts "pipeline"
$DataRoot = Join-Path $TrainingRoot "data"
$CorpusRoot = Join-Path $TrainingRoot "data-production"
$TeacherCache = Join-Path $TrainingRoot "data-smoke8\teacher-cache"
$Python = Join-Path $TrainingRoot ".venv\Scripts\python.exe"
$Needle = Join-Path $TrainingRoot ".venv\Scripts\needle.exe"
$Checkpoint = Join-Path $TrainingRoot "checkpoints\needle2.pkl"
$Adapter = Join-Path $Artifacts "hash-production.pkl"
$TrainingReport = Join-Path $Artifacts "hash-production-training.json"
$Weights = Join-Path $Artifacts "hash-production.cact"
$Engine = Join-Path $HashRoot "Native\Hash.Needle.bin"
$SmokeProject = Join-Path $SmokeRoot "NeedleSmoke.csproj"
$SmokeExe = Join-Path $SmokeRoot "bin\Release\net8.0\NeedleSmoke.exe"
$BenchmarkJson = Join-Path $BenchmarkRoot "results\latest.json"
$BenchmarkHtml = Join-Path $BenchmarkRoot "results\latest.html"
$StatePath = Join-Path $PipelineRoot "state.json"
$LogPath = Join-Path $PipelineRoot ("pipeline-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

New-Item -ItemType Directory -Force -Path $Artifacts, $PipelineRoot, (Split-Path $BenchmarkJson) | Out-Null

foreach ($required in @($Python, $Needle, $Checkpoint, $Engine)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required pipeline input is missing: $required"
    }
}
# rtk trims command output to save agent tokens. It is a convenience, not a dependency: on a machine without it
# every stage runs the same command directly, so a fresh checkout does not need DooDesch's tooling installed.
$script:UseRtk = [bool](Get-Command rtk -ErrorAction SilentlyContinue)
if (-not $script:UseRtk) {
    Write-Host "rtk is not on PATH; running every stage command directly." -ForegroundColor DarkYellow
}

$script:PipelineState = [ordered]@{
    formatVersion = 1
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    finishedAtUtc = $null
    status = "running"
    error = $null
    accuracy = $null
    exact = $null
    cases = $null
    processId = $PID
    log = $LogPath
    corpus = $CorpusRoot
    adapter = $Adapter
    weights = $Weights
    reportJson = $BenchmarkJson
    reportHtml = $BenchmarkHtml
    parameters = [ordered]@{
        epochs = $Epochs
        patience = $Patience
        batchSize = $BatchSize
        perLanguage = $PerLanguage
        evalPerLanguage = $EvalPerLanguage
        minimumAccuracy = $MinimumAccuracy
        includeExternalDevSources = [bool]$IncludeExternalDevSources
        teacherApi = $TeacherApi
        teacherModel = $TeacherModel
        teacherBaseUrl = $TeacherBaseUrl
    }
    stages = [ordered]@{}
}

function Save-PipelineState {
    $script:PipelineState | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $StatePath -Encoding utf8NoBOM
}

function Set-StageState {
    param([string]$Name, [string]$Status, [string]$Detail = "")
    $script:PipelineState.stages[$Name] = [ordered]@{
        status = $Status
        detail = $Detail
        atUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Save-PipelineState
}

function Test-Fresh {
    param([string]$Output, [string[]]$Inputs)
    if (-not (Test-Path -LiteralPath $Output -PathType Leaf)) { return $false }
    $outputTime = (Get-Item -LiteralPath $Output).LastWriteTimeUtc
    foreach ($inputPath in $Inputs) {
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { return $false }
        if ((Get-Item -LiteralPath $inputPath).LastWriteTimeUtc -gt $outputTime) { return $false }
    }
    return $true
}

function Invoke-Rtk {
    param([string]$Command, [string[]]$Arguments)
    if ($script:UseRtk) {
        Write-Host ("`n> rtk proxy {0} {1}" -f $Command, ($Arguments -join " "))
        & rtk proxy $Command @Arguments
    }
    else {
        Write-Host ("`n> {0} {1}" -f $Command, ($Arguments -join " "))
        & $Command @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command"
    }
}

function Assert-CorpusCoverage {
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $CorpusRoot "manifest.json") | ConvertFrom-Json
    # Back at the provider's 100 per intent. It was briefly lowered to 90 because setvar (94) and
    # setmovespeed (96) fell short when the teacher ran out of distinct phrasings; the spoken-form
    # variants carry both back over the line without a single extra teacher call.
    $required = [ordered]@{ train = 100; validation = 10; holdout = 10 }
    foreach ($split in $required.Keys) {
        $rows = Get-Content -LiteralPath (Join-Path $CorpusRoot "$split.jsonl") |
            ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object { $_.command -and @($_.answers).Count -gt 0 }
        $groups = @($rows | Group-Object command)
        if ($groups.Count -ne [int]$manifest.commands) {
            throw "$split covers $($groups.Count) commands; expected $($manifest.commands)"
        }
        $minimum = [int](($groups | Measure-Object Count -Minimum).Minimum)
        if ($minimum -lt $required[$split]) {
            throw "$split has only $minimum positive examples for its least-covered command; provider minimum is $($required[$split])"
        }
        Write-Host "$split coverage: $($rows.Count) positive rows, $($groups.Count) commands, minimum $minimum/command"
    }
}

function Invoke-Stage {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    Set-StageState $Name "running"
    try {
        & $Action
        Set-StageState $Name "completed"
    }
    catch {
        Set-StageState $Name "failed" $_.Exception.Message
        throw
    }
}

Save-PipelineState
Start-Transcript -LiteralPath $LogPath -Append | Out-Null
try {
    $generatorInputs = @(
        (Join-Path $TrainingRoot "generate_data.py"),
        (Join-Path $DataRoot "tools.json"),
        (Join-Path $DataRoot "values.json"),
        (Join-Path $DataRoot "commands.json"),
        (Join-Path $BenchmarkRoot "cases.json")
    )
    $corpusFiles = @("manifest.json", "train.jsonl", "validation.jsonl", "holdout.jsonl") |
        ForEach-Object { Join-Path $CorpusRoot $_ }
    $corpusFresh = (-not $ForceData)
    foreach ($corpusFile in $corpusFiles) {
        $corpusFresh = $corpusFresh -and (Test-Fresh $corpusFile $generatorInputs)
    }
    if ($corpusFresh) {
        $existingManifest = Get-Content -Raw -LiteralPath (Join-Path $CorpusRoot "manifest.json") | ConvertFrom-Json
        $corpusFresh = ([int]$existingManifest.perLanguage -eq $PerLanguage) -and
            ([int]$existingManifest.evalPerLanguage -eq $EvalPerLanguage) -and
            ([bool]$existingManifest.excludeExternalDevSources -eq (-not [bool]$IncludeExternalDevSources))
    }

    Invoke-Stage "data" {
        if ($corpusFresh) {
            Write-Host "Fresh production corpus found; generation skipped."
        }
        else {
            $generateArgs = @(
                (Join-Path $TrainingRoot "generate_data.py"),
                "--tools", (Join-Path $DataRoot "tools.json"),
                "--values", (Join-Path $DataRoot "values.json"),
                "--commands", (Join-Path $DataRoot "commands.json"),
                "--cases", (Join-Path $BenchmarkRoot "cases.json"),
                "--out", $CorpusRoot,
                "--cache", $TeacherCache,
                "--api", $TeacherApi,
                "--model", $TeacherModel,
                "--per-language", "$PerLanguage",
                "--eval-per-language", "$EvalPerLanguage",
                "--batch-tools", "1"
            )
            if ($TeacherBaseUrl) {
                $generateArgs += @("--base-url", $TeacherBaseUrl)
            }
            if ($FallbackTeacherModel) {
                $generateArgs += @("--fallback-model", $FallbackTeacherModel)
            }
            if (-not $IncludeExternalDevSources) {
                $generateArgs += "--exclude-external-dev-sources"
            }
            Invoke-Rtk $Python $generateArgs
        }
        Invoke-Rtk $Python @((Join-Path $TrainingRoot "audit_dataset.py"), $CorpusRoot)
        Invoke-Rtk $Python @((Join-Path $TrainingRoot "audit_tokens.py"), $CorpusRoot, "--budget", "256")
        Assert-CorpusCoverage
    }

    $trainingInputs = @(
        (Join-Path $CorpusRoot "train.jsonl"),
        (Join-Path $CorpusRoot "validation.jsonl"),
        (Join-Path $TrainingRoot "finetune_hash.py"),
        $Checkpoint
    )
    Invoke-Stage "training" {
        if ((-not $ForceTrain) -and (Test-Fresh $Adapter $trainingInputs) -and
            (Test-Fresh $TrainingReport $trainingInputs)) {
            Write-Host "Fresh adapter and training report found; training skipped."
        }
        else {
            Invoke-Rtk $Python @(
                (Join-Path $TrainingRoot "finetune_hash.py"),
                (Join-Path $CorpusRoot "train.jsonl"),
                "--validation", (Join-Path $CorpusRoot "validation.jsonl"),
                "--checkpoint", $Checkpoint,
                "--out", $Adapter,
                "--log", $TrainingReport,
                "--epochs", "$Epochs",
                "--patience", "$Patience",
                "--batch-size", "$BatchSize",
                "--lr", "0.0001",
                "--lora-rank", "32",
                "--lora-alpha", "32",
                "--max-len", "256"
            )
        }
    }

    Invoke-Stage "export" {
        if ((-not $ForceExport) -and (Test-Fresh $Weights @($Adapter, $Checkpoint))) {
            Write-Host "Fresh native weights found; export skipped."
        }
        else {
            Invoke-Rtk $Needle @("build", $Checkpoint, "--lora", $Adapter, "--out", $Weights, "--bits", "4")
        }
    }

    Invoke-Stage "benchmark-build" {
        $smokeSources = @($SmokeProject, (Join-Path $SmokeRoot "Program.cs"))
        if (Test-Fresh $SmokeExe $smokeSources) {
            Write-Host "Fresh native benchmark executable found; build skipped."
        }
        else {
            Invoke-Rtk "dotnet" @("build", $SmokeProject, "-c", "Release", "--nologo")
        }
    }

    $benchmarkInputs = @($Weights, (Join-Path $CorpusRoot "holdout.jsonl"), $Engine, $SmokeExe)
    Invoke-Stage "native-benchmark" {
        if ((-not $ForceBenchmark) -and (Test-Fresh $BenchmarkJson $benchmarkInputs)) {
            Write-Host "Fresh native benchmark report found; execution skipped."
        }
        else {
            Invoke-Rtk $SmokeExe @(
                "benchmark", $Engine, $Weights,
                (Join-Path $CorpusRoot "holdout.jsonl"), $BenchmarkJson
            )
        }
    }

    Invoke-Stage "html-report" {
        Invoke-Rtk $Python @(
            (Join-Path $BenchmarkRoot "render_native_report.py"),
            $BenchmarkJson,
            "--training", $TrainingReport,
            "--manifest", (Join-Path $CorpusRoot "manifest.json"),
            "--out", $BenchmarkHtml
        )
    }

    $result = Get-Content -Raw -LiteralPath $BenchmarkJson | ConvertFrom-Json
    $accuracy = [double]$result.accuracy
    $script:PipelineState.accuracy = $accuracy
    $script:PipelineState.exact = [int]$result.exact
    $script:PipelineState.cases = [int]$result.cases
    if ($accuracy -lt $MinimumAccuracy) {
        $script:PipelineState.status = "needs-iteration"
        $script:PipelineState.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        Save-PipelineState
        throw ("Native exact-match accuracy {0:P2} is below the release gate {1:P2}. Report: {2}" -f
            $accuracy, $MinimumAccuracy, $BenchmarkHtml)
    }

    $script:PipelineState.status = "completed"
    $script:PipelineState.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    Save-PipelineState
    Write-Host "`nPipeline completed: $($result.exact)/$($result.cases) exact" -ForegroundColor Green
    Write-Host "HTML report: $BenchmarkHtml"
}
catch {
    if ($script:PipelineState.status -eq "running") {
        $script:PipelineState.status = "failed"
        $script:PipelineState.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        $script:PipelineState.error = $_.Exception.Message
        Save-PipelineState
    }
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
