# VoiceType — Reset local app data ("clean install" simulation)
# Moves %LOCALAPPDATA%\VoiceType to a timestamped backup so the next app start
# behaves like a first run: no settings.json, default AppSettings, model must be
# downloaded/pointed to again. Use -Full to also remove the MSIX package data.
#
# Usage:
#   .\reset-dev-data.ps1            # reset unpackaged data only (settings/models/sessions/temp)
#   .\reset-dev-data.ps1 -Full      # also uninstall the MSIX package + wipe its package data
#   .\reset-dev-data.ps1 -Restore   # restore the most recent backup
#
# The app must be CLOSED before running this script.

[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$dataRoot = Join-Path $env:LOCALAPPDATA 'VoiceType'
$packageFamily = 'DimQ1.VoiceType_310ax279fjzmt'

function Write-Step([string]$msg) { Write-Host "  -> $msg" -ForegroundColor Green }
function Write-Head([string]$msg) { Write-Host $msg -ForegroundColor Cyan }

# ---- Restore mode ----
if ($Restore) {
    Write-Head "=== VoiceType: restore latest data backup ==="
    $latest = Get-ChildItem "$env:LOCALAPPDATA\VoiceType.backup-*" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) {
        Write-Error "No backup found matching $env:LOCALAPPDATA\VoiceType.backup-*"
        exit 1
    }
    if (Test-Path $dataRoot) {
        Remove-Item $dataRoot -Recurse -Force
        Write-Step "Removed current $dataRoot"
    }
    Move-Item $latest.FullName $dataRoot
    Write-Step "Restored $($latest.Name) -> $dataRoot"
    Write-Host "Done." -ForegroundColor Cyan
    return
}

# ---- Reset mode ----
Write-Head "=== VoiceType: reset local app data ==="

# Safety: refuse to run while the app is running
$running = Get-Process -Name 'VoiceType.WinUI' -ErrorAction SilentlyContinue
if ($running) {
    Write-Error "VoiceType.WinUI is running (PID $($running.Id -join ', ')). Close it first."
    exit 1
}

# 1. Backup + remove unpackaged data root
if (Test-Path $dataRoot) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = "$dataRoot.backup-$stamp"
    Move-Item $dataRoot $backup
    Write-Step "Moved $dataRoot -> $backup"
} else {
    Write-Step "Nothing to reset: $dataRoot does not exist (already clean)"
}

# 2. Optional: full MSIX reset
if ($Full) {
    Write-Head "=== Full reset: MSIX package ==="
    $pkg = Get-AppxPackage -Name 'DimQ1.VoiceType' -ErrorAction SilentlyContinue
    if ($pkg) {
        $pkg | Remove-AppxPackage
        Write-Step "Uninstalled $($pkg.Name) v$($pkg.Version)"
    } else {
        Write-Step "MSIX package not installed"
    }

    $pkgData = Join-Path $env:LOCALAPPDATA "Packages\$packageFamily"
    if (Test-Path $pkgData) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        Move-Item $pkgData "$pkgData.backup-$stamp"
        Write-Step "Moved package data -> $pkgData.backup-$stamp"
    }
}

Write-Host ""
Write-Host "Done. Next app start will run with a fresh, first-launch state." -ForegroundColor Cyan
Write-Host "Restore with: .\reset-dev-data.ps1 -Restore" -ForegroundColor DarkGray
