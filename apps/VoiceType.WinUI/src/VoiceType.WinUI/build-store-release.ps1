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
#   VoiceType.WinUI\AppPackages\VoiceType.WinUI_<version>_x64_Test\
#     └─ VoiceType.WinUI_<version>_x64.msix     (local install / WACK / Store)
#     └─ VoiceType.WinUI_<version>_x64.msixupload (only with Store-associated packaging)

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

# 2. Publish → MSIX
#    SelfContained=true bundles the .NET 10 runtime into the package — required for
#    Microsoft Store distribution, where the .NET runtime is NOT auto-installed.
#    PublishAppxPackage=true triggers single-project MSIX packaging during `dotnet publish`
#    (no publish profile is committed to git — the csproj defaults to framework-dependent).
Write-Host '[2/3] Publishing MSIX package...' -ForegroundColor Yellow
$publishArgs = @(
    'publish', $csprojPath,
    '-c', 'Release',
    "-p:Platform=$Arch",
    '-p:GpuArch=CPU',
    '-p:SelfContained=true',
    '-p:PublishAppxPackage=true',
    '-p:AppxBundle=Never'
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
#    Single-project MSIX packaging outputs to <ProjectDir>\AppPackages\ (not under bin\)
#    when publishing from the command line without an explicit OutDir.
$appPackagesDir = Join-Path $PSScriptRoot 'AppPackages'
$manifestPath = Join-Path $PSScriptRoot 'Package.appxmanifest'
[xml]$manifest = Get-Content -Path $manifestPath -Raw
$packageVersion = [string]$manifest.Package.Identity.Version
if ([string]::IsNullOrWhiteSpace($packageVersion)) {
    throw "Could not read package version from $manifestPath"
}

$msixFile = Get-ChildItem -Path $appPackagesDir -Filter '*.msix' -Recurse -ErrorAction SilentlyContinue `
    | Where-Object { $_.Name -like "VoiceType.WinUI_${packageVersion}_*.msix" } `
    | Sort-Object LastWriteTime -Descending `
    | Select-Object -First 1
$msixUpload = Get-ChildItem -Path $appPackagesDir -Filter '*.msixupload' -Recurse -ErrorAction SilentlyContinue `
    | Where-Object { $_.Name -like "VoiceType.WinUI_${packageVersion}_*.msixupload" } `
    | Sort-Object LastWriteTime -Descending `
    | Select-Object -First 1

if (-not $msixFile) {
    throw "Could not find a VoiceType MSIX for package version $packageVersion under $appPackagesDir"
}

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
else {
    Write-Host ''
    Write-Host '  ℹ️  No .msixupload was generated by the current publish profile.' -ForegroundColor DarkYellow
    Write-Host '     The signed .msix is ready for local installation and WACK testing.' -ForegroundColor DarkYellow
    Write-Host '     Use a Store-associated packaging profile to create the Partner Center upload archive.' -ForegroundColor DarkYellow
}

Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Cyan
Write-Host '  1. Run Windows App Cert Kit (WACK) on the .msix' -ForegroundColor White
if ($msixUpload) {
    Write-Host '  2. Upload .msixupload to Partner Center → Packages' -ForegroundColor White
}
else {
    Write-Host '  2. Create a Store-associated upload archive in the packaging workflow' -ForegroundColor White
}
Write-Host '  3. Complete Store listing (descriptions, screenshots)' -ForegroundColor White
Write-Host '  4. Submit for certification' -ForegroundColor White
Write-Host ''
Write-Host '  📖 Full guide: VoiceType.WinUI\StoreSubmissionGuide.md' -ForegroundColor DarkGray
