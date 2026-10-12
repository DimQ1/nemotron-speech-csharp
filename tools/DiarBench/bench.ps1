# Runs the DiarBench matrix (models x threads x modes) on one wav and prints the summary lines.
#   pwsh -File tools/DiarBench/bench.ps1 -Wav <conv.wav> [-Threads "4,8"] [-Modes "low_latency,eco_2s"] [-Models "diar_step.onnx,diar_step.int8.onnx"]
# Lists are comma-separated strings (pwsh -File does not parse arrays; "4,0" would become the int 40).
# Run on an otherwise idle machine; never more threads than physical cores (oversubscription is catastrophic).
param(
    [Parameter(Mandatory)] [string] $Wav,
    [string] $Threads = '4,8',
    [string] $Modes = 'low_latency,very_low_latency,ultra_low_latency,ncpp_v3_streaming,eco_2s,offline',
    [string] $Models = 'diar_step.onnx,diar_step.int8.onnx'
)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'DiarBench.csproj'
dotnet build $proj -c Release -nologo -v q | Out-Null
$exe = Join-Path $PSScriptRoot 'bin/Release/net10.0/DiarBench.exe'
foreach ($m in $Models.Split(',')) {
    foreach ($t in $Threads.Split(',')) {
        foreach ($mode in $Modes.Split(',')) {
            Write-Host "=== $m | $mode | threads=$t"
            & $exe --wav $Wav --mode $mode --step $m --threads $t | Where-Object { $_ -match '^(model=|steps=|steady|peak|frontend)' }
        }
    }
}
