[CmdletBinding()]
param(
    [string]$Version = "nightly",
    [string]$SwfPath,
    [string]$MxmlcPath
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($env:HEADLESS_SKUA_RUFFLE_VERSION)) {
    $Version = $env:HEADLESS_SKUA_RUFFLE_VERSION
}

$runtimeRoot = $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$ruffleTarget = Join-Path $runtimeRoot "ruffle"
$gameTarget = Join-Path $runtimeRoot "game"
$resolvedVersion = $Version
$archiveName = $null
$downloadUrl = $null

if ($Version -ieq "nightly") {
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/ruffle-rs/ruffle/releases?per_page=20" -Headers @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "Headless-Skua"
    }

    $nightlyRelease = $releases |
        Where-Object { $_.tag_name -match '^nightly-\d{4}-\d{2}-\d{2}$' } |
        Sort-Object published_at -Descending |
        Select-Object -First 1

    if ($null -eq $nightlyRelease) {
        throw "Could not find a published Ruffle nightly release."
    }

    $nightlyAsset = $nightlyRelease.assets |
        Where-Object { $_.name -match '^ruffle-nightly_\d{4}_\d{2}_\d{2}-web-selfhosted\.zip$' } |
        Select-Object -First 1

    if ($null -eq $nightlyAsset) {
        throw "Ruffle release $($nightlyRelease.tag_name) does not contain the web self-hosted package."
    }

    $resolvedVersion = $nightlyRelease.tag_name
    $archiveName = $nightlyAsset.name
    $downloadUrl = $nightlyAsset.browser_download_url
}
elseif ($Version -match '^nightly-(\d{4})-(\d{2})-(\d{2})$') {
    $dateToken = "$($Matches[1])_$($Matches[2])_$($Matches[3])"
    $archiveName = "ruffle-nightly_$dateToken-web-selfhosted.zip"
    $downloadUrl = "https://github.com/ruffle-rs/ruffle/releases/download/$Version/$archiveName"
}
elseif ($Version -match '^\d+\.\d+\.\d+$') {
    $archiveName = "ruffle-$Version-web-selfhosted.zip"
    $downloadUrl = "https://github.com/ruffle-rs/ruffle/releases/download/v$Version/$archiveName"
}
else {
    throw "Invalid Ruffle version '$Version'. Use 'nightly', 'nightly-YYYY-MM-DD', or a stable version such as '0.7.1'."
}
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
    New-Item -ItemType Directory -Path $patchedSourceRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceRoot "*") -Destination $patchedSourceRoot -Recurse -Force

    $patchedModulesPath = Join-Path $patchedSourceRoot "skua\module\Modules.as"
    $patchedMainPath = Join-Path $patchedSourceRoot "skua\Main.as"
    $patchedWorldPath = Join-Path $patchedSourceRoot "skua\api\World.as"
    $patchedCombatPath = Join-Path $patchedSourceRoot "skua\api\Combat.as"
    $modulesContent = Get-Content -LiteralPath $patchedModulesPath -Raw
    $mainContent = Get-Content -LiteralPath $patchedMainPath -Raw
    $worldContent = Get-Content -LiteralPath $patchedWorldPath -Raw
    $combatContent = Get-Content -LiteralPath $patchedCombatPath -Raw

    $moduleTogglePattern = 'module\.onToggle\(Main\.instance\.getGame\(\)\);'
    $moduleToggleReplacement = @'
var game:* = Main.instance.getGame();
                if (isGameReady(game)) {
                    module.onToggle(game);
                }
'@
    $moduleToggleReplacement = $moduleToggleReplacement.Trim()
    $patchedModules = [regex]::Replace(
        $modulesContent,
        $moduleTogglePattern,
        $moduleToggleReplacement,
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2)
    )

    if ([regex]::Matches($modulesContent, $moduleTogglePattern).Count -ne 2) {
        Write-Warning "The Skua module toggle callbacks did not match the expected source; using the existing compiled client."
        return $false
    }

    $moduleReadyHelper = @'
    private static function isGameReady(game:*):Boolean {
        return game != null && game.world != null && game.world.avatars != null
            && game.ui != null && game.ui.ModalStack != null;
    }

'@
    $frameHandlerAnchor = '    public static function handleFrame(e:Event):void {'
    if (-not $patchedModules.Contains($frameHandlerAnchor)) {
        Write-Warning "The Skua module frame handler was not found; using the existing compiled client."
        return $false
    }
    $patchedModules = $patchedModules.Replace($frameHandlerAnchor, $moduleReadyHelper + $frameHandlerAnchor)

    $moduleGuardPattern = 'if\s*\(game\s*==\s*null\)\s*\{\s*return;\s*\}'
    $patchedModules = [regex]::Replace(
        $patchedModules,
        $moduleGuardPattern,
        'if (!isGameReady(game)) { return; }',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2)
    )

    if ([string]::Equals($modulesContent, $patchedModules, [StringComparison]::Ordinal)) {
        Write-Warning "The Skua module frame guard did not match the expected source; using the existing compiled client."
        return $false
    }

    $worldContentBeforePatch = $worldContent

    $skipCutscenesBefore = @'
    public static function skipCutscenes():void {
        while (Main.instance.game.mcExtSWF.numChildren > 0) {
'@
    $skipCutscenesAfter = @'
    public static function skipCutscenes():void {
        var game:* = Main.instance != null ? Main.instance.game : null;
        if (game == null || game.mcExtSWF == null) return;
        while (game.mcExtSWF.numChildren > 0) {
'@
    $worldContent = $worldContent.Replace($skipCutscenesBefore, $skipCutscenesAfter)
    $worldContent = $worldContent.Replace(
        '        Main.instance.game.mcExtSWF.removeChildAt(0);',
        '        game.mcExtSWF.removeChildAt(0);'
    )
    $worldContent = $worldContent.Replace(
        '        Main.instance.game.showInterface();',
        '        game.showInterface();'
    )

    $hidePlayersBefore = @'
    public static function hidePlayers(enabled:Boolean):void {
        var world:* = Main.instance.game.world;
'@
    $hidePlayersAfter = @'
    public static function hidePlayers(enabled:Boolean):void {
        var game:* = Main.instance != null ? Main.instance.game : null;
        var world:* = game != null ? game.world : null;
        if (world == null || world.avatars == null) return;
'@
    $worldContent = $worldContent.Replace($hidePlayersBefore, $hidePlayersAfter)

    $disableFxBefore = @'
    public static function disableFX(enabled:Boolean):void {
        if (!_fxLastOpt && enabled) {
'@
    $disableFxAfter = @'
    public static function disableFX(enabled:Boolean):void {
        var game:* = Main.instance != null ? Main.instance.game : null;
        var world:* = game != null ? game.world : null;
        if (world == null || world.avatars == null) return;
        if (!_fxLastOpt && enabled) {
'@
    $worldContent = $worldContent.Replace($disableFxBefore, $disableFxAfter)
    $worldContent = $worldContent.Replace(
        'for each (var avatar:* in Main.instance.game.world.avatars)',
        'for each (var avatar:* in world.avatars)'
    )

    $killLagBefore = @'
    public static function killLag(enable:Boolean):void {
        Main.instance.game.world.visible = !enable;
'@
    $killLagAfter = @'
    public static function killLag(enable:Boolean):void {
        var game:* = Main.instance != null ? Main.instance.game : null;
        if (game == null || game.world == null) return;
        game.world.visible = !enable;
'@
    $worldContent = $worldContent.Replace($killLagBefore, $killLagAfter)

    $combatContentBeforePatch = $combatContent
    $magnetizeBefore = @'
    public static function magnetize():void {
        var target:* = Main.instance.game.world.myAvatar.target;
'@
    $magnetizeAfter = @'
    public static function magnetize():void {
        var game:* = Main.instance != null ? Main.instance.game : null;
        var world:* = game != null ? game.world : null;
        if (world == null || world.myAvatar == null) return;
        var target:* = world.myAvatar.target;
'@
    $combatContent = $combatContent.Replace($magnetizeBefore, $magnetizeAfter)
    $combatContent = $combatContent.Replace(
        '            target.pMC.x = Main.instance.game.world.myAvatar.pMC.x;',
        '            target.pMC.x = world.myAvatar.pMC.x;'
    )
    $combatContent = $combatContent.Replace(
        '            target.pMC.y = Main.instance.game.world.myAvatar.pMC.y;',
        '            target.pMC.y = world.myAvatar.pMC.y;'
    )

    $infiniteRangeBefore = @'
    public static function infiniteRange():void {
        var active:Array = Main.instance.game.world.actions.active;
        for (var i:int = 0; i < 6; i++) {
            active[i].range = 20000;
'@
    $infiniteRangeAfter = @'
    public static function infiniteRange():void {
        var game:* = Main.instance != null ? Main.instance.game : null;
        var world:* = game != null ? game.world : null;
        if (world == null || world.actions == null || world.actions.active == null) return;
        var active:Array = world.actions.active;
        for (var i:int = 0; i < Math.min(6, active.length); i++) {
            if (active[i] != null) active[i].range = 20000;
'@
    $combatContent = $combatContent.Replace($infiniteRangeBefore, $infiniteRangeAfter)

    if (([string]::Equals($worldContentBeforePatch, $worldContent, [StringComparison]::Ordinal)) -or ([string]::Equals($combatContentBeforePatch, $combatContent, [StringComparison]::Ordinal))) {
        Write-Warning "The Skua gameplay guards did not match the expected source; using the existing compiled client."
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
    Set-Content -LiteralPath $patchedWorldPath -Value $worldContent -Encoding utf8
    Set-Content -LiteralPath $patchedCombatPath -Value $combatContent -Encoding utf8

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
    Write-Host "Downloading Ruffle $resolvedVersion..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath

    $extractPath = Join-Path $tempRoot "extracted"
    Expand-Archive -Path $archivePath -DestinationPath $extractPath -Force

    $ruffleEntry = Get-ChildItem -Path $extractPath -Filter "ruffle.js" -File -Recurse | Select-Object -First 1
    if ($null -eq $ruffleEntry) {
        throw "The Ruffle archive did not contain ruffle.js."
    }

    New-Item -ItemType Directory -Path $ruffleTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $ruffleEntry.DirectoryName "*") -Destination $ruffleTarget -Recurse -Force

    Set-Content -LiteralPath (Join-Path $runtimeRoot "ruffle-version.txt") -Value $resolvedVersion -NoNewline -Encoding utf8
    Write-Host "Ruffle $resolvedVersion installed to $ruffleTarget" -ForegroundColor Green

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
