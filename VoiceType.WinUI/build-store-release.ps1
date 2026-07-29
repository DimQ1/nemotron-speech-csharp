# VoiceType.WinUI — Store Release Build Script
# ==============================================
# Builds the MSIX package for Microsoft Store submission.
#
# Prerequisites:
#   1. Windows App SDK installed (already in project via NuGet)
#   2. .NET 10 SDK
#   3. EV Code Signing certificate (for signing)
#
# Usage:
#   .\build-store-release.ps1                  # x64, unsigned (WACK testing)
#   .\build-store-release.ps1 -Arch arm64       # ARM64 build (Surface Pro X, etc.)
#   .\build-store-release.ps1 -Sign             # With code signing (Store submission)
#   .\build-store-release.ps1 -Sign -CertThumbprint "ABCD1234..."
#
# Output:
#   VoiceType.WinUI\bin\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages\
#     └─ VoiceType.WinUI_1.0.0.0_x64.msix     (≈30-40 MB)
#     └─ VoiceType.WinUI_1.0.0.0_x64.msixupload (with signing only)

param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Arch = 'x64',

    [switch]$Sign,

    [string]$CertThumbprint = '',

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$csprojPath = Join-Path $PSScriptRoot 'VoiceType.WinUI.csproj'

Write-Host '═══════════════════════════════════════════' -ForegroundColor Cyan
Write-Host '  VoiceType.WinUI — Store Release Build' -ForegroundColor Cyan
Write-Host "  Architecture: $Arch" -ForegroundColor Cyan
Write-Host '═══════════════════════════════════════════' -ForegroundColor Cyan
Write-Host ''

# 1. Clean
if ($Clean) {
    Write-Host '[1/3] Cleaning...' -ForegroundColor Yellow
    $cleanDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows10.0.26100.0\win-$Arch"
    if (Test-Path $cleanDir) { Remove-Item -Recurse -Force $cleanDir }
    dotnet clean $csprojPath -c Release -p:Platform=$Arch -p:GpuArch=CPU -v q
    if ($LASTEXITCODE -ne 0) { throw 'Clean failed' }
}

# 2. Publish → MSIX (PublishProfile auto-selects win-{Arch}.pubxml)
Write-Host '[2/3] Publishing MSIX package...' -ForegroundColor Yellow
$publishArgs = @(
    'publish', $csprojPath,
    '-c', 'Release',
    "-p:Platform=$Arch",
    '-p:GpuArch=CPU'
)

if ($Sign -and $CertThumbprint) {
    $publishArgs += '-p:AppxPackageSigningEnabled=true'
    $publishArgs += "-p:PackageCertificateThumbprint=$CertThumbprint"
    Write-Host '  🔐 Signing with certificate thumbprint' -ForegroundColor Green
}
elseif ($Sign) {
    Write-Host '  🔐 Signing with default dev certificate' -ForegroundColor Green
    $publishArgs += '-p:AppxPackageSigningEnabled=true'
}
else {
    $publishArgs += '-p:AppxPackageSigningEnabled=false'
    Write-Host '  ⚠️  Unsigned package — for WACK testing only.' -ForegroundColor DarkYellow
    Write-Host '     Use -Sign -CertThumbprint for Store submission.' -ForegroundColor DarkYellow
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

# 3. Find the generated MSIX
$appPackagesDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows10.0.26100.0\win-$Arch\AppPackages"
$msixFile = Get-ChildItem -Path $appPackagesDir -Filter '*.msix' -Recurse -ErrorAction SilentlyContinue `
    | Where-Object { $_.Name -like 'VoiceType.WinUI*' } `
    | Select-Object -First 1
$msixUpload = Get-ChildItem -Path $appPackagesDir -Filter '*.msixupload' -Recurse -ErrorAction SilentlyContinue `
    | Where-Object { $_.Name -like 'VoiceType.WinUI*' } `
    | Select-Object -First 1

Write-Host ''
Write-Host '═══════════════════════════════════════════' -ForegroundColor Green
Write-Host '  ✅ Build Complete!' -ForegroundColor Green
Write-Host '═══════════════════════════════════════════' -ForegroundColor Green

if ($msixFile) {
    $sizeMB = [math]::Round($msixFile.Length / 1MB, 1)
    Write-Host "  📦 MSIX: $($msixFile.FullName)" -ForegroundColor White
    Write-Host "     Size: $sizeMB MB" -ForegroundColor White
}

if ($msixUpload) {
    $sizeMB = [math]::Round($msixUpload.Length / 1MB, 1)
    Write-Host "  📤 MSIX Upload: $($msixUpload.FullName)" -ForegroundColor White
    Write-Host "     Size: $sizeMB MB" -ForegroundColor White
    Write-Host ''
    Write-Host '  👉 Upload this .msixupload file to Partner Center → Packages' -ForegroundColor Green
}

Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Cyan
Write-Host '  1. Run Windows App Cert Kit (WACK) on the .msix' -ForegroundColor White
Write-Host '  2. Upload .msixupload to Partner Center → Packages' -ForegroundColor White
Write-Host '  3. Complete Store listing (descriptions, screenshots)' -ForegroundColor White
Write-Host '  4. Submit for certification' -ForegroundColor White
Write-Host ''
Write-Host '  📖 Full guide: VoiceType.WinUI\StoreSubmissionGuide.md' -ForegroundColor DarkGray
