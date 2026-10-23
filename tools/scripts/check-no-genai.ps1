#Requires -Version 7.0
<#
.SYNOPSIS
  Fails when ONNX Runtime GenAI is back in the product (it was removed: every recognizer runs on plain ONNX Runtime).

.DESCRIPTION
  Needs PowerShell 7 (pwsh): Windows PowerShell 5.1 cannot load System.Reflection.Metadata, which check 3 uses. Works on Windows
  and Linux (paths are built with Join-Path; on Linux the WinUI head is reported as NOT BUILT).

  Three checks. The script builds nothing and restores nothing: it only reads what a previous build or restore left on disk, and
  fails with a clear message when that state is missing (run `dotnet build` / `dotnet restore` first).
   1. restore state: no obj/project.assets.json of any project of the repository mentions an OnnxRuntimeGenAI package;
   2. `dotnet list package --include-transitive --no-restore` of the CLI, WinUI and Uno heads (the same fact as seen by NuGet,
      read from the existing project.assets.json; a head without one is reported as a failure);
   3. build output: in bin/<Configuration> of the CLI, the WinUI app and the Uno desktop head no file is named
      *onnxruntime-genai* / *OnnxRuntimeGenAI* (managed, native, import library, aar, .so), and no product assembly
      (SpeechLib*, VoiceType*, NemotronSpeech) references a GenAI assembly (read from the file metadata, nothing is loaded).
  A head whose output folder does not exist is reported as NOT BUILT; the script exits 2 when no head output was found at all,
  1 when anything of GenAI is found, 0 otherwise. Build the heads first, from a clean output (delete bin/<Configuration>)
  when a previous build may have left files behind:
    dotnet build NemotronSpeech.slnx -c Release
    dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -c Release -f net10.0-desktop -p:RuntimeIdentifier=win-x64   (from apps/VoiceType.Uno/src)

  What it does not see: an Android APK/AAB (build one and look inside) and any output folder that is not one of the three above.
  The same rules for the SpeechLib assemblies are enforced by Unit_AssemblyLayeringTests, which only sees the output of the SpeechLib.Tests project.

.PARAMETER RepoRoot       Repository (or worktree) root; default: two folders above this script.
.PARAMETER Configuration  Build configuration whose bin/<Configuration> folders are scanned (default Release).
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path
function Join-PathParts([string]$root, [string[]]$parts) { foreach ($part in $parts) { $root = Join-Path $root $part }; $root }

$heads = [ordered]@{
    'CLI (NemotronSpeech)' = @('apps', 'NemotronSpeech', 'src', 'NemotronSpeech')
    'WinUI'                = @('apps', 'VoiceType.WinUI', 'src', 'VoiceType.WinUI')
    'Uno desktop'          = @('apps', 'VoiceType.Uno', 'src', 'VoiceType.Uno')
}
$headOutput = @{
    'CLI (NemotronSpeech)' = @('bin', $Configuration)
    'WinUI'                = @('bin', $Configuration)
    'Uno desktop'          = @('bin', $Configuration, 'net10.0-desktop')
}
$pattern = 'OnnxRuntimeGenAI|onnxruntime-genai'
$findings = [System.Collections.Generic.List[string]]::new()

# 1. restore state of every project (folders and separators are matched for both / and \)
$assets = Get-ChildItem $RepoRoot -Recurse -Filter project.assets.json -File -ErrorAction SilentlyContinue |
    Where-Object {
        $relative = $_.FullName.Substring($RepoRoot.Length) -replace '\\', '/'
        $relative -match '/obj/' -and $relative -notmatch '^/(\.claude/worktrees|work)/'
    }
foreach ($file in $assets) {
    if (Select-String -Path $file.FullName -Pattern $pattern -List -Quiet) { $findings.Add("package in $($file.FullName)") }
}
Write-Host "restore state: $($assets.Count) project.assets.json scanned"

# 2. dotnet list package, per head, from the existing restore state only
foreach ($head in $heads.Keys) {
    $folder = Join-PathParts $RepoRoot $heads[$head]
    if (-not (Test-Path $folder)) { $findings.Add("the folder of $head does not exist: $folder (is -RepoRoot the repository root?)"); continue }
    $project = Get-ChildItem $folder -Filter *.csproj | Select-Object -First 1
    if (-not (Test-Path (Join-PathParts $folder @('obj', 'project.assets.json')))) {
        $findings.Add("no restore state for ${head} ($folder): run 'dotnet restore' or 'dotnet build' of the project first (this script does not restore)")
        continue
    }
    $listing = & dotnet list $project.FullName package --include-transitive --no-restore 2>&1 | Out-String
    if ($listing -match $pattern) { $findings.Add("dotnet list package of $head mentions GenAI") }
    elseif ($listing -notmatch 'Microsoft\.ML\.OnnxRuntime\b') { $findings.Add("dotnet list package of $head did not list Microsoft.ML.OnnxRuntime (stale or partial restore state?)") }
    Write-Host "dotnet list package: $head ok"
}

# 3. build output of the heads
Add-Type -AssemblyName System.Reflection.Metadata
function Get-ReferencedAssemblyNames([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { return @() }
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        return @($metadata.AssemblyReferences | ForEach-Object { $metadata.GetString($metadata.GetAssemblyReference($_).Name) })
    }
    finally { $stream.Dispose() }
}

$checked = 0
foreach ($head in $heads.Keys) {
    $output = Join-PathParts (Join-PathParts $RepoRoot $heads[$head]) $headOutput[$head]
    if (-not (Test-Path $output)) { Write-Host "build output: $head NOT BUILT ($output)"; continue }
    $files = Get-ChildItem $output -Recurse -File
    $hits = @($files | Where-Object { $_.Name -match $pattern })
    foreach ($hit in $hits) { $findings.Add("$head output has $($hit.FullName)") }
    $product = @($files | Where-Object { $_.Name -match '^(SpeechLib|VoiceType|NemotronSpeech).*\.dll$' })
    foreach ($assembly in $product) {
        if ((Get-ReferencedAssemblyNames $assembly.FullName) -match $pattern) { $findings.Add("${head}: $($assembly.Name) references a GenAI assembly") }
    }
    $ort = @($files | Where-Object { $_.Name -in 'onnxruntime.dll', 'libonnxruntime.so', 'Microsoft.ML.OnnxRuntime.dll' })
    if ($ort.Count -eq 0) { $findings.Add("$head output has no plain ONNX Runtime binary (checked the wrong folder?)") }
    $checked++
    Write-Host "build output: $head $($files.Count) files, $($product.Count) product assemblies, GenAI files: $($hits.Count)"
}

if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Host "FOUND: $_" -ForegroundColor Red }
    exit 1
}
if ($checked -eq 0) { Write-Host 'No head output found: build the heads first.' -ForegroundColor Yellow; exit 2 }
Write-Host "OK: no ONNX Runtime GenAI package or binary ($checked head outputs checked)" -ForegroundColor Green
