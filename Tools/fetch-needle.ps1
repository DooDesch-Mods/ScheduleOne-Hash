[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\Native\Hash.Needle.bin'),
    [string] $WheelPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$wheelUrl = 'https://huggingface.co/Cactus-Compute/needle2/resolve/main/python/cactus_needle-2.0.2-py3-none-win_amd64.whl'
$wheelSha256 = '718955a433f4c16c193266a37627dd34167389fa55d0c385a2a38064a5691403'
$librarySha256 = 'ec6f681d86e7a31ee81d02930b8bc5abf7b1a8da25b6c24dd3188fd62e950f13'

if ([string]::IsNullOrWhiteSpace($WheelPath)) {
    $WheelPath = Join-Path $env:TEMP 'cactus_needle-2.0.2-py3-none-win_amd64.whl'
    Invoke-WebRequest -UseBasicParsing -Uri $wheelUrl -OutFile $WheelPath
}

$actualWheelHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $WheelPath).Hash.ToLowerInvariant()
if ($actualWheelHash -ne $wheelSha256) {
    throw "Needle wheel checksum mismatch: expected $wheelSha256, got $actualWheelHash"
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$archive = [System.IO.Compression.ZipFile]::OpenRead([System.IO.Path]::GetFullPath($WheelPath))
try {
    $entry = $archive.GetEntry('needle/libneedle.dll')
    if ($null -eq $entry) { throw 'needle/libneedle.dll is missing from the pinned wheel' }
    $input = $entry.Open()
    try {
        $output = [System.IO.File]::Create($resolvedOutput)
        try { $input.CopyTo($output) } finally { $output.Dispose() }
    } finally { $input.Dispose() }
} finally { $archive.Dispose() }

$actualLibraryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedOutput).Hash.ToLowerInvariant()
if ($actualLibraryHash -ne $librarySha256) {
    Remove-Item -LiteralPath $resolvedOutput -Force
    throw "Needle library checksum mismatch: expected $librarySha256, got $actualLibraryHash"
}

Write-Output "Needle 2.0.2: $resolvedOutput"
