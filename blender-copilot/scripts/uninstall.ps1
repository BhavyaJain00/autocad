[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BlenderVersion = "5.3",
    [switch]$RemoveUserData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$blenderConfig = Join-Path $env:APPDATA "Blender Foundation\Blender\$BlenderVersion"
$addonsDir = Join-Path $blenderConfig "scripts\addons\blender_copilot"
$startupScript = Join-Path $blenderConfig "scripts\startup\blender_copilot_autostart.py"
$localHome = Join-Path $env:LOCALAPPDATA "BlenderCopilot"
$localBin = Join-Path $localHome "bin"

if ($PSCmdlet.ShouldProcess("BlenderCopilotHost", "Stop running host process")) {
    Get-Process -Name "BlenderCopilotHost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

foreach ($path in @($startupScript, $addonsDir, $localBin)) {
    if (Test-Path $path) {
        if ($PSCmdlet.ShouldProcess($path, "Remove")) {
            Remove-Item -Path $path -Recurse -Force
            Write-Host "Removed $path"
        }
    }
}

if ($RemoveUserData -and (Test-Path $localHome)) {
    if ($PSCmdlet.ShouldProcess($localHome, "Remove user data and logs")) {
        Remove-Item -Path $localHome -Recurse -Force
        Write-Host "Removed $localHome"
    }
}

Write-Host "AI Copilot for Blender $BlenderVersion uninstalled." -ForegroundColor Green
