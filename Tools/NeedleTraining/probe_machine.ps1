<#
.SYNOPSIS
    Report this machine's training hardware and name the teacher model that actually fits it.

.DESCRIPTION
    The corpus stage needs a local instruction model to write natural phrasings. Which one is the right
    one is a property of the machine, not of the repository, so it is measured here instead of guessed
    in a document: RAM, VRAM, GPU vendor and free disk are read from the system, and each candidate's
    download size is read from the Hugging Face API rather than estimated.

    Two teacher routes exist and the choice between them is decided by the GPU:

      FreeToken  - NVIDIA only (RTX 30/40/50). Serves large MoE checkpoints by keeping the experts in
                   host RAM, so system RAM is the binding limit, not VRAM. No constrained decoding, so
                   generate_data.py must run with --api openai.
      Ollama     - any GPU or CPU. This is the route the corpus was designed against, with a hard JSON
                   schema on every teacher reply.

    Nothing is downloaded or installed here.
#>
[CmdletBinding()]
param(
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# MoE checkpoints from FreeToken's supported list, smallest first. Dense checkpoints are left out on
# purpose: FreeToken resolves them to the fused backend, which needs the whole model in VRAM.
$Candidates = @(
    [ordered]@{ id = "openai/gpt-oss-20b";              note = "21B MoE, 3.6B active; smallest usable teacher" }
    [ordered]@{ id = "nvidia/Gemma-4-26B-A4B-NVFP4";    note = "26B MoE, 4B active, 4-bit" }
    [ordered]@{ id = "nvidia/Qwen3.6-35B-A3B-NVFP4";    note = "35B MoE, 3B active, 4-bit" }
    [ordered]@{ id = "Qwen/Qwen3.6-35B-A3B-FP8";        note = "35B MoE, 3B active, 8-bit" }
    [ordered]@{ id = "openai/gpt-oss-120b";             note = "120B MoE, 5.1B active" }
)

function Get-HuggingFaceSizeGb {
    param([string]$RepoId)
    try {
        $meta = Invoke-RestMethod -UseBasicParsing -TimeoutSec 30 `
            -Uri "https://huggingface.co/api/models/$($RepoId)?blobs=true"
    }
    catch {
        Write-Warning "$RepoId size unknown: $($_.Exception.Message)"
        return $null
    }
    $bytes = 0L
    foreach ($file in $meta.siblings) {
        $name = [string]$file.rfilename
        if ($name -notmatch '\.(safetensors|gguf|bin)$') { continue }
        if ($file.PSObject.Properties.Name -contains "size" -and $null -ne $file.size) {
            $bytes += [int64]$file.size
        }
    }
    if ($bytes -le 0) { return $null }
    return [math]::Round($bytes / 1GB, 1)
}

$os = Get-CimInstance Win32_OperatingSystem
$cpu = @(Get-CimInstance Win32_Processor)[0]
$ramGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)

$gpus = @()
$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($nvidiaSmi) {
    $rows = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits 2>$null
    if ($LASTEXITCODE -eq 0) {
        foreach ($row in @($rows)) {
            $parts = $row -split ",\s*"
            if ($parts.Count -ge 3) {
                $gpus += [ordered]@{
                    name = $parts[0].Trim()
                    vramGb = [math]::Round([double]$parts[1] / 1024, 1)
                    driver = $parts[2].Trim()
                    vendor = "NVIDIA"
                }
            }
        }
    }
    else {
        Write-Warning "nvidia-smi is on PATH but returned exit code $LASTEXITCODE; treating the machine as having no usable NVIDIA GPU."
    }
}
if ($gpus.Count -eq 0) {
    foreach ($card in Get-CimInstance Win32_VideoController) {
        $gpus += [ordered]@{
            name = $card.Name
            # AdapterRAM is a 32-bit field and reports garbage above 4 GB, so it is deliberately not read here.
            vramGb = $null
            driver = $card.DriverVersion
            vendor = if ($card.Name -match "NVIDIA") { "NVIDIA" } elseif ($card.Name -match "AMD|Radeon") { "AMD" } elseif ($card.Name -match "Intel") { "Intel" } else { "unknown" }
        }
    }
}
$hasNvidia = @($gpus | Where-Object { $_.vendor -eq "NVIDIA" }).Count -gt 0

$repoDrive = (Get-Item $PSScriptRoot).PSDrive.Name
$freeDiskGb = [math]::Round((Get-PSDrive $repoDrive).Free / 1GB, 1)

$tools = [ordered]@{}
foreach ($tool in "python", "py", "uv", "dotnet", "ollama", "ft", "nvidia-smi", "git", "pwsh") {
    $found = Get-Command $tool -ErrorAction SilentlyContinue
    $tools[$tool] = if ($found) { $found.Source } else { $null }
}

# The MoE offload backend streams experts from host RAM. Leave the OS, the corpus generator and the
# page cache room to work rather than filling RAM to the brim.
$ramBudgetGb = [math]::Round($ramGb - 8, 1)
$sized = @()
foreach ($candidate in $Candidates) {
    $sizeGb = Get-HuggingFaceSizeGb $candidate.id
    $sized += [ordered]@{
        id = $candidate.id
        note = $candidate.note
        downloadGb = $sizeGb
        fitsRam = if ($null -eq $sizeGb) { $null } else { $sizeGb -le $ramBudgetGb }
        fitsDisk = if ($null -eq $sizeGb) { $null } else { $sizeGb -le ($freeDiskGb - 20) }
    }
}

$viable = @($sized | Where-Object { $_.fitsRam -eq $true -and $_.fitsDisk -eq $true })
$recommendation = $null
if ($hasNvidia -and $viable.Count -gt 0) {
    $best = $viable[-1]
    $recommendation = [ordered]@{
        route = "freetoken"
        model = $best.id
        downloadGb = $best.downloadGb
        generatorApi = "openai"
        baseUrl = "http://127.0.0.1:1919"
    }
}
else {
    $reason = if (-not $hasNvidia) { "no NVIDIA GPU: FreeToken supports RTX 30/40/50 only" }
              else { "no supported MoE checkpoint fits ${ramBudgetGb} GB of usable RAM" }
    $recommendation = [ordered]@{
        route = "ollama"
        model = "qwen3:8b"
        downloadGb = 5.2
        generatorApi = "ollama"
        baseUrl = "http://127.0.0.1:11434"
        reason = $reason
    }
}

$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    machine = [ordered]@{
        os = $os.Caption
        cpu = $cpu.Name.Trim()
        cores = $cpu.NumberOfCores
        threads = $cpu.NumberOfLogicalProcessors
        ramGb = $ramGb
        ramBudgetGb = $ramBudgetGb
        freeDiskGb = $freeDiskGb
        gpus = $gpus
        hasNvidia = $hasNvidia
    }
    tools = $tools
    candidates = $sized
    recommendation = $recommendation
}

if ($Json) {
    $report | ConvertTo-Json -Depth 8
    return
}

Write-Host "`n=== Machine ===" -ForegroundColor Cyan
Write-Host ("{0}`n{1} ({2} cores / {3} threads)" -f $os.Caption, $cpu.Name.Trim(), $cpu.NumberOfCores, $cpu.NumberOfLogicalProcessors)
Write-Host ("RAM: {0} GB total, {1} GB usable for expert offload" -f $ramGb, $ramBudgetGb)
Write-Host ("Free disk on {0}: {1} GB" -f $repoDrive, $freeDiskGb)
foreach ($gpu in $gpus) {
    $vram = if ($null -eq $gpu.vramGb) { "VRAM unknown" } else { "$($gpu.vramGb) GB VRAM" }
    Write-Host ("GPU: {0} - {1}, driver {2}" -f $gpu.name, $vram, $gpu.driver)
}

Write-Host "`n=== Tools on PATH ===" -ForegroundColor Cyan
foreach ($tool in $tools.Keys) {
    $state = if ($tools[$tool]) { $tools[$tool] } else { "missing" }
    Write-Host ("{0,-12} {1}" -f $tool, $state)
}

Write-Host "`n=== Teacher candidates (FreeToken, download size from Hugging Face) ===" -ForegroundColor Cyan
foreach ($candidate in $sized) {
    $size = if ($null -eq $candidate.downloadGb) { "size unknown" } else { "$($candidate.downloadGb) GB" }
    $verdict = if ($candidate.fitsRam -eq $true -and $candidate.fitsDisk -eq $true) { "fits" }
               elseif ($null -eq $candidate.fitsRam) { "unverified" }
               elseif ($candidate.fitsRam -ne $true) { "too large for RAM" }
               else { "too large for the free disk" }
    Write-Host ("{0,-34} {1,-14} {2,-22} {3}" -f $candidate.id, $size, $verdict, $candidate.note)
}

Write-Host "`n=== Recommendation ===" -ForegroundColor Green
if ($recommendation.route -eq "freetoken") {
    Write-Host ("Install FreeToken for Windows from https://www.flashml.ai/ and serve {0}." -f $recommendation.model)
    Write-Host ("Then run the pipeline with:")
    Write-Host ("  -TeacherApi openai -TeacherBaseUrl {0} -TeacherModel {1}" -f $recommendation.baseUrl, $recommendation.model)
}
else {
    Write-Host ("Use Ollama: {0}." -f $recommendation.reason)
    Write-Host ("  ollama pull {0}" -f $recommendation.model)
    Write-Host ("Then run the pipeline with its defaults (-TeacherApi ollama -TeacherModel {0})." -f $recommendation.model)
}
Write-Host ""
