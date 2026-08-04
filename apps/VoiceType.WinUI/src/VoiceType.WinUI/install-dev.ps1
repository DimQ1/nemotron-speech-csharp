# VoiceType — Local Dev Installer
# Installs the current MSIX built by build-dev.ps1 and trusts its generated .cer in CurrentUser\TrustedPeople.

$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $PSScriptRoot 'Package.appxmanifest'
[xml]$manifest = Get-Content -Path $manifestPath -Raw
$packageVersion = [string]$manifest.Package.Identity.Version
$packageArch = [string]$manifest.Package.Identity.ProcessorArchitecture
if ([string]::IsNullOrWhiteSpace($packageVersion)) {
    throw "Could not read package version from $manifestPath"
}
if ([string]::IsNullOrWhiteSpace($packageArch)) {
    throw "Could not read ProcessorArchitecture from $manifestPath"
}

$appPackagesDir = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages'
$packageDir = Join-Path $appPackagesDir "VoiceType.WinUI_${packageVersion}_x64_Test"
$msixPath = Join-Path $packageDir "VoiceType.WinUI_${packageVersion}_x64.msix"
$cerPath = Join-Path $packageDir "VoiceType.WinUI_${packageVersion}_x64.cer"

Write-Host "=== VoiceType Dev Installer ===" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $msixPath)) {
    throw "MSIX not found: $msixPath`nRun .\\build-dev.ps1 first."
}

if (-not (Test-Path $cerPath)) {
    throw "Signing certificate not found: $cerPath`nRun .\\build-dev.ps1 first."
}

# 1. Trust package signer cert for current user (no admin required)
Write-Host "[1/3] Trusting dev certificate (CurrentUser\\TrustedPeople)..." -ForegroundColor Yellow
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
Write-Host "  -> Certificate trusted for current user" -ForegroundColor Green

# 2. Install or update package (data preserved by AppX deployment)
Write-Host "[2/3] Installing package version $packageVersion..." -ForegroundColor Yellow
$depPaths = @()
$depRoot = Join-Path $packageDir 'Dependencies'
if (Test-Path $depRoot)
{
    $archDepRoot = Join-Path $depRoot $packageArch
    if (Test-Path $archDepRoot)
    {
        $depPaths = Get-ChildItem -Path $archDepRoot -Filter '*.msix' | Select-Object -ExpandProperty FullName
    }
    else
    {
        # Fallback for uncommon layouts where per-arch folders are not used.
        $depPaths = Get-ChildItem -Path $depRoot -Filter '*.msix' | Select-Object -ExpandProperty FullName
    }
}

if ($depPaths.Count -gt 0) {
    Add-AppxPackage -Path $msixPath -DependencyPath $depPaths -ForceApplicationShutdown
}
else {
    Add-AppxPackage -Path $msixPath -ForceApplicationShutdown
}

# 3. Verify + launch
Write-Host "[3/3] Verifying installation..." -ForegroundColor Yellow
$pkg = Get-AppxPackage -Name DimQ1.VoiceType -ErrorAction SilentlyContinue
if (-not $pkg) {
    throw "Package DimQ1.VoiceType was not found after install."
}

Write-Host "Installed: $($pkg.Name) v$($pkg.Version) -- $($pkg.Status)" -ForegroundColor Green
Write-Host "Launching..." -ForegroundColor Cyan
Start-Process "shell:AppsFolder\DimQ1.VoiceType_310ax279fjzmt!App"

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
