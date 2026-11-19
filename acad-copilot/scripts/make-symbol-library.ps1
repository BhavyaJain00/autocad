<#
.SYNOPSIS
    Generates the electrical / plumbing symbol library of the bundle,
    deploy\AcadCopilot.bundle\Contents\Blocks\AcadCopilot-Services.dxf, and validates it by parsing it back.

.DESCRIPTION
    The library is data in src\AcadCopilot.Core\Services\SymbolLibrary.cs, written as an AutoCAD R12 DXF (BLOCK +
    ATTDEF per symbol, a catalogue sheet of INSERT + ATTRIB) by SymbolLibraryDxf. No AutoCAD is needed or touched.
    Runs the small generator scripts\SymbolLibrary under the shared build lock <repo>\.build.lock with its own
    artifacts folder, then 'dotnet build-server shutdown'. build.ps1 copies the file into the bundle (it is part of the
    bundle template) and checks it is there; a Core test fails when the file is out of date with the code.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-symbol-library.ps1
#>
[CmdletBinding()]
param(
    [string] $Output = ''
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Output) { $Output = Join-Path $repo 'deploy\AcadCopilot.bundle\Contents\Blocks\AcadCopilot-Services.dxf' }
$project = Join-Path $repo 'scripts\SymbolLibrary\SymbolLibraryGen.csproj'
$artifacts = Join-Path ([System.IO.Path]::GetTempPath()) ('acadcopilot-symbols-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$lockPath = Join-Path $repo '.build.lock'

$dotnet = $env:DOTNET_EXE
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'No dotnet found (set DOTNET_EXE).' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

$held = $false
$deadline = (Get-Date).AddMinutes(60)
while (-not $held) {
    try {
        New-Item -ItemType File -Path $lockPath -ErrorAction Stop | Out-Null
        $held = $true
    }
    catch {
        $existing = Get-Item -LiteralPath $lockPath -ErrorAction SilentlyContinue
        if ($existing -and ((Get-Date) - $existing.LastWriteTime).TotalMinutes -gt 12) {
            Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
            continue
        }
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for the build lock $lockPath." }
        Write-Host '    waiting for the build lock...'
        Start-Sleep -Seconds 15
    }
}

try {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $dotnet run --project $project -c Release --artifacts-path $artifacts -p:UseSharedCompilation=false -- $Output | Out-Host
    $exit = $LASTEXITCODE
    & $dotnet build-server shutdown | Out-Null
    $ErrorActionPreference = $previous
    if ($exit -ne 0) { throw "The symbol library generator failed (exit $exit)." }
}
finally {
    Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $artifacts -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Symbol library ready: $Output" -ForegroundColor Green
