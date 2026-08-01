# VoiceType — Local Dev Installer (requires admin)
# Installs the MSIX package and trusts the signing certificate in LocalMachine stores.

$ErrorActionPreference = 'Stop'
$rootCerPath = "$env:TEMP\VoiceType-Root.cer"
$signerCerPath = "$env:TEMP\VoiceType-Dev.cer"
$manifestPath = Join-Path $PSScriptRoot 'Package.appxmanifest'
[xml]$manifest = Get-Content -Path $manifestPath -Raw
$packageVersion = [string]$manifest.Package.Identity.Version
if ([string]::IsNullOrWhiteSpace($packageVersion)) {
    throw "Could not read package version from $manifestPath"
}

$appPackagesDir = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages'
$msixPath = Join-Path $appPackagesDir "VoiceType.WinUI_${packageVersion}_x64_Test\VoiceType.WinUI_${packageVersion}_x64.msix"

Write-Host "=== VoiceType Dev Installer ===" -ForegroundColor Cyan
Write-Host ""

# 1. Trust the ROOT CA in LocalMachine\Root (required for MSIX chain validation)
Write-Host "[1/3] Trusting Root CA..." -ForegroundColor Yellow

if (-not (Test-Path $rootCerPath)) {
    Write-Error "Root CA not found: $rootCerPath"
    exit 1
}

Import-Certificate -FilePath $rootCerPath -CertStoreLocation Cert:\LocalMachine\Root
Write-Host "  -> Root CA trusted" -ForegroundColor Green

Import-Certificate -FilePath $rootCerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Write-Host "  -> TrustedPeople OK" -ForegroundColor Green

# 2. Keep any existing package data; Add-AppxPackage upgrades older versions in place.
Write-Host "[2/3] Preparing package update..." -ForegroundColor Yellow
$existingPackage = Get-AppxPackage -Name DimQ1.VoiceType -ErrorAction SilentlyContinue
if ($existingPackage) {
    Write-Host "  -> Updating v$($existingPackage.Version) to v$packageVersion (application data preserved)" -ForegroundColor Green
} else {
    Write-Host "  -> Fresh install" -ForegroundColor Green
}

# 3. Install MSIX
Write-Host "[3/3] Installing VoiceType $msixPath..." -ForegroundColor Yellow
Add-AppxPackage -Path $msixPath

# 4. Verify
Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
$pkg = Get-AppxPackage -Name DimQ1.VoiceType -ErrorAction SilentlyContinue
if ($pkg) {
    Write-Host "Installed: $($pkg.Name) v$($pkg.Version) -- $($pkg.Status)" -ForegroundColor Green
    Write-Host "Launching..." -ForegroundColor Cyan
    Start-Process "shell:AppsFolder\DimQ1.VoiceType_310ax279fjzmt!App"
} else {
    Write-Host "ERROR: Package not found after install" -ForegroundColor Red
    exit 1
}

pause
