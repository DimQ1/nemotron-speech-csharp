[CmdletBinding()]
param(
    [string]$Distribution = "Ubuntu-24.04",
    [string]$LinuxUser = "voicetype",
    [string]$LinuxAppDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LinuxAppDir)) {
    $LinuxAppDir = "/home/$LinuxUser/voicetype-uno"
}

$workspace = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$appSource = Join-Path $workspace "apps/VoiceType.Uno/src/VoiceType.Uno"
$project = Join-Path $appSource "VoiceType.Uno.csproj"
$publishDir = Join-Path $appSource "bin/Debug/net10.0-desktop/linux-x64/publish"
$launcher = Join-Path $appSource "launch-linux.sh"
$fixer = Join-Path $workspace "tools/scripts/x11-window-fixer.py"

function Convert-WindowsPathToWsl {
    param([Parameter(Mandatory)][string]$Path)

    if ($Path.Length -lt 3 -or $Path[1] -ne ':') {
        throw "Expected an absolute Windows path, got '$Path'."
    }

    $drive = $Path.Substring(0, 1).ToLowerInvariant()
    $rest = $Path.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

Write-Host "Publishing VoiceType.Uno for Linux..."
& dotnet publish $project -c Debug -r linux-x64 -f net10.0-desktop -p:GpuArch=CPU --self-contained true
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$requiredFiles = @(
    (Join-Path $publishDir "VoiceType.Uno"),
    $launcher,
    $fixer
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path $file -PathType Leaf)) {
        throw "Required Linux launch file was not produced: $file"
    }
}

$publishWsl = Convert-WindowsPathToWsl (Resolve-Path $publishDir).Path
$launcherWsl = Convert-WindowsPathToWsl (Resolve-Path $launcher).Path
$fixerWsl = Convert-WindowsPathToWsl (Resolve-Path $fixer).Path
$modelConfig = "/home/$LinuxUser/.local/share/VoiceType/models/cpu-int4/genai_config.json"

Write-Host "Copying the Linux bundle to ${Distribution}:$LinuxAppDir and starting it..."
function Invoke-WslCommand {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & wsl.exe -d $Distribution -u $LinuxUser -- @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "WSL command failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

Invoke-WslCommand @("mkdir", "-p", $LinuxAppDir)
Invoke-WslCommand @("cp", "-a", "$publishWsl/.", "$LinuxAppDir/")
Invoke-WslCommand @("cp", $launcherWsl, "$LinuxAppDir/launch-linux.sh")
Invoke-WslCommand @("cp", $fixerWsl, "$LinuxAppDir/x11-window-fixer.py")
Invoke-WslCommand @("chmod", "+x", "$LinuxAppDir/VoiceType.Uno", "$LinuxAppDir/launch-linux.sh", "$LinuxAppDir/x11-window-fixer.py")
Invoke-WslCommand @("test", "-f", $modelConfig)
Invoke-WslCommand @("test", "-S", "/mnt/wslg/PulseServer")

& wsl.exe -d $Distribution -u $LinuxUser -- env `
    "DISPLAY=:0" `
    "XDG_RUNTIME_DIR=/run/user/1000" `
    "PULSE_SERVER=/mnt/wslg/PulseServer" `
    "$LinuxAppDir/launch-linux.sh"
exit $LASTEXITCODE