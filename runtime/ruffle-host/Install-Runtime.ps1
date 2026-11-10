[CmdletBinding()]
param(
    [string]$Version = "0.7.1",
    [string]$SwfPath
)

$ErrorActionPreference = "Stop"

$runtimeRoot = $PSScriptRoot
$ruffleTarget = Join-Path $runtimeRoot "ruffle"
$gameTarget = Join-Path $runtimeRoot "game"
$archiveName = "ruffle-$Version-web-selfhosted.zip"
$downloadUrl = "https://github.com/ruffle-rs/ruffle/releases/download/v$Version/$archiveName"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("headless-skua-ruffle-" + [guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot $archiveName

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Write-Host "Downloading Ruffle $Version..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath

    $extractPath = Join-Path $tempRoot "extracted"
    Expand-Archive -Path $archivePath -DestinationPath $extractPath -Force

    $ruffleEntry = Get-ChildItem -Path $extractPath -Filter "ruffle.js" -File -Recurse | Select-Object -First 1
    if ($null -eq $ruffleEntry) {
        throw "The Ruffle archive did not contain ruffle.js."
    }

    New-Item -ItemType Directory -Path $ruffleTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $ruffleEntry.DirectoryName "*") -Destination $ruffleTarget -Recurse -Force

    Write-Host "Ruffle installed to $ruffleTarget" -ForegroundColor Green

    if (-not [string]::IsNullOrWhiteSpace($SwfPath)) {
        if (-not (Test-Path -LiteralPath $SwfPath -PathType Leaf)) {
            throw "SWF file not found: $SwfPath"
        }

        New-Item -ItemType Directory -Path $gameTarget -Force | Out-Null
        Copy-Item -LiteralPath $SwfPath -Destination (Join-Path $gameTarget "skua.swf") -Force
        Write-Host "Skua SWF copied to $(Join-Path $gameTarget 'skua.swf')" -ForegroundColor Green
    }
    else {
        Write-Host "No -SwfPath supplied. Copy the built Skua SWF to $(Join-Path $gameTarget 'skua.swf') before launching the runtime." -ForegroundColor Yellow
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
