<#
.SYNOPSIS
    Removes AI Copilot for AutoCAD 2027 for the current Windows user.

.DESCRIPTION
    Removes %APPDATA%\Autodesk\ApplicationPlugins\AcadCopilot.bundle and any AcadCopilot.bundle.new /
    AcadCopilot.bundle.old-<timestamp> folders left by install.ps1.

    - Refuses while AutoCAD (acad.exe) is running: the loaded plug-in files are locked. Close AutoCAD first.
      (-WhatIf still previews.)
    - -RemoveUserData also deletes %LOCALAPPDATA%\AcadCopilot (or ACADCOPILOT_HOME): panel settings, logs, discovery
      files and the CLIs' private working folders.
    - Never touches AutoCAD's settings (if you added the bundle to Trusted Locations, remove it there yourself) and
      never touches the Claude Code / Codex / Gemini installations, logins or configuration.

    Supports -WhatIf and -Confirm.

.PARAMETER PluginsDir
    Plug-in folder (default %APPDATA%\Autodesk\ApplicationPlugins).

.PARAMETER RemoveUserData
    Also delete %LOCALAPPDATA%\AcadCopilot.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -WhatIf
    powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
    powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -RemoveUserData
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $PluginsDir = '',
    [switch] $RemoveUserData
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$bundleName = 'AcadCopilot.bundle'
if (-not $PluginsDir) { $PluginsDir = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }

$acad = @(Get-Process -Name 'acad' -ErrorAction SilentlyContinue)
if ($acad.Count -gt 0) {
    $message = "AutoCAD is running (acad.exe, process $($acad.Id -join ', ')). Close AutoCAD, then run the uninstaller again."
    if ($WhatIfPreference) { Write-Warning "$message (WhatIf: preview only.)" } else { throw $message }
}

$folders = @(Get-ChildItem -LiteralPath $PluginsDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq $bundleName -or $_.Name -eq "$bundleName.new" -or $_.Name -like "$bundleName.old-*" })
$leftOver = @()

if ($folders.Count -eq 0) {
    Write-Host "No AI Copilot bundle in $PluginsDir."
}
foreach ($folder in $folders) {
    if ($PSCmdlet.ShouldProcess($folder.FullName, 'Remove the plug-in bundle')) {
        try { Remove-Item -LiteralPath $folder.FullName -Recurse -Force }
        catch { $leftOver += $folder.FullName }
    }
}

if ($RemoveUserData) {
    $dataDir = if ($env:ACADCOPILOT_HOME) { $env:ACADCOPILOT_HOME } else { Join-Path $env:LOCALAPPDATA 'AcadCopilot' }
    if (Test-Path -LiteralPath $dataDir) {
        if ($PSCmdlet.ShouldProcess($dataDir, 'Remove settings, logs and working folders')) {
            try { Remove-Item -LiteralPath $dataDir -Recurse -Force }
            catch { $leftOver += $dataDir }
        }
    }
}

foreach ($otherRoot in @((Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins'), (Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins'))) {
    $other = Join-Path $otherRoot $bundleName
    if (Test-Path -LiteralPath $other) {
        Write-Warning "A copy for all users remains in '$other' (not removed: administrator rights needed)."
    }
}

Write-Host ''
if ($WhatIfPreference) {
    Write-Host 'WhatIf: nothing was changed.' -ForegroundColor Green
}
elseif ($leftOver.Count -gt 0) {
    Write-Warning ('Still in use and kept: ' + ($leftOver -join ', '))
    Write-Host 'Close any program using AcadCopilotMcp.exe (Claude Desktop, a CLI session) and run this script again.' -ForegroundColor Yellow
}
else {
    Write-Host 'AI Copilot for AutoCAD 2027 was removed. AutoCAD no longer loads it at its next start.' -ForegroundColor Green
}
