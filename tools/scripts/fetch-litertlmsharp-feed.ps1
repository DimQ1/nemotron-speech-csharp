<#
.SYNOPSIS
Downloads the LiteRtLmSharp packages this repository consumes into a local NuGet folder feed.

.DESCRIPTION
`SpeechLib.LiteRT.Native` references LiteRtLmSharp 1.4.0 (native LiteRT-LM v0.18.0), the fork's build
with the engine-usage improvements this repository uses — upstream's own 1.3.0 on nuget.org is a
different build without them. It is published from the fork https://github.com/DimQ1/LiteRtLmSharp to
its GitHub Packages feed, and the same .nupkg files are attached to the fork's `v<version>` GitHub
release. GitHub Packages requires an authenticated read even for public packages, so this script uses
the release assets instead — plain HTTPS, no token, no GitHub CLI — and drops them into the local
folder feed that nuget.config maps the LiteRtLmSharp* packages to.

Run once after cloning (and in CI before building a project that consumes SpeechLib.LiteRT.Native):

  pwsh tools/scripts/fetch-litertlmsharp-feed.ps1                    # the packages this repo references
  pwsh tools/scripts/fetch-litertlmsharp-feed.ps1 -Rid win-x64       # add/refresh one runtime package
  pwsh tools/scripts/fetch-litertlmsharp-feed.ps1 -Rid osx-arm64     # macOS publish (VoiceType.Uno references it for that RID only)
  pwsh tools/scripts/fetch-litertlmsharp-feed.ps1 -All               # every package the release carries
  pwsh tools/scripts/fetch-litertlmsharp-feed.ps1 -Version 1.4.0 -Force

Already-downloaded packages are skipped unless -Force is given, so the script is cheap to call from a
build script. Use the GitHub Packages feed directly (see the project README) if you prefer to consume
the published feed with a `read:packages` token.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.4.0',
    [string]$Repo = 'DimQ1/LiteRtLmSharp',
    [string]$Destination,
    # Package ids to fetch; empty = the ones this repository references (see the switch below).
    [string[]]$Package,
    # Runtime packages share the naming LiteRtLmSharp.runtime.<rid>; empty = the RIDs this repo references.
    [string[]]$Rid,
    [switch]$All,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# The packages a restore of this repository needs: SpeechLib.LiteRT.Native references the managed
# package plus the win-x64 / linux-x64 / android-arm64 runtime packages (apps publish per RID).
$referencedPackages = @('LiteRtLmSharp', 'LiteRtLmSharp.runtime.win-x64', 'LiteRtLmSharp.runtime.linux-x64',
                        'LiteRtLmSharp.runtime.android-arm64')
# Everything the 1.4.0 release carries (kept in sync with the runtime packages in LiteRtLmSharp.slnx).
$everyPackage = @('LiteRtLmSharp', 'LiteRtLmSharp.Extensions.AI', 'LiteRtLmSharp.SemanticKernel') +
    @('win-x64', 'linux-x64', 'linux-arm64', 'android-arm64', 'android-x64', 'osx-arm64') |
    ForEach-Object { if ($_ -like 'LiteRtLmSharp*') { $_ } else { "LiteRtLmSharp.runtime.$_" } }

$packages = if ($All) { $everyPackage } elseif ($Package) { $Package } else { $referencedPackages }
if ($Rid) { $packages += @($Rid | ForEach-Object { $_ -split ',' } | ForEach-Object { "LiteRtLmSharp.runtime.$($_.Trim())" }) }
$packages = $packages | Sort-Object -Unique

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dest = if ($Destination) { $Destination } else { Join-Path $repoRoot 'nuget-feed/litertlmsharp' }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$dest = (Resolve-Path $dest).Path

Write-Host "Feed: $dest"
Write-Host "Source: https://github.com/$Repo/releases/download/v$Version ($($packages.Count) package(s))"

$fetched = 0; $skipped = 0
foreach ($id in $packages) {
    $file = Join-Path $dest "$id.$Version.nupkg"
    if ((Test-Path -LiteralPath $file) -and -not $Force) {
        Write-Host "  skip   $id $Version (already in the feed)"
        $skipped++
        continue
    }
    # The asset name is deterministic, so no API call (and no rate limit) is involved.
    $url = "https://github.com/$Repo/releases/download/v$Version/$id.$Version.nupkg"
    Write-Host "  fetch  $id $Version"
    try {
        Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
    }
    catch {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        throw "Could not download $url — is v$Version published on $Repo? ($($_.Exception.Message))"
    }
    # A release asset that is a stray HTML error page would poison the feed silently; nupkg is a zip.
    $head = [System.IO.File]::ReadAllBytes($file)[0..1]
    if (-not ($head[0] -eq 0x50 -and $head[1] -eq 0x4B)) {
        Remove-Item -LiteralPath $file -Force
        throw "$id.$Version.nupkg is not a zip archive (PK header missing); the download did not return a package."
    }
    $fetched++
}

Write-Host "Done: $fetched fetched, $skipped already present, feed has $((Get-ChildItem $dest -Filter *.nupkg).Count) package(s)."
