<#
.SYNOPSIS
    Installs AI Copilot for AutoCAD 2027 for the current Windows user.

.DESCRIPTION
    Copies the bundle made by scripts\build.ps1 (dist\AcadCopilot.bundle, or a AcadCopilot.bundle-<version>.zip) to

        %APPDATA%\Autodesk\ApplicationPlugins\AcadCopilot.bundle\
            PackageContents.xml                 read by AutoCAD's autoloader at start-up
            Contents\AcadCopilot.Plugin.dll ... the plug-in
            Contents\bridge\AcadCopilotMcp.exe  the AutoCAD MCP server the CLIs start

    AutoCAD 2027 loads it at its next start. No administrator rights are needed.

    - Refuses while AutoCAD (acad.exe) is running: AutoCAD keeps the loaded DLLs open and only looks for plug-ins
      when it starts. Close AutoCAD first. (-WhatIf still previews.)
    - Refuses while the installed AcadCopilotMcp.exe is running (for example started by your own Claude Desktop).
    - Copies into a staging folder first and swaps it in only when the copy is complete, so a failed copy never leaves
      a half-installed bundle.
    - Removes the "downloaded from the internet" mark from the files (Unblock-File), without which .NET refuses them.
    - Warns about a second copy in an all-users plug-in folder (AutoCAD would load two).
    - Never starts, stops or changes AutoCAD or its settings. Never touches the Claude Code / Codex / Gemini
      installations, logins or configuration.

    Supports -WhatIf (shows every action, changes nothing) and -Confirm.

.PARAMETER BundlePath
    The bundle folder (containing PackageContents.xml) or a .zip made by build.ps1 -Zip
    (default <repo>\dist\AcadCopilot.bundle).

.PARAMETER PluginsDir
    Target plug-in folder (default %APPDATA%\Autodesk\ApplicationPlugins).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -WhatIf
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -BundlePath C:\Downloads\AcadCopilot.bundle-0.1.0.zip
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $BundlePath = '',
    [string] $PluginsDir = ''
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$bundleName = 'AcadCopilot.bundle'
if (-not $BundlePath) { $BundlePath = Join-Path (Split-Path $PSScriptRoot -Parent) "dist\$bundleName" }
if (-not $PluginsDir) { $PluginsDir = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }
$target = Join-Path $PluginsDir $bundleName
$staging = "$target.new"

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------- source ----------
$extracted = $null
if (-not (Test-Path -LiteralPath $BundlePath)) { throw "No bundle at '$BundlePath'. Run scripts\build.ps1 first." }

try {
    if ((Get-Item -LiteralPath $BundlePath).PSIsContainer) {
        $source = (Resolve-Path -LiteralPath $BundlePath).Path
    }
    else {
        if ([System.IO.Path]::GetExtension($BundlePath) -ne '.zip') { throw "'$BundlePath' is neither a bundle folder nor a .zip." }
        # Extracted to a temporary folder, also under -WhatIf so the preview can check the contents. .NET's ZipFile is
        # used because Expand-Archive of Windows PowerShell 5.1 fails when the caller runs with -WhatIf.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $extracted = Join-Path ([System.IO.Path]::GetTempPath()) ("AcadCopilot-install-" + [Guid]::NewGuid().ToString('N'))
        [System.IO.Directory]::CreateDirectory($extracted) | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $BundlePath).Path, $extracted)
        $manifestFile = Get-ChildItem -LiteralPath $extracted -Recurse -Filter 'PackageContents.xml' | Select-Object -First 1
        if (-not $manifestFile) { throw "The zip '$BundlePath' contains no PackageContents.xml." }
        $source = $manifestFile.DirectoryName
    }

    foreach ($required in @('PackageContents.xml', 'Contents\AcadCopilot.Plugin.dll', 'Contents\AcadCopilot.Core.dll', 'Contents\bridge\AcadCopilotMcp.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required))) {
            throw "The bundle in '$source' has no $required. Run scripts\build.ps1 again."
        }
    }

    $version = (Get-Item -LiteralPath (Join-Path $source 'Contents\AcadCopilot.Plugin.dll')).VersionInfo.ProductVersion
    if (-not $version) { $version = '0.0.0' }
    $version = ($version -split '\+')[0]

    Write-Step "Installing AI Copilot for AutoCAD 2027 $version"
    Write-Host "  from  $source"
    Write-Host "  to    $target"

    # ---------- refuse while in use ----------
    $acad = @(Get-Process -Name 'acad' -ErrorAction SilentlyContinue)
    if ($acad.Count -gt 0) {
        $message = "AutoCAD is running (acad.exe, process $($acad.Id -join ', ')). Close AutoCAD, then run the installer again."
        if ($WhatIfPreference) { Write-Warning "$message (WhatIf: preview only.)" } else { throw $message }
    }

    $bridgeInUse = @(Get-Process -Name 'AcadCopilotMcp' -ErrorAction SilentlyContinue | Where-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $path -and $path.StartsWith($target, [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($bridgeInUse.Count -gt 0) {
        $message = "The installed AcadCopilotMcp.exe is running (process $($bridgeInUse.Id -join ', ')), probably started by Claude Desktop or a CLI session that uses it. Close that program, then run the installer again."
        if ($WhatIfPreference) { Write-Warning "$message (WhatIf: preview only.)" } else { throw $message }
    }

    # ---------- other copies ----------
    foreach ($otherRoot in @((Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins'), (Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins'))) {
        $other = Join-Path $otherRoot $bundleName
        if (Test-Path -LiteralPath $other) {
            Write-Warning "Another copy is installed for all users in '$other'. AutoCAD may load both; remove one of them (administrator rights needed for that folder)."
        }
    }

    # ---------- copy (staging, then swap) ----------
    if (-not (Test-Path -LiteralPath $PluginsDir)) {
        if ($PSCmdlet.ShouldProcess($PluginsDir, 'Create the plug-in folder')) {
            New-Item -ItemType Directory -Path $PluginsDir -Force | Out-Null
        }
    }

    if ($PSCmdlet.ShouldProcess($staging, "Copy the bundle from $source")) {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        Copy-Item -LiteralPath $source -Destination $staging -Recurse -Force
        Get-ChildItem -LiteralPath $staging -Recurse -File | Unblock-File
    }

    $backup = $null
    if (Test-Path -LiteralPath $target) {
        $backup = "$target.old-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        if ($PSCmdlet.ShouldProcess($target, 'Move the previous installation aside')) {
            Rename-Item -LiteralPath $target -NewName (Split-Path $backup -Leaf)
        }
    }

    if ($PSCmdlet.ShouldProcess($target, 'Activate the new bundle')) {
        try {
            Rename-Item -LiteralPath $staging -NewName $bundleName
        }
        catch {
            # Put the previous installation back so AutoCAD still finds a complete bundle.
            if ($backup -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {
                Rename-Item -LiteralPath $backup -NewName $bundleName
            }
            throw
        }
    }

    # ---------- clean up older copies ----------
    $leftovers = @(Get-ChildItem -LiteralPath $PluginsDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$bundleName.old-*" })
    foreach ($folder in $leftovers) {
        if ($PSCmdlet.ShouldProcess($folder.FullName, 'Remove the previous installation')) {
            try { Remove-Item -LiteralPath $folder.FullName -Recurse -Force }
            catch { Write-Warning "Could not remove $($folder.FullName): $($_.Exception.Message)" }
        }
    }

    # ---------- environment hints (read-only) ----------
    $runtimeOk = $false
    $runtimeFolder = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
    if (Test-Path -LiteralPath $runtimeFolder) {
        $runtimeOk = [bool](Get-ChildItem -LiteralPath $runtimeFolder -Directory | Where-Object { $_.Name -like '10.*' })
    }
    if (-not $runtimeOk) {
        Write-Warning 'No .NET 10 runtime in Program Files\dotnet. AutoCAD 2027 normally installs it; AcadCopilotMcp.exe needs it. Install ".NET Runtime 10" from https://dotnet.microsoft.com/download/dotnet/10.0 if the panel says the AutoCAD connector cannot start.'
    }

    foreach ($cli in @('claude', 'codex', 'gemini')) {
        if (-not (Get-Command $cli -ErrorAction SilentlyContinue)) {
            Write-Warning "'$cli' is not on PATH: that provider shows as 'Not installed' in the panel (see docs\USER-GUIDE.md)."
        }
    }

    Write-Host ''
    if ($WhatIfPreference) {
        Write-Host 'WhatIf: nothing was changed.' -ForegroundColor Green
    }
    else {
        Write-Host "Installed AI Copilot for AutoCAD 2027 $version in $target." -ForegroundColor Green
    }
    Write-Host 'Next: start AutoCAD 2027 and open a drawing. If AutoCAD asks whether to load AcadCopilot.Plugin.dll'
    Write-Host '(it is not digitally signed), choose Load. The AI Copilot panel docks on the right; the COPILOT command or the'
    Write-Host '"AI Copilot" ribbon tab shows it again.'
    Write-Host "MCP server for your own Claude Desktop / Claude Code / Codex / Gemini: $(Join-Path $target 'Contents\bridge\AcadCopilotMcp.exe')"
}
finally {
    if ($extracted -and (Test-Path -LiteralPath $extracted)) {
        try { [System.IO.Directory]::Delete($extracted, $true) } catch { Write-Warning "Could not remove the temporary folder $extracted." }
    }
}
