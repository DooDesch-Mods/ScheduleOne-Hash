<#
.SYNOPSIS
    Put everything the Needle training pipeline needs on a fresh machine.

.DESCRIPTION
    Creates the training virtual environment, installs cactus-needle and its JAX stack, downloads the
    86 MB needle2 base checkpoint and the SentencePiece tokenizer, fetches the native inference engine
    into Native/Hash.Needle.bin, and proves that the exporter and the engine agree on the .cact format.

    The training package is pinned to 2.0.3 because the shipped engine demands it. See the export-format
    check at the end of this script for the measurement behind that number.

    Nothing here needs the game, a game bridge, or a network call at inference time.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$TrainingRoot = $PSScriptRoot
$HashRoot = (Resolve-Path (Join-Path $TrainingRoot "..\..")).Path
$Venv = Join-Path $TrainingRoot ".venv"
$Python = Join-Path $Venv "Scripts\python.exe"
$Checkpoint = Join-Path $TrainingRoot "checkpoints\needle2.pkl"
$Engine = Join-Path $HashRoot "Native\Hash.Needle.bin"
# PyPI 2.0.0-2.0.2 write export tag 0x05E12A82; 2.0.3 and later write 0x05E12A83, which is the only tag
# the shipped engine contains. The checkpoint format is 2 across all of them, so needle2.pkl is unaffected.
$NeedleVersion = "2.0.3"

function Step {
    param([string]$Name)
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
}

Step "Prerequisites"
$uv = Get-Command uv -ErrorAction SilentlyContinue
$hostPython = Get-Command python -ErrorAction SilentlyContinue
if (-not $uv -and -not $hostPython) {
    throw "Neither uv nor python is on PATH. Install Python 3.11 or newer, or uv, and run this again."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Warning "dotnet is not on PATH. Everything up to the native benchmark still runs, but the pipeline's benchmark stage needs the .NET 8 SDK."
}
Write-Host ("uv:     {0}" -f $(if ($uv) { $uv.Source } else { "not installed (falling back to python -m venv)" }))
Write-Host ("python: {0}" -f $(if ($hostPython) { $hostPython.Source } else { "not on PATH" }))

Step "Virtual environment"
if ($Force -and (Test-Path -LiteralPath $Venv)) {
    Write-Host "Removing the existing environment because -Force was given."
    Remove-Item -Recurse -Force -LiteralPath $Venv
}
if (Test-Path -LiteralPath $Python) {
    Write-Host "$Venv already exists; reusing it."
}
elseif ($uv) {
    & uv venv --python 3.13 $Venv
    if ($LASTEXITCODE -ne 0) { throw "uv venv failed with exit code $LASTEXITCODE" }
}
else {
    & python -m venv $Venv
    if ($LASTEXITCODE -ne 0) { throw "python -m venv failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
    throw "The environment was created but $Python is missing."
}

Step "cactus-needle 2.0.3 and the JAX stack"
# The training package comes from PyPI, never from the wheel that fetch-needle.ps1 downloads. The Hugging
# Face wheel is the slim engine build and carries no finetune, build or tokenizer module, so installing it
# into this environment silently removes the training code and leaves imports failing with
# "unknown location" - and it does so under a version number that looks like an exact match.
if ($uv) {
    & uv pip install --python $Python "cactus-needle==$NeedleVersion"
}
else {
    & $Python -m pip install --upgrade pip
    & $Python -m pip install "cactus-needle==$NeedleVersion"
}
if ($LASTEXITCODE -ne 0) { throw "Installing cactus-needle failed with exit code $LASTEXITCODE" }

& $Python -c "import jax; from needle.model.finetune import render_example; from needle.model.tokenizer import get_tokenizer; print('needle import ok, jax backend', jax.default_backend())"
if ($LASTEXITCODE -ne 0) { throw "The Needle package does not import; the environment is not usable." }

Step "Base checkpoint and tokenizer"
if ((Test-Path -LiteralPath $Checkpoint -PathType Leaf) -and -not $Force) {
    Write-Host ("checkpoints/needle2.pkl already present ({0:N1} MB)." -f ((Get-Item -LiteralPath $Checkpoint).Length / 1MB))
}
else {
    New-Item -ItemType Directory -Force -Path (Split-Path $Checkpoint) | Out-Null
    & $Python -c @"
from huggingface_hub import hf_hub_download
import shutil, sys
source = hf_hub_download('Cactus-Compute/needle2', 'checkpoints/needle2.pkl', repo_type='model')
shutil.copyfile(source, sys.argv[1])
print('checkpoint ->', sys.argv[1])
"@ $Checkpoint
    if ($LASTEXITCODE -ne 0) { throw "Downloading the needle2 checkpoint failed with exit code $LASTEXITCODE" }
}
# get_tokenizer() pulls the SentencePiece model on first use; doing it here keeps the first real run offline.
& $Python -c "from needle.model.tokenizer import get_tokenizer; print('tokenizer vocab', get_tokenizer().vocab_size)"
if ($LASTEXITCODE -ne 0) { throw "Fetching the Needle tokenizer failed with exit code $LASTEXITCODE" }

Step "Native inference engine"
if ((Test-Path -LiteralPath $Engine -PathType Leaf) -and -not $Force) {
    Write-Host ("Native/Hash.Needle.bin already present ({0:N1} MB)." -f ((Get-Item -LiteralPath $Engine).Length / 1MB))
}
else {
    & pwsh -NoProfile -File (Join-Path $HashRoot "Tools\fetch-needle.ps1")
    if ($LASTEXITCODE -ne 0) { throw "fetch-needle.ps1 failed with exit code $LASTEXITCODE" }
}

Step "Export format"
# An adapter that the engine refuses to load is only discovered after the training run, so it is proven
# here instead: the exporter's tag has to be a tag the engine binary actually knows.
$exportTag = & $Python -c "from needle.model.export import TAG; print(hex(TAG))"
if ($LASTEXITCODE -ne 0) { throw "Could not read the exporter's format tag." }
$tagBytes = [BitConverter]::GetBytes([uint32]$exportTag)
$engineBytes = [System.IO.File]::ReadAllBytes($Engine)
$found = $false
for ($index = 0; $index -le $engineBytes.Length - 4; $index++) {
    if ($engineBytes[$index] -eq $tagBytes[0] -and $engineBytes[$index + 1] -eq $tagBytes[1] -and
        $engineBytes[$index + 2] -eq $tagBytes[2] -and $engineBytes[$index + 3] -eq $tagBytes[3]) {
        $found = $true
        break
    }
}
if (-not $found) {
    throw ("cactus-needle writes .cact tag $exportTag, which Native/Hash.Needle.bin does not contain. " +
        "An adapter exported by this environment would fail with 'needle_load failed with code -1'. " +
        "Either pin a cactus-needle version whose needle/model/export.py TAG matches the engine, or " +
        "update the engine in Tools/fetch-needle.ps1 to match the package.")
}
Write-Host "Exporter tag $exportTag is present in the engine."

Step "Ready"
Write-Host "Environment: $Venv"
Write-Host "Checkpoint:  $Checkpoint"
Write-Host "Engine:      $Engine"
Write-Host "`nNext: pwsh -File $TrainingRoot\probe_machine.ps1" -ForegroundColor Green
