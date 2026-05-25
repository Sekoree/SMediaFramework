#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string]$OutputDir
)

$dllName = 'Processing.NDI.Lib.x64.dll'
$sentinel = Join-Path $OutputDir $dllName
if (Test-Path $sentinel) {
    Write-Host 'NDI DLL already present, skipping.'
    exit 0
}

# Candidate directories: env var set by NDI SDK/Runtime installer, then common paths
$candidateDirs = @()

$runtimeDir = [System.Environment]::GetEnvironmentVariable('NDI_RUNTIME_DIR_V6', 'Machine')
if (-not $runtimeDir) {
    $runtimeDir = [System.Environment]::GetEnvironmentVariable('NDI_RUNTIME_DIR_V6', 'User')
}
if ($runtimeDir) { $candidateDirs += $runtimeDir }

$candidateDirs += @(
    'C:\Program Files\NDI\NDI 6 SDK\Bin\x64',
    'C:\Program Files\NDI\NDI 6 Runtime\x64',
    'C:\Program Files\NewTek\NDI 6 SDK\Bin\x64',
    'C:\Program Files\NewTek\NDI 6 Runtime\x64'
)

$source = $candidateDirs |
    ForEach-Object { Join-Path $_ $dllName } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if (-not $source) {
    Write-Host 'NDI SDK not found, skipping. Install NDI 6 Runtime from https://ndi.video/download-ndi-sdk/ to enable NDI features.'
    exit 0
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Copy-Item -Path $source -Destination $sentinel -Force
Write-Host "Copied NDI DLL from $source"
