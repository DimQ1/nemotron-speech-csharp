# VoiceType (Uno, WinAppSDK head) — Microsoft Store MSIX
# =========================================================
# Builds the MSIX that replaces VoiceType.WinUI in the Microsoft Store. The package keeps the WinUI identity
# (DimQ1.VoiceType, Packaging/Windows/Package.appxmanifest), so the Store delivers it as an update and the app finds the
# WinUI data folder as its own (WinUiDataImport converts settings.json and moves the translation model at the first start).
#
# Usage:
#   .\build-store-msix.ps1 -Version 2.0.0                 # Store upload: unsigned .msix (Partner Center re-signs it)
#   .\build-store-msix.ps1 -Version 2.0.0 -Preview -Install
#       # local test: identity DimQ1.VoiceType.UnoPreview ("VoiceType Preview"), signed with a dev certificate,
#       # installed next to VoiceType.WinUI without touching its data
#   .\build-store-msix.ps1 -Version 2.0.0 -Sign -Install -Force
#       # the real identity, signed with a dev certificate: an in-place update of a locally installed VoiceType.WinUI
#       # (this is the Store update on this machine: it converts THAT data)
#
# Output: bin\StorePackages\<identity>_<version>_x64_<signed|unsigned>.msix (the Store upload is DimQ1.VoiceType_..._unsigned.msix)
# Details: docs/store/uno-store-release.md

param(
    # MAJOR.MINOR.PATCH (or MAJOR.MINOR.PATCH.0): the Store needs a 4-part version ending in .0, higher than the last submission.
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # Separate identity for local tests (another package family = another data folder).
    [switch]$Preview,

    # Sign with a dev certificate whose subject is the manifest Publisher (created in CurrentUser\My when missing).
    [switch]$Sign,

    # Trust the certificate for the current user and install the package (implies -Sign).
    [switch]$Install,

    # Allow -Install of the real identity while DimQ1.VoiceType (VoiceType.WinUI) is installed.
    [switch]$Force,

    [string]$CertThumbprint = ''
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$csproj = Join-Path $projectDir 'VoiceType.Uno.csproj'
$tfm = 'net10.0-windows10.0.26100'

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(\.0)?$') {
    throw "Version '$Version' must be MAJOR.MINOR.PATCH (the Store adds .0; the fourth part must be 0)."
}
$displayVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
$packageVersion = "$displayVersion.0"
if ([version]$packageVersion -le [version]'1.3.4.0') {
    throw "Version $packageVersion is not higher than the last VoiceType.WinUI submission (1.3.4.0)."
}
if ($Install) { $Sign = $true }

# 1. The manifest for this build: version (and the preview identity) written into a copy under obj\.
$source = Join-Path $projectDir 'Packaging\Windows\Package.appxmanifest'
[xml]$manifest = Get-Content -Path $source -Raw
$manifest.Package.Identity.Version = $packageVersion
if ($Preview) {
    $manifest.Package.Identity.Name = 'DimQ1.VoiceType.UnoPreview'
    $manifest.Package.Properties.DisplayName = 'VoiceType Preview'
    $manifest.Package.Applications.Application.VisualElements.DisplayName = 'VoiceType Preview'
}
$identity = [string]$manifest.Package.Identity.Name
$publisher = [string]$manifest.Package.Identity.Publisher
$manifestDir = Join-Path $projectDir 'obj\store-manifest'
New-Item -ItemType Directory -Force $manifestDir | Out-Null
$manifestPath = Join-Path $manifestDir 'Package.appxmanifest'
$manifest.Save($manifestPath)

Write-Host '==========================================' -ForegroundColor Cyan
Write-Host ' VoiceType (Uno WinAppSDK) - Store MSIX' -ForegroundColor Cyan
Write-Host " Identity:  $identity" -ForegroundColor Cyan
Write-Host " Version:   $packageVersion" -ForegroundColor Cyan
Write-Host " Signed:    $Sign" -ForegroundColor Cyan
Write-Host '==========================================' -ForegroundColor Cyan

if ($Install -and -not $Preview -and -not $Force -and (Get-AppxPackage -Name 'DimQ1.VoiceType')) {
    throw 'DimQ1.VoiceType (VoiceType.WinUI) is installed: installing the real identity UPDATES it and converts its data. Use -Preview, or -Force to do exactly that.'
}

# 2. Certificate (only for a signed local package; the Store upload stays unsigned).
$signArgs = @('-p:AppxPackageSigningEnabled=false')
if ($Sign) {
    $cert = $null
    if ($CertThumbprint) {
        $cert = Get-Item "Cert:\CurrentUser\My\$CertThumbprint"
    }
    else {
        $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
        if (-not $cert) {
            Write-Host 'Creating a dev code-signing certificate in CurrentUser\My...' -ForegroundColor Yellow
            $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher -CertStoreLocation 'Cert:\CurrentUser\My' `
                -KeyExportPolicy Exportable -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
        }
    }
    Write-Host "  Certificate: $($cert.Thumbprint)" -ForegroundColor Green
    $signArgs = @('-p:AppxPackageSigningEnabled=true', "-p:PackageCertificateThumbprint=$($cert.Thumbprint)")
}

# 3. Publish: self-contained .NET + WinAppSDK, one x64 .msix.
$publishArgs = @(
    'publish', $csproj, '-c', 'Release', '-f', $tfm, '-r', 'win-x64',
    '-p:VoiceTypeMsix=true', '-p:GenerateAppxPackageOnBuild=true', '-p:SelfContained=true',
    "-p:WindowsAppxManifestPath=$manifestPath",
    "-p:ApplicationDisplayVersion=$displayVersion"
) + $signArgs
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$packageDir = Join-Path $projectDir "bin\Release\$tfm\win-x64\AppPackages\VoiceType.Uno_${packageVersion}_x64_Test"
$built = Join-Path $packageDir "VoiceType.Uno_${packageVersion}_x64.msix"
if (-not (Test-Path $built)) { throw "No package at $built" }
# The Store and the preview build write the same AppPackages folder: keep each under its identity.
$outDir = Join-Path $projectDir 'bin\StorePackages'
New-Item -ItemType Directory -Force $outDir | Out-Null
$kind = if ($Sign) { 'signed' } else { 'unsigned' }
$msix = Join-Path $outDir "${identity}_${packageVersion}_x64_$kind.msix"
Copy-Item -LiteralPath $built -Destination $msix -Force
Write-Host ''
Write-Host ("MSIX: {0} ({1:N0} MB)" -f $msix, ((Get-Item $msix).Length / 1MB)) -ForegroundColor Green

# 4. Optional local install.
if ($Install) {
    $cer = Join-Path $outDir "$($cert.Thumbprint).cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
    Add-AppxPackage -Path $msix -ForceApplicationShutdown
    $installed = Get-AppxPackage -Name $identity
    Write-Host "Installed $($installed.Name) $($installed.Version) ($($installed.PackageFamilyName))" -ForegroundColor Green
    Write-Host "Data folder: $env:LOCALAPPDATA\Packages\$($installed.PackageFamilyName)\LocalCache\Local\VoiceType" -ForegroundColor White
}
elseif (-not $Sign) {
    Write-Host 'Unsigned: upload this .msix to Partner Center > Packages (the Store signs it). Run WACK on it first.' -ForegroundColor White
}
