# VoiceType.WinUI — Local Dev Build (signed)
# Builds a local MSIX signed with a development certificate from CurrentUser\My.

param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'

$csprojPath = Join-Path $PSScriptRoot 'VoiceType.WinUI.csproj'
$manifestPath = Join-Path $PSScriptRoot 'Package.appxmanifest'
[xml]$manifest = Get-Content -Path $manifestPath -Raw
$publisher = [string]$manifest.Package.Identity.Publisher
$packageVersion = [string]$manifest.Package.Identity.Version

if ([string]::IsNullOrWhiteSpace($publisher)) {
    throw "Could not read Publisher from $manifestPath"
}

if ([string]::IsNullOrWhiteSpace($packageVersion)) {
    throw "Could not read Version from $manifestPath"
}

Write-Host '========================================' -ForegroundColor Cyan
Write-Host ' VoiceType.WinUI — Dev MSIX Build' -ForegroundColor Cyan
Write-Host " Arch: $Arch" -ForegroundColor Cyan
Write-Host " Publisher: $publisher" -ForegroundColor Cyan
Write-Host " Version: $packageVersion" -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# Reuse existing sign-capable cert for this Publisher or create one.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host '[1/2] Creating new dev code-signing certificate...' -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $publisher `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyExportPolicy Exportable `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(5)
}
else {
    Write-Host '[1/2] Reusing existing dev certificate...' -ForegroundColor Yellow
}

Write-Host "  -> Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green

Write-Host '[2/2] Publishing signed MSIX...' -ForegroundColor Yellow
$publishArgs = @(
    'publish', $csprojPath,
    '-c', 'Release',
    "-p:Platform=$Arch",
    '-p:GpuArch=CPU',
    '-p:AppxPackageSigningEnabled=true',
    "-p:PackageCertificateThumbprint=$($cert.Thumbprint)"
)

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Publish failed'
}

$appPackagesDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows10.0.26100.0\win-$Arch\AppPackages"
$packageDir = Join-Path $appPackagesDir "VoiceType.WinUI_${packageVersion}_${Arch}_Test"
$msixPath = Join-Path $packageDir "VoiceType.WinUI_${packageVersion}_${Arch}.msix"
$cerPath = Join-Path $packageDir "VoiceType.WinUI_${packageVersion}_${Arch}.cer"

Write-Host ''
Write-Host 'Build complete:' -ForegroundColor Green
Write-Host "  MSIX: $msixPath" -ForegroundColor White
Write-Host "  CER:  $cerPath" -ForegroundColor White
Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host '  .\install-dev.ps1' -ForegroundColor White
