#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string]$OutputDir
)

$sentinel = Join-Path $OutputDir 'portmidi.dll'
if (Test-Path $sentinel) {
    Write-Host 'PortMidi DLL already present, skipping download.'
    exit 0
}

# conda-forge is the only reliable source of prebuilt portmidi.dll for Windows x64.
# The .conda file is a ZIP; inside is a .tar.zst that Windows 11's built-in tar handles natively.
$url         = 'https://conda.anaconda.org/conda-forge/win-64/portmidi-2.0.8-hac47afa_0.conda'
$tempConda   = Join-Path $env:TEMP 'portmidi-2.0.8.conda'
$tempZip     = Join-Path $env:TEMP 'portmidi-2.0.8.zip'
$tempExtract = Join-Path $env:TEMP 'portmidi-extract'

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Write-Host 'Downloading PortMidi 2.0.8 from conda-forge ...'
Invoke-WebRequest -Uri $url -OutFile $tempConda -UseBasicParsing

# .conda is a ZIP — rename and extract to find the pkg .tar.zst inside
Copy-Item $tempConda $tempZip -Force
if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
Expand-Archive -Path $tempZip -DestinationPath $tempExtract

$tarZst = Get-ChildItem $tempExtract -Filter 'pkg-*.tar.zst' | Select-Object -First 1
if (-not $tarZst) {
    Write-Error "Could not find pkg-*.tar.zst inside the .conda archive."
    exit 1
}

Write-Host 'Extracting portmidi.dll ...'
$prevLocation = Get-Location
Set-Location $tempExtract
tar -xf $tarZst.FullName 'Library/bin/portmidi.dll'
Set-Location $prevLocation

$extracted = Join-Path $tempExtract 'Library\bin\portmidi.dll'
if (-not (Test-Path $extracted)) {
    Write-Error "Extraction finished but portmidi.dll was not found. Archive structure may have changed."
    exit 1
}

Copy-Item $extracted -Destination $sentinel -Force

Remove-Item $tempConda   -Force -ErrorAction SilentlyContinue
Remove-Item $tempZip     -Force -ErrorAction SilentlyContinue
Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'PortMidi DLL extracted successfully.'
