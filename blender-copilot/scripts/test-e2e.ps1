[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$blenderExe = "C:\Program Files\Blender Foundation\Blender 5.3\blender.exe"
if (-not (Test-Path $blenderExe)) {
    throw "Blender 5.3 was not found at '$blenderExe'."
}

$hostExe = Join-Path $repoRoot "dist\bin\BlenderCopilotHost.exe"
if (-not (Test-Path $hostExe)) {
    $hostExe = Join-Path $repoRoot "src\BlenderCopilot.Host\bin\Release\net10.0-windows10.0.19041.0\BlenderCopilotHost.exe"
}
if (-not (Test-Path $hostExe)) {
    throw "BlenderCopilotHost.exe was not found. Run scripts\build.ps1 first."
}

$bridgeExe = Join-Path (Split-Path -Parent $hostExe) "bridge\AcadCopilotMcp.exe"
if (-not (Test-Path $bridgeExe)) {
    throw "AcadCopilotMcp.exe was not found at '$bridgeExe'."
}

$dotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
if (Test-Path $dotnetDir) {
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = "$dotnetDir;$env:PATH"
}

$bridgeVer = (& $bridgeExe --version).Trim()
Write-Host "1. Verified MCP Bridge binary: AcadCopilotMcp.exe v$bridgeVer" -ForegroundColor Green

$addonDir = Join-Path $repoRoot "addon"
$artifactsDir = Join-Path $repoRoot "artifacts\ui"
New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null

# Start Blender 5.3 in background mode hosting the live TCP worker and test BlenderCopilotHost self-test against it
$pyScript = @"
import sys, time
sys.path.insert(0, r'$addonDir')
import blender_copilot
blender_copilot.register()
w = blender_copilot.get_worker()
print(f'WORKER_PORT={w.port}', flush=True)
start = time.time()
while time.time() - start < 45:
    w.pump_main_thread()
    time.sleep(0.03)
blender_copilot.unregister()
"@

$tempPy = Join-Path $env:TEMP "blender_copilot_e2e_$PID.py"
Set-Content -Path $tempPy -Value $pyScript -Encoding UTF8

Write-Host "2. Starting Blender 5.3.0 with blender_copilot add-on..." -ForegroundColor Cyan
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $blenderExe
$psi.Arguments = "--background --python `"$tempPy`""
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$blenderProc = [System.Diagnostics.Process]::Start($psi)
$workerPort = 0
try {
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not $blenderProc.HasExited) {
        $line = $blenderProc.StandardOutput.ReadLine()
        if ($null -ne $line -and $line -match "WORKER_PORT=(\d+)") {
            $workerPort = [int]$Matches[1]
            break
        }
    }

    if ($workerPort -le 0) {
        throw "Blender 5.3 did not report WORKER_PORT."
    }
    Write-Host "3. Blender 5.3 worker listening on TCP port $workerPort (PID $($blenderProc.Id))." -ForegroundColor Green

    Write-Host "4. Running BlenderCopilotHost end-to-end pipe & UI test against Blender 5.3..." -ForegroundColor Cyan
    $dotnetExe = Join-Path $dotnetDir "dotnet.exe"
    $hostDll = Join-Path (Split-Path -Parent $hostExe) "BlenderCopilotHost.dll"
    & $dotnetExe $hostDll --self-test --blender-pid $blenderProc.Id --worker-port $workerPort --artifacts-dir $artifactsDir
    if ($LASTEXITCODE -ne 0) {
        throw "BlenderCopilotHost --self-test failed with exit code $LASTEXITCODE."
    }
    Write-Host "5. End-to-end Blender 5.3 + Named Pipe + WPF Panel test PASSED!" -ForegroundColor Green
}
finally {
    if (-not $blenderProc.HasExited) {
        $blenderProc.Kill()
    }
    Remove-Item -Path $tempPy -Force -ErrorAction SilentlyContinue
}
