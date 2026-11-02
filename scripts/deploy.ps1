<#
.SYNOPSIS
    Copies the built plugin and JS plugins to a Valheim target.

.DESCRIPTION
    Paths come from .env at the project root (BEPINEX_PLUGINS / SERVER_BEPINEX_PATH plus the
    matching server_plugins folder). Run task build/bundle first; this script only deploys.

    The game must be closed: a loaded plugin DLL cannot be overwritten.

.PARAMETER Target
    client or server.

.PARAMETER ProjectRoot
    Project root containing .env. Defaults to the parent of this script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('client', 'server')]
    [string]$Target,

    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Read-DotEnv([string]$path) {
    $values = @{}
    if (-not (Test-Path -LiteralPath $path)) { return $values }
    foreach ($line in Get-Content -LiteralPath $path) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
            $values[$Matches[1]] = $Matches[2].Trim().Trim('"')
        }
    }
    return $values
}

$envFile = Join-Path $ProjectRoot '.env'
$settings = Read-DotEnv $envFile
if ($settings.Count -eq 0) { throw "No .env found at $envFile" }

$bepInEx = if ($Target -eq 'server') { $settings['SERVER_BEPINEX_PATH'] } else { $settings['BEPINEX_PLUGINS'] }
$pluginRoot = if ($Target -eq 'server') { $settings['SERVER_PLUGINS'] } else { $settings['CLIENT_PLUGINS'] }
$gamePath = if ($Target -eq 'server') { $settings['SERVER_GAME_PATH'] } else { $settings['GAME_PATH'] }

if (-not $bepInEx -or -not (Test-Path -LiteralPath $bepInEx)) {
    throw "$Target target is not configured: set SERVER_BEPINEX_PATH / BEPINEX_PLUGINS in .env"
}
if (-not $pluginRoot) {
    throw "$Target target has no plugin folder configured (SERVER_PLUGINS / CLIENT_PLUGINS)"
}

$buildDir = Join-Path $ProjectRoot 'bin/Release/netstandard2.1'
$jsOut = Join-Path $ProjectRoot 'scripting/dist'

$runtimeFiles = @('ValheimJS.dll', 'Jint.dll', 'Acornima.dll', 'ValheimJS.pdb')
$pluginDir = if ($bepInEx -like '*\plugins') { Join-Path $bepInEx 'ValheimJS' } else { Join-Path $bepInEx 'plugins/ValheimJS' }

foreach ($file in $runtimeFiles) {
    $source = Join-Path $buildDir $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing build output: $source (run 'task build' first)"
    }
}

if (-not (Test-Path -LiteralPath $jsOut)) {
    throw "Missing JS bundles: $jsOut (run 'task bundle' first)"
}

# Only the process this target belongs to can lock the files we are about to replace.
$processName = if ($Target -eq 'server') { 'valheim_server' } else { 'valheim' }
$running = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning ("{0} is running ({1}); plugin files will fail to overwrite. Close it first." -f $running[0].ProcessName, $running[0].Id)
}

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
foreach ($file in $runtimeFiles) {
    $source = Join-Path $buildDir $file
    Copy-Item -LiteralPath $source -Destination $pluginDir -Force
}

New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
Copy-Item -Path (Join-Path $jsOut '*') -Destination $pluginRoot -Recurse -Force

Write-Output "Deployed $Target plugin -> $pluginDir"
Write-Output "Deployed $Target plugins -> $pluginRoot"
if ($gamePath) { Write-Output "Target game: $gamePath" }
