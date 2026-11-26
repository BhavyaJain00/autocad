[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BlenderVersion = "5.3"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distBin = Join-Path $repoRoot "dist\bin"
$srcBin = Join-Path $repoRoot "src\BlenderCopilot.Host\bin\Release\net10.0-windows10.0.19041.0"
$binSource = if (Test-Path (Join-Path $distBin "BlenderCopilotHost.exe")) { $distBin } else { $srcBin }

if (-not (Test-Path (Join-Path $binSource "BlenderCopilotHost.exe"))) {
    throw "BlenderCopilotHost.exe not found. Run scripts\build.ps1 first."
}

$addonSource = Join-Path $repoRoot "addon\blender_copilot"
$blenderConfig = Join-Path $env:APPDATA "Blender Foundation\Blender\$BlenderVersion"
$addonsDir = Join-Path $blenderConfig "scripts\addons\blender_copilot"
$startupDir = Join-Path $blenderConfig "scripts\startup"
$startupScript = Join-Path $startupDir "blender_copilot_autostart.py"

$localHome = Join-Path $env:LOCALAPPDATA "BlenderCopilot"
$localBin = Join-Path $localHome "bin"

if ($PSCmdlet.ShouldProcess($localBin, "Install BlenderCopilotHost.exe and MCP bridge")) {
    # Stop any running BlenderCopilotHost.exe before replacing binaries
    Get-Process -Name "BlenderCopilotHost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path $localBin) {
        Remove-Item -Path $localBin -Recurse -Force
    }
    New-Item -ItemType Directory -Path $localBin -Force | Out-Null
    Copy-Item -Path (Join-Path $binSource "*") -Destination $localBin -Recurse -Force
    Get-ChildItem -Path $localBin -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
}

if ($PSCmdlet.ShouldProcess($addonsDir, "Install blender_copilot add-on into Blender $BlenderVersion")) {
    if (Test-Path $addonsDir) {
        Remove-Item -Path $addonsDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $addonsDir -Force | Out-Null
    Copy-Item -Path (Join-Path $addonSource "*") -Destination $addonsDir -Recurse -Force
    Get-ChildItem -Path $addonsDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
}

$autostartCode = @"
# Auto-loads AI Copilot for Blender 5.3 Desktop at startup.
import bpy

def _enable_blender_copilot():
    try:
        import blender_copilot
        if getattr(blender_copilot, "get_worker", lambda: None)() is None:
            blender_copilot.register()
    except Exception as exc:
        print(f"[AI Copilot] Startup registration error: {exc}")
    return None

def register():
    if not bpy.app.timers.is_registered(_enable_blender_copilot):
        bpy.app.timers.register(_enable_blender_copilot, first_interval=0.25, persistent=True)

def unregister():
    pass
"@

if ($PSCmdlet.ShouldProcess($startupScript, "Install Blender $BlenderVersion startup autoloader")) {
    New-Item -ItemType Directory -Path $startupDir -Force | Out-Null
    Set-Content -Path $startupScript -Value $autostartCode -Encoding UTF8
}

Write-Host ""
Write-Host "AI Copilot for Blender $BlenderVersion installed!" -ForegroundColor Green
Write-Host "  Host & MCP Bridge : $localBin"
Write-Host "  Blender Add-on    : $addonsDir"
Write-Host "  Startup Autoload  : $startupScript"
Write-Host "  Knowledge & Logs  : $localHome"
Write-Host ""
Write-Host "Open 'Blender 5.3' from your Desktop shortcut: the AI Copilot panel docks on the right automatically." -ForegroundColor Cyan
