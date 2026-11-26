[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Test
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$acadRoot = Join-Path (Split-Path -Parent $repoRoot) "acad-copilot"
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$artifactsBuild = Join-Path $repoRoot "artifacts\build"
$hostProj = Join-Path $repoRoot "src\BlenderCopilot.Host\BlenderCopilot.Host.csproj"

Write-Host "Building BlenderCopilot.Host ($Configuration) in isolated artifacts folder..." -ForegroundColor Cyan
& $dotnet build $hostProj -c $Configuration --artifacts-path $artifactsBuild -m:1 -nodeReuse:false -p:UseSharedCompilation=false
$exitCode = $LASTEXITCODE
& $dotnet build-server shutdown | Out-Null
if ($exitCode -ne 0) {
    throw "dotnet build failed with exit code $exitCode."
}

$cfgLower = $Configuration.ToLowerInvariant()
$binOut = Join-Path $artifactsBuild "bin\BlenderCopilot.Host\$cfgLower"
if (-not (Test-Path (Join-Path $binOut "BlenderCopilotHost.exe"))) {
    $binOut = Get-ChildItem -Path (Join-Path $artifactsBuild "bin\BlenderCopilot.Host") -Filter "BlenderCopilotHost.exe" -Recurse |
        Select-Object -First 1 -ExpandProperty DirectoryName
}

$distDir = Join-Path $repoRoot "dist"
$distBin = Join-Path $distDir "bin"
$distBridge = Join-Path $distBin "bridge"
$distAddon = Join-Path $distDir "addon\blender_copilot"

if (Test-Path $distDir) {
    Remove-Item -Path $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $distBin -Force | Out-Null
New-Item -ItemType Directory -Path $distBridge -Force | Out-Null
New-Item -ItemType Directory -Path $distAddon -Force | Out-Null

Copy-Item -Path (Join-Path $binOut "*") -Destination $distBin -Recurse -Force

# Copy the MCP bridge from AcadCopilot's dist/artifacts/bin
$bridgeCandidates = @(
    (Join-Path $artifactsBuild "bin\AcadCopilot.McpBridge\$cfgLower"),
    (Join-Path $acadRoot "dist\AcadCopilot.bundle\Contents\bridge"),
    (Join-Path $acadRoot "artifacts\build\bin\AcadCopilot.McpBridge\release"),
    (Join-Path $acadRoot "src\AcadCopilot.McpBridge\bin\Release\net10.0")
)
$bridgeSource = $bridgeCandidates | Where-Object { Test-Path (Join-Path $_ "AcadCopilotMcp.exe") } | Select-Object -First 1
if (-not $bridgeSource) {
    throw "AcadCopilotMcp.exe not found in $acadRoot."
}
Copy-Item -Path (Join-Path $bridgeSource "*") -Destination $distBridge -Recurse -Force
# Ensure updated AcadCopilot.Core.dll is in bridge too
Copy-Item -Path (Join-Path $distBin "AcadCopilot.Core.dll") -Destination $distBridge -Force

Copy-Item -Path (Join-Path $repoRoot "addon\blender_copilot\*") -Destination $distAddon -Recurse -Force

Write-Host "Package assembled in $distDir" -ForegroundColor Green

if ($Test) {
    & (Join-Path $PSScriptRoot "test-e2e.ps1")
}
