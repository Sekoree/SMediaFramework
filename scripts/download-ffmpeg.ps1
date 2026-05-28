#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string]$OutputDir
)

$sentinel = Join-Path $OutputDir 'avcodec-62.dll'
if (Test-Path $sentinel) {
    Write-Host 'FFmpeg DLLs already present, skipping download.'
    exit 0
}

# BtbN builds are ZIP (no 7-Zip required) and available for both GPL and LGPL.
# "latest" tag always resolves to the newest 8.0.x patch build.
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip'
$tempArchive = Join-Path $env:TEMP 'ffmpeg-n8.1-shared.zip'
$tempExtract = Join-Path $env:TEMP 'ffmpeg-n8.1-extract'

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Write-Host 'Downloading FFmpeg 8.1 shared build from BtbN ...'
Invoke-WebRequest -Uri $url -OutFile $tempArchive -UseBasicParsing

Write-Host "Extracting DLLs to $OutputDir ..."
if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
Expand-Archive -Path $tempArchive -DestinationPath $tempExtract

Get-ChildItem -Path $tempExtract -Filter '*.dll' -Recurse |
    Where-Object { $_.DirectoryName -match '\\bin$' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $OutputDir -Force }

Remove-Item $tempArchive  -Force -ErrorAction SilentlyContinue
Remove-Item $tempExtract  -Recurse -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $sentinel)) {
    Write-Error "Extraction finished but avcodec-62.dll was not found in $OutputDir. The BtbN archive structure may have changed."
    exit 1
}

Write-Host 'FFmpeg DLLs extracted successfully.'
