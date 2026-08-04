# Store Association Helper
# =======================
# After reserving your app name in Partner Center, run this to update the manifest.
#
# Usage:
#   .\associate-store.ps1 -AppName "12345YourCompany.VoiceType" -Publisher "CN=ABCD1234-5678-..." -DisplayName "Your Publisher Name"
#
# Or copy-paste values from Partner Center → Product Management → Product Identity

param(
    [Parameter(Mandatory=$true)]
    [string]$AppName,

    [Parameter(Mandatory=$true)]
    [string]$Publisher,

    [Parameter(Mandatory=$true)]
    [string]$DisplayName,

    [string]$Version = '1.0.1.0'
)

$manifestPath = Join-Path $PSScriptRoot 'Package.appxmanifest'
Write-Host "Updating: $manifestPath" -ForegroundColor Cyan

$content = Get-Content $manifestPath -Raw

# Replace Identity Name (the current GUID)
$content = $content -replace 'Name="[^"]*"', "Name=`"$AppName`""

# Replace Publisher
$content = $content -replace 'Publisher="[^"]*"', "Publisher=`"$Publisher`""

# Replace PublisherDisplayName
$content = $content -replace '<PublisherDisplayName>[^<]*</PublisherDisplayName>', "<PublisherDisplayName>$DisplayName</PublisherDisplayName>"

# Update version
$content = $content -replace 'Version="\d+\.\d+\.\d+\.\d+"', "Version=`"$Version`""

Set-Content $manifestPath $content -NoNewline
Write-Host '✅ Package.appxmanifest updated with Store identity:' -ForegroundColor Green
Write-Host "   App Name:     $AppName" -ForegroundColor White
Write-Host "   Publisher:    $Publisher" -ForegroundColor White
Write-Host "   Display Name: $DisplayName" -ForegroundColor White
Write-Host "   Version:      $Version" -ForegroundColor White
Write-Host ''
Write-Host 'Next: run .\build-store-release.ps1' -ForegroundColor Cyan
