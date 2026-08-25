[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [ValidateSet("linux-x64")]
    [string]$Runtime = "linux-x64",
    [string]$Distribution = "Ubuntu-24.04",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^[0-9][0-9A-Za-z.+:~-]*$') {
    throw "'$Version' is not a valid Debian package version."
}

$projectDir = $PSScriptRoot
$workspace = (Resolve-Path (Join-Path $projectDir "../../../..")).Path
$project = Join-Path $projectDir "VoiceType.Uno.csproj"
$packagingDir = Join-Path $projectDir "Packaging/Linux"
$publishDir = Join-Path $workspace "build/voicetype-uno-linux-x64"
$stagingDir = Join-Path $workspace "build/deb/voicetype-uno_${Version}_amd64"
$outputDir = Join-Path $workspace "build/linux-packages"
$outputFile = Join-Path $outputDir "voicetype-uno_${Version}_amd64.deb"
$installDir = Join-Path $stagingDir "opt/voicetype-uno"

function Convert-ToWslPath {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if ($resolvedPath.Length -lt 3 -or $resolvedPath[1] -ne ':') {
        throw "Expected an absolute Windows path, got '$resolvedPath'."
    }

    $drive = $resolvedPath.Substring(0, 1).ToLowerInvariant()
    $rest = $resolvedPath.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

if ($Clean) {
    Remove-Item $publishDir, $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Publishing VoiceType.Uno $Version for $Runtime..."
dotnet publish $project `
    -c Release `
    -r $Runtime `
    -f net10.0-desktop `
    -p:GpuArch=CPU `
    --self-contained true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Linux publish failed with exit code $LASTEXITCODE."
}

$appBinary = Join-Path $publishDir "VoiceType.Uno"
if (-not (Test-Path $appBinary -PathType Leaf)) {
    throw "Published application binary was not found: $appBinary"
}

Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item `
    (Join-Path $stagingDir "DEBIAN"), `
    $installDir, `
    (Join-Path $stagingDir "usr/bin"), `
    (Join-Path $stagingDir "usr/share/applications"), `
    (Join-Path $stagingDir "usr/share/icons/hicolor/scalable/apps") `
    -ItemType Directory -Force | Out-Null

Copy-Item (Join-Path $publishDir "*") $installDir -Recurse -Force
Copy-Item (Join-Path $projectDir "launch-linux.sh") (Join-Path $installDir "launch-linux.sh") -Force
Copy-Item (Join-Path $workspace "tools/scripts/x11-window-fixer.py") (Join-Path $installDir "x11-window-fixer.py") -Force
Copy-Item (Join-Path $packagingDir "voicetype-uno") (Join-Path $stagingDir "usr/bin/voicetype-uno") -Force
Copy-Item (Join-Path $packagingDir "voicetype-uno.desktop") (Join-Path $stagingDir "usr/share/applications/voicetype-uno.desktop") -Force
Copy-Item (Join-Path $projectDir "Assets/Icons/icon.svg") (Join-Path $stagingDir "usr/share/icons/hicolor/scalable/apps/voicetype-uno.svg") -Force

$control = (Get-Content (Join-Path $packagingDir "control") -Raw).Replace("@VERSION@", $Version)
Set-Content (Join-Path $stagingDir "DEBIAN/control") $control

New-Item $outputDir -ItemType Directory -Force | Out-Null
Remove-Item $outputFile -Force -ErrorAction SilentlyContinue

$stagingWsl = Convert-ToWslPath $stagingDir
$outputWsl = Convert-ToWslPath $outputFile
$linuxStaging = "/tmp/voicetype-deb-$PID"
$linuxOutput = "/tmp/voicetype-uno_${Version}_amd64.deb"
$executables = @(
    "$linuxStaging/opt/voicetype-uno/VoiceType.Uno",
    "$linuxStaging/opt/voicetype-uno/launch-linux.sh",
    "$linuxStaging/opt/voicetype-uno/x11-window-fixer.py",
    "$linuxStaging/usr/bin/voicetype-uno"
)

wsl.exe -d $Distribution -- rm -rf $linuxStaging $linuxOutput
if ($LASTEXITCODE -ne 0) {
    throw "Failed to clean the Linux package staging directory."
}

wsl.exe -d $Distribution -- mkdir -p $linuxStaging
wsl.exe -d $Distribution -- cp -a "$stagingWsl/." "$linuxStaging/"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to copy package contents into the Linux staging directory."
}

wsl.exe -d $Distribution -- bash -lc "sed -i 's/\r$//' '$linuxStaging/DEBIAN/control' '$linuxStaging/opt/voicetype-uno/launch-linux.sh' '$linuxStaging/opt/voicetype-uno/x11-window-fixer.py' '$linuxStaging/usr/bin/voicetype-uno' '$linuxStaging/usr/share/applications/voicetype-uno.desktop'"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to normalize Linux package line endings."
}

wsl.exe -d $Distribution -- bash -lc "find '$linuxStaging' -type d -exec chmod 0755 {} +; find '$linuxStaging' -type f -exec chmod 0644 {} +"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to normalize Debian package permissions."
}

foreach ($executable in $executables) {
    wsl.exe -d $Distribution -- chmod 0755 $executable
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to mark package file executable: $executable"
    }
}

wsl.exe -d $Distribution -- dpkg-deb --build --root-owner-group $linuxStaging $linuxOutput
if ($LASTEXITCODE -ne 0) {
    throw "Debian package build failed with exit code $LASTEXITCODE."
}

wsl.exe -d $Distribution -- cp $linuxOutput $outputWsl
if ($LASTEXITCODE -ne 0) {
    throw "Failed to copy the Debian package to $outputFile."
}

wsl.exe -d $Distribution -- rm -rf $linuxStaging $linuxOutput

$package = Get-Item $outputFile
$hash = Get-FileHash $outputFile -Algorithm SHA256
Write-Host ""
Write-Host "Debian package created:" -ForegroundColor Green
Write-Host "  $($package.FullName)"
Write-Host "  Size: $([math]::Round($package.Length / 1MB, 1)) MB"
Write-Host "  SHA256: $($hash.Hash)"