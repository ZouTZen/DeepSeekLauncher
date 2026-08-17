# promote-new.ps1 — replace the running DshDesktop.exe with the verified new
# build (DshDesktop.new.exe, default port 3080).
#
# The running launcher locks DshDesktop.exe, so the replacement cannot happen
# while its window is open. Steps:
#   1. Close the DeepSeek Harness window (this destroys the session / releases
#      port 3080 and the exe file lock).
#   2. Run this script (double-click or from PowerShell).
#   3. Double-click dist\DshDesktop.exe — the new build (single instance,
#      launcher log, kill-on-close Job Object, auto-locate node/CLI) starts on
#      the default port 3080.
#
# Does NOT auto-start the exe. Idempotent: safe to rerun after the rename.

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist  = Join-Path $root 'dist'
$new   = Join-Path $dist 'DshDesktop.new.exe'
$final = Join-Path $dist 'DshDesktop.exe'

if (-not (Test-Path $new)) { throw "new build not found: $new (run build.ps1 -OutputName DshDesktop.new.exe first)" }

# Wait for any running DshDesktop instance to exit (poll up to 60s).
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    $running = Get-Process DshDesktop -ErrorAction SilentlyContinue
    if (-not $running) { break }
    Write-Host "Waiting for running launcher to exit ($($running.Count) instance(s))..."
    Start-Sleep -Seconds 2
}

$running = Get-Process DshDesktop -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "ERROR: launcher still running. Close the window first, then rerun this script." -ForegroundColor Red
    exit 1
}

# Back up the old exe, then promote the new one to the canonical name.
if (Test-Path $final) {
    Copy-Item -Path $final -Destination (Join-Path $dist 'DshDesktop.old.exe') -Force
    Write-Host "Backed up old exe -> DshDesktop.old.exe" -ForegroundColor Yellow
}
Copy-Item -Path $new -Destination $final -Force
Remove-Item -Path $new -Force

Write-Host "Promoted $new -> $final" -ForegroundColor Green
Write-Host "Done. Double-click dist\DshDesktop.exe to start on port 3080." -ForegroundColor Green
