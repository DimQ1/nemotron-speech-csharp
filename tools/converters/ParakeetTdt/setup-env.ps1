# Creates a Python venv for Parakeet TDT ONNX conversion and installs deps.
# NOTE: NeMo 2.4 officially supports Python 3.10–3.12. If the system python is
# outside that range, install Python 3.12 first or use conda (recommended).
[CmdletBinding()]
param(
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot

$venv = Join-Path $scriptDir ".venv"
if (-not (Test-Path $venv)) {
    Write-Host "Creating venv at $venv ..."
    & $Python -m venv $venv
}

$py = Join-Path $venv "Scripts\python.exe"
& $py -m pip install --upgrade pip
& $py -m pip install -r (Join-Path $scriptDir "requirements.txt")

Write-Host ""
Write-Host "Environment ready. Activate with:" -ForegroundColor Green
Write-Host "  & $($venv)\Scripts\Activate.ps1"
