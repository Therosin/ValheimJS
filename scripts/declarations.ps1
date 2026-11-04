# Generates the SDK's TypeScript declarations from the compiled plugin's binding metadata.
# -Check regenerates into a temp file and fails when the checked-in copy has drifted.
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'scripting/sdk/api/generated.ts'
$harness = Join-Path $root 'harness/Harness.csproj'
$assembly = Join-Path $root 'bin/Release/netstandard2.1/ValheimJS.dll'

if (-not $env:MANAGED_PATH) { throw 'MANAGED_PATH is missing from the environment (.env).' }
if (-not (Test-Path $assembly)) { throw "Plugin assembly not found: $assembly (run 'task build')." }

$arguments = @(
    'run', '--project', $harness, '-c', 'Release', '--',
    '--declarations', '--assembly', $assembly, '--managed', $env:MANAGED_PATH
)

if ($env:BEPINEX_PATH) { $arguments += @('--references', (Join-Path $env:BEPINEX_PATH 'core')) }

$temp = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "valheimjs-declarations-$PID.ts")

try {
    & dotnet @arguments --out $temp
    if ($LASTEXITCODE -ne 0) { throw 'Declaration generation failed.' }

    if ($Check) {
        $expected = Get-Content $target -Raw
        $actual = Get-Content $temp -Raw
        if ($expected -ne $actual) {
            throw "Generated declarations drifted from $target. Run 'task declarations' and commit the result."
        }

        Write-Output 'Declarations are up to date.'
    }
    else {
        Copy-Item $temp $target -Force
        Write-Output "Updated $target"
    }
}
finally {
    if (Test-Path $temp) { Remove-Item $temp -Force }
}
