<#
.SYNOPSIS
    Builds AI Copilot for AutoCAD 2027 in Release and assembles the AutoCAD autoloader bundle
    dist\AcadCopilot.bundle.

.DESCRIPTION
    1. Builds src\AcadCopilot.Plugin. That build also builds AcadCopilot.Core and the MCP bridge
       (AcadCopilot.McpBridge) and copies the bridge into the plug-in's bridge\ output folder.
       The build goes to its own folder, artifacts\build\ (dotnet --artifacts-path: bin\<project>\<config> and
       obj\<project>\<config>), so design-time builds of an open IDE and other builds working in the projects' own
       bin\ and obj\ folders cannot disturb it (WPF's generated *.g.cs files vanishing from a shared obj\ folder made
       the packaging build fail). -InPlace builds into the projects' own bin\ and obj\ folders instead.
    2. With -Test, runs every test project under tests\, one after the other, in place (their own bin\ and obj\, the
       layout the tests expect). UI tests render off-screen only.
    3. Assembles the bundle from the template deploy\AcadCopilot.bundle\ and the build output:
         dist\AcadCopilot.bundle\PackageContents.xml       from the template (AppVersion / Version = build version)
         dist\AcadCopilot.bundle\Contents\                 AcadCopilot.Plugin.dll, AcadCopilot.Core.dll (+ .pdb/.deps.json)
         dist\AcadCopilot.bundle\Contents\bridge\          AcadCopilotMcp.exe + its dependencies
       Autodesk assemblies are never copied (they are referenced with Private=false; any file whose company is
       Autodesk is skipped as well).
    4. Checks the bundle: plug-in, Core and bridge present, every ModuleName in PackageContents.xml exists, no
       Autodesk assembly, and AcadCopilotMcp.exe answers --version.
    5. With -Zip, also writes dist\AcadCopilot.bundle-<version>.zip.

    Every dotnet build/test runs under the shared build lock <repo>\.build.lock (one build at a time on this PC; a lock
    older than 12 minutes is treated as stale). Builds use -m:1 -nodeReuse:false and no shared compiler server, and the
    script ends with 'dotnet build-server shutdown', so no build process is left running.

    Works in Windows PowerShell 5.1 and PowerShell 7. Opens no windows. Never starts or touches AutoCAD.

.PARAMETER Configuration
    Build configuration (default Release).

.PARAMETER AcadInstallDir
    AutoCAD 2027 folder with acmgd.dll, acdbmgd.dll, accoremgd.dll and AdWindows.dll
    (default: AcadInstallDir from Directory.Build.props, C:\Program Files\Autodesk\AutoCAD 2027\).

.PARAMETER Test
    Also run every test project under tests\ before packaging.

.PARAMETER Zip
    Also write dist\AcadCopilot.bundle-<version>.zip.

.PARAMETER InPlace
    Build into the projects' own bin\ and obj\ folders instead of artifacts\build\.

.PARAMETER NoLock
    Do not use the build lock (only when nothing else builds on this PC).

.PARAMETER LockTimeoutMinutes
    How long to wait for the build lock before giving up (default 60).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Test -Zip
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $AcadInstallDir = '',
    [switch] $Test,
    [switch] $Zip,
    [switch] $InPlace,
    [switch] $NoLock,
    [ValidateRange(1, 600)]
    [int] $LockTimeoutMinutes = 60
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pluginProject = Join-Path $repo 'src\AcadCopilot.Plugin\AcadCopilot.Plugin.csproj'
$bundleTemplate = Join-Path $repo 'deploy\AcadCopilot.bundle'
$manifestSource = Join-Path $bundleTemplate 'PackageContents.xml'
if (-not (Test-Path -LiteralPath $manifestSource) -and (Test-Path -LiteralPath (Join-Path $repo 'deploy\PackageContents.xml'))) {
    $bundleTemplate = $null
    $manifestSource = Join-Path $repo 'deploy\PackageContents.xml'
}
$artifacts = Join-Path $repo 'artifacts\build'
if ($InPlace) {
    $pluginOut = Join-Path $repo "src\AcadCopilot.Plugin\bin\$Configuration\net10.0-windows10.0.19041.0"
    $bridgeOut = Join-Path $repo "src\AcadCopilot.McpBridge\bin\$Configuration\net10.0"
}
else {
    # dotnet --artifacts-path layout: bin\<project>\<configuration in lower case> for single-target projects.
    $pivot = $Configuration.ToLowerInvariant()
    $pluginOut = Join-Path $artifacts "bin\AcadCopilot.Plugin\$pivot"
    $bridgeOut = Join-Path $artifacts "bin\AcadCopilot.McpBridge\$pivot"
}
$dist = Join-Path $repo 'dist'
$bundle = Join-Path $dist 'AcadCopilot.bundle'
$contents = Join-Path $bundle 'Contents'
$contentsBridge = Join-Path $contents 'bridge'
$lockPath = Join-Path $repo '.build.lock'
$script:lockHeld = $false

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

# Runs a console program and returns its exit code and stdout lines (stderr discarded). Windows PowerShell 5.1 turns
# redirected stderr into errors, so the error preference is relaxed while it runs.
function Invoke-Native([string] $exe, [string[]] $arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $exe @arguments 2>$null)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Find-Dotnet {
    $candidates = @()
    if ($env:DOTNET_EXE) { $candidates += $env:DOTNET_EXE }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            $sdks = (Invoke-Native $candidate @('--list-sdks')).Output
            if ($sdks | Where-Object { $_ -match '^10\.' }) { return $candidate }
        }
    }
    throw 'No .NET 10 SDK found (looked at DOTNET_EXE, %LOCALAPPDATA%\Microsoft\dotnet and Program Files\dotnet). Install it or set DOTNET_EXE.'
}

function Enter-BuildLock {
    if ($NoLock) { return }
    $deadline = (Get-Date).AddMinutes($LockTimeoutMinutes)
    $announced = $false
    while ($true) {
        try {
            New-Item -ItemType File -Path $lockPath -ErrorAction Stop | Out-Null
            $script:lockHeld = $true
            return
        }
        catch {
            $existing = Get-Item -LiteralPath $lockPath -ErrorAction SilentlyContinue
            if ($existing -and ((Get-Date) - $existing.LastWriteTime).TotalMinutes -gt 12) {
                Write-Warning "Removing a stale build lock from $($existing.LastWriteTime)."
                Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
                continue
            }
            if (-not $existing -and -not (Test-Path -LiteralPath (Split-Path $lockPath -Parent))) { throw }
            if ((Get-Date) -gt $deadline) { throw "Timed out after $LockTimeoutMinutes min waiting for the build lock $lockPath (another build is running)." }
            if (-not $announced) { Write-Host "    waiting for the build lock ($lockPath)..."; $announced = $true }
            Start-Sleep -Seconds 15
        }
    }
}

function Exit-BuildLock {
    if ($script:lockHeld) {
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
        $script:lockHeld = $false
    }
}

# Runs one dotnet command under the build lock, its output shown on the console; returns its exit code.
function Invoke-Dotnet([string[]] $arguments) {
    Enter-BuildLock
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $script:dotnet @arguments | Out-Host
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
        Exit-BuildLock
    }
}

function Test-AutodeskFile([System.IO.FileInfo] $file) {
    if ($file.Extension -notin @('.dll', '.exe')) { return $false }
    if ($file.Name -in @('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll', 'AdWindows.dll', 'AcWindows.dll', 'AcCui.dll', 'AcMr.dll', 'AcTcMgd.dll')) { return $true }
    $company = $file.VersionInfo.CompanyName
    return [bool]($company -and $company -match 'Autodesk')
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$script:dotnet = Find-Dotnet
Write-Step "Using $script:dotnet"

if (-not (Test-Path -LiteralPath $pluginProject)) { throw "Plug-in project not found: $pluginProject" }
if (-not (Test-Path -LiteralPath $manifestSource)) { throw "Bundle manifest not found: $manifestSource" }

$commonArgs = @('-c', $Configuration, '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-v:minimal', '-clp:NoSummary')
if ($AcadInstallDir) {
    if (-not $AcadInstallDir.EndsWith('\')) { $AcadInstallDir += '\' }
    $commonArgs += "-p:AcadInstallDir=$AcadInstallDir"
    $acadDir = $AcadInstallDir
}
else {
    $acadDir = 'C:\Program Files\Autodesk\AutoCAD 2027\'
}
foreach ($reference in @('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll', 'AdWindows.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $acadDir $reference))) {
        throw "$reference not found in '$acadDir'. Pass -AcadInstallDir <AutoCAD 2027 folder>."
    }
}
# The package is built in its own artifacts folder; the tests run in place, where they expect their bin\ layout
# (the MCP end-to-end test starts src\AcadCopilot.McpBridge\bin\<Config>\net10.0\AcadCopilotMcp.exe).
$buildArgs = if ($InPlace) { $commonArgs } else { $commonArgs + @('--artifacts-path', $artifacts) }

try {
    Write-Step "Building the plug-in, Core and the MCP bridge ($Configuration)"
    $exit = Invoke-Dotnet (@('build', $pluginProject) + $buildArgs)
    if ($exit -ne 0) {
        # A stale incremental state (for example WPF's generated *.g.cs files missing from obj\) fails the build even
        # though the sources are fine; one full rebuild clears it. Real compile errors fail again.
        Write-Warning "Build failed (exit $exit); retrying once as a full rebuild (--no-incremental)."
        $exit = Invoke-Dotnet (@('build', $pluginProject, '--no-incremental') + $buildArgs)
        if ($exit -ne 0) { throw "Build failed (exit $exit)." }
    }

    if ($Test) {
        $testProjects = @(Get-ChildItem (Join-Path $repo 'tests') -Recurse -Filter '*.Tests.csproj' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object Name)
        if ($testProjects.Count -eq 0) { Write-Warning 'No test projects found under tests\.' }
        $failed = @()
        foreach ($testProject in $testProjects) {
            Write-Step "Testing $($testProject.BaseName)"
            $exit = Invoke-Dotnet (@('test', $testProject.FullName) + $commonArgs)
            if ($exit -ne 0) { $failed += $testProject.BaseName }
        }
        if ($failed.Count -gt 0) { throw "Tests failed: $($failed -join ', ')" }
    }
}
finally {
    # Stop compiler/MSBuild servers (ours and stale ones) while holding the lock, so no build is cut off.
    $null = Invoke-Dotnet @('build-server', 'shutdown')
}

$pluginDll = Join-Path $pluginOut 'AcadCopilot.Plugin.dll'
if (-not (Test-Path -LiteralPath $pluginDll) -and -not $InPlace) {
    # Should the artifacts layout ever differ (for example a configuration-and-framework pivot), find the output.
    $found = Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin\AcadCopilot.Plugin') -Recurse -Filter 'AcadCopilot.Plugin.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -ne 'bridge' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) {
        $pluginOut = $found.DirectoryName
        $pluginDll = $found.FullName
    }
}
if (-not (Test-Path -LiteralPath $pluginDll)) { throw "Build output missing: $pluginDll" }

Write-Step "Assembling $bundle"
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
New-Item -ItemType Directory -Path $contentsBridge -Force | Out-Null

# Extra files of the bundle template (deploy\AcadCopilot.bundle), if any; PackageContents.xml is written below.
if ($bundleTemplate) {
    $templateRoot = (Resolve-Path -LiteralPath $bundleTemplate).Path.TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $templateRoot -Recurse -File | Where-Object { $_.Name -ne 'PackageContents.xml' }) {
        $target = Join-Path $bundle $file.FullName.Substring($templateRoot.Length).TrimStart('\')
        $targetDir = Split-Path $target -Parent
        if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

$skipped = @()
$pluginRoot = (Resolve-Path -LiteralPath $pluginOut).Path.TrimEnd('\')
foreach ($file in Get-ChildItem -LiteralPath $pluginRoot -Recurse -File) {
    $relative = $file.FullName.Substring($pluginRoot.Length).TrimStart('\')
    if ($file.Extension -eq '.xml') { continue }                       # API docs
    if (Test-AutodeskFile $file) { $skipped += $relative; continue }    # never ship Autodesk assemblies
    $target = Join-Path $contents $relative
    $targetDir = Split-Path $target -Parent
    if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
if ($skipped.Count -gt 0) { Write-Warning "Not shipped (Autodesk assemblies): $($skipped -join ', ')" }

# The plug-in build copies the bridge into bridge\; fall back to the bridge's own output if that step did not run.
$bridgeExe = Join-Path $contentsBridge 'AcadCopilotMcp.exe'
if (-not (Test-Path -LiteralPath $bridgeExe)) {
    if (-not (Test-Path -LiteralPath (Join-Path $bridgeOut 'AcadCopilotMcp.exe'))) {
        throw "The MCP bridge was not built: neither $bridgeExe nor $bridgeOut\AcadCopilotMcp.exe exists."
    }
    Write-Warning "bridge\ missing in the plug-in output; copying it from $bridgeOut."
    Get-ChildItem -LiteralPath $bridgeOut -File | Where-Object { $_.Extension -ne '.xml' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $contentsBridge -Force }
}

# Manifest: copy, with AppVersion set to the version that was built.
$version = (Get-Item -LiteralPath $pluginDll).VersionInfo.ProductVersion
if (-not $version) { $version = '0.0.0' }
$version = ($version -split '\+')[0]
[xml] $manifest = Get-Content -LiteralPath $manifestSource -Raw -Encoding UTF8
$package = $manifest.DocumentElement
if ($package.LocalName -ne 'ApplicationPackage') { throw "$manifestSource is not an ApplicationPackage manifest." }
if ($package.HasAttribute('AppVersion') -and $package.GetAttribute('AppVersion') -ne $version) {
    Write-Host "    AppVersion $($package.GetAttribute('AppVersion')) -> $version"
    $package.SetAttribute('AppVersion', $version)
}
foreach ($entry in @($manifest.SelectNodes('//*[local-name()="ComponentEntry"]'))) {
    if ($entry.HasAttribute('Version') -and $entry.GetAttribute('Version') -ne $version) {
        Write-Host "    ComponentEntry $($entry.GetAttribute('AppName')) Version $($entry.GetAttribute('Version')) -> $version"
        $entry.SetAttribute('Version', $version)
    }
}
$manifestTarget = Join-Path $bundle 'PackageContents.xml'
$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$writer = [System.Xml.XmlWriter]::Create($manifestTarget, $settings)
try { $manifest.Save($writer) } finally { $writer.Dispose() }

Write-Step 'Checking the bundle'
# Blocks\AcadCopilot-Services.dxf: the electrical / plumbing symbol library (bundle template; regenerate it with
# scripts\make-symbol-library.ps1, a Core test fails when it is out of date).
foreach ($required in @('AcadCopilot.Plugin.dll', 'AcadCopilot.Core.dll', 'bridge\AcadCopilotMcp.exe', 'bridge\AcadCopilotMcp.dll', 'bridge\AcadCopilotMcp.runtimeconfig.json', 'Blocks\AcadCopilot-Services.dxf')) {
    if (-not (Test-Path -LiteralPath (Join-Path $contents $required))) { throw "Bundle is missing Contents\$required" }
}

$modules = @($manifest.SelectNodes('//*[local-name()="ComponentEntry"]') | ForEach-Object { $_.GetAttribute('ModuleName') } | Where-Object { $_ })
if ($modules.Count -eq 0) { throw 'PackageContents.xml has no ComponentEntry with a ModuleName.' }
foreach ($module in $modules) {
    $modulePath = Join-Path $bundle ($module -replace '^\.[\\/]', '' -replace '/', '\')
    if (-not (Test-Path -LiteralPath $modulePath)) { throw "PackageContents.xml loads '$module', which is not in the bundle." }
}

$autodesk = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object { Test-AutodeskFile $_ })
if ($autodesk.Count -gt 0) { throw "Autodesk assemblies must not be shipped: $($autodesk.FullName -join ', ')" }

$bridgeRun = Invoke-Native $bridgeExe @('--version')
if ($bridgeRun.ExitCode -ne 0) { throw "AcadCopilotMcp.exe --version failed (exit $($bridgeRun.ExitCode)). Is the .NET 10 runtime installed?" }
$bridgeVersion = ($bridgeRun.Output -join ' ').Trim()

$files = @(Get-ChildItem -LiteralPath $bundle -Recurse -File)
$sizeMb = [Math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)

if ($Zip) {
    $zipPath = Join-Path $dist "AcadCopilot.bundle-$version.zip"
    Write-Step "Writing $zipPath"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path $bundle -DestinationPath $zipPath
}

Write-Host ''
Write-Host "Bundle ready: $bundle" -ForegroundColor Green
Write-Host "  plug-in  $version   ($($modules -join ', '))"
Write-Host "  bridge   $bridgeVersion"
Write-Host "  $($files.Count) files, $sizeMb MB"
Write-Host 'Install with: powershell -ExecutionPolicy Bypass -File scripts\install.ps1   (add -WhatIf to preview)'
