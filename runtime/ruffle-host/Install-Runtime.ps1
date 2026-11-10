[CmdletBinding()]
param(
    [string]$Version = "0.7.1",
    [string]$SwfPath,
    [string]$MxmlcPath
)

$ErrorActionPreference = "Stop"

$runtimeRoot = $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$ruffleTarget = Join-Path $runtimeRoot "ruffle"
$gameTarget = Join-Path $runtimeRoot "game"
$archiveName = "ruffle-$Version-web-selfhosted.zip"
$downloadUrl = "https://github.com/ruffle-rs/ruffle/releases/download/v$Version/$archiveName"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("headless-skua-ruffle-" + [guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot $archiveName
$useDefaultSwf = [string]::IsNullOrWhiteSpace($SwfPath)

if ($useDefaultSwf) {
    $SwfPath = Join-Path $repositoryRoot "vendor\VibeSkua\Skua.AS3\skua\bin\skua.swf"
}

function Resolve-MxmlcPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath) -and (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    if (-not [string]::IsNullOrWhiteSpace($env:MXMLC_PATH) -and (Test-Path -LiteralPath $env:MXMLC_PATH -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $env:MXMLC_PATH).Path
    }

    if (-not [string]::IsNullOrWhiteSpace($env:FLEX_HOME)) {
        $candidate = Join-Path $env:FLEX_HOME "bin\mxmlc.bat"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command mxmlc -ErrorAction SilentlyContinue
    if ($null -ne $command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $command.Source).Path
    }

    $knownLocations = @(
        "C:\MoonshineSDKs\Flex_SDK\Flex_4.16.1_AIR_51.1\bin\mxmlc.bat",
        "C:\ApacheFlex\bin\mxmlc.bat"
    )

    foreach ($candidate in $knownLocations) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Build-HeadlessSkuaSwf {
    param(
        [string]$CompilerPath,
        [string]$SourceRoot,
        [string]$OutputPath,
        [string]$TemporaryRoot
    )

    if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        return $false
    }

    $modulesPath = Join-Path $SourceRoot "skua\module\Modules.as"
    $mainPath = Join-Path $SourceRoot "skua\Main.as"
    if (-not (Test-Path -LiteralPath $modulesPath -PathType Leaf) -or -not (Test-Path -LiteralPath $mainPath -PathType Leaf)) {
        Write-Warning "Skua AS3 source was not found; using the existing compiled client."
        return $false
    }

    $patchedSourceRoot = Join-Path $TemporaryRoot "patched-as3-src"
    Copy-Item -LiteralPath $SourceRoot -Destination $patchedSourceRoot -Recurse -Force

    $patchedModulesPath = Join-Path $patchedSourceRoot "skua\module\Modules.as"
    $patchedMainPath = Join-Path $patchedSourceRoot "skua\Main.as"
    $modulesContent = Get-Content -LiteralPath $patchedModulesPath -Raw
    $mainContent = Get-Content -LiteralPath $patchedMainPath -Raw

    $moduleGuardPattern = 'if\s*\(game\s*==\s*null\)\s*\{\s*return;\s*\}'
    $patchedModules = [regex]::Replace(
        $modulesContent,
        $moduleGuardPattern,
        'if (game == null || game.ui == null || game.ui.ModalStack == null) { return; }',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2)
    )

    if ([string]::Equals($modulesContent, $patchedModules, [StringComparison]::Ordinal)) {
        Write-Warning "The Skua module frame guard did not match the expected source; using the existing compiled client."
        return $false
    }

    $mainContentBeforePatch = $mainContent
    $mainContent = [regex]::Replace(
        $mainContent,
        '(var obj:\*\s*=\s*_getObjectA\(instance\.game,\s*parts\);\s*)(obj\[varName\]\s*=\s*value;)',
        '$1if (obj == null) return;' + [Environment]::NewLine + '        $2',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2)
    )
    $mainContent = [regex]::Replace(
        $mainContent,
        '(var obj:\*\s*=\s*_getObjectA\(instance\.game,\s*parts\);\s*)(obj\[key\]\s*=\s*value;)',
        '$1if (obj == null) return;' + [Environment]::NewLine + '        $2',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2)
    )
    $mainContent = [regex]::Replace(
        $mainContent,
        '(var obj:\*\s*=\s*_getObjectS\(instance\.game,\s*path\);\s*)(obj\[index\]\s*=\s*value;)',
        '$1if (obj == null) return;' + [Environment]::NewLine + '        $2',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2)
    )

    if ([string]::Equals($mainContentBeforePatch, $mainContent, [StringComparison]::Ordinal)) {
        Write-Warning "The Skua Flash setter guards did not match the expected source; using the existing compiled client."
        return $false
    }

    Set-Content -LiteralPath $patchedModulesPath -Value $patchedModules -Encoding utf8
    Set-Content -LiteralPath $patchedMainPath -Value $mainContent -Encoding utf8

    $compilerDirectory = Split-Path -Parent $CompilerPath
    if ([string]::IsNullOrWhiteSpace($env:FLEX_HOME)) {
        $env:FLEX_HOME = Split-Path -Parent $compilerDirectory
    }
    if ([string]::IsNullOrWhiteSpace($env:PLAYERGLOBAL_HOME)) {
        $env:PLAYERGLOBAL_HOME = Join-Path $env:FLEX_HOME "frameworks\libs\player"
    }

    Write-Host "Compiling the headless-safe Skua AS3 client..." -ForegroundColor Cyan
    & $CompilerPath -source-path $patchedSourceRoot -default-size 958 550 -output $OutputPath $patchedMainPath -target-player 28.0 -optimize
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        Write-Warning "The patched AS3 build failed; using the existing compiled Skua client."
        return $false
    }

    Write-Host "Headless-safe Skua client compiled to $OutputPath" -ForegroundColor Green
    return $true
}

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

        if ($useDefaultSwf) {
            $as3SourceRoot = Join-Path $repositoryRoot "vendor\VibeSkua\Skua.AS3\skua\src"
            $resolvedMxmlcPath = Resolve-MxmlcPath -RequestedPath $MxmlcPath
            if (-not [string]::IsNullOrWhiteSpace($resolvedMxmlcPath)) {
                $patchedSwfPath = Join-Path $tempRoot "skua-headless.swf"
                if (Build-HeadlessSkuaSwf -CompilerPath $resolvedMxmlcPath -SourceRoot $as3SourceRoot -OutputPath $patchedSwfPath -TemporaryRoot $tempRoot) {
                    $SwfPath = $patchedSwfPath
                }
            }
            else {
                Write-Host "Flex mxmlc not found; using the bundled Skua SWF without optional AS3 headless guards." -ForegroundColor Yellow
            }
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
