# Uploads converted ONNX artifacts to HuggingFace.
# Prerequisites:
#   - `hf` CLI (part of huggingface_hub) and a logged-in write token: hf auth login
#   - The artifacts in the directory passed via -ArtifactDir
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepoId,          # e.g. DimQ1/parakeet-tdt-0.6b-v3-onnx

    [Parameter(Mandatory)]
    [string]$ArtifactDir,     # directory containing encoder.onnx, genai_config.json, ...

    [string]$CommitMessage = "Add parakeet-tdt-0.6b-v3 ONNX (FP32 + INT4)"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ArtifactDir)) {
    throw "Artifact directory not found: $ArtifactDir"
}

Write-Host "Uploading $ArtifactDir -> https://huggingface.co/$RepoId ..."

# Create the repo if it does not exist, then upload files.
hf repo create $RepoId --type model --yes 2>$null
hf upload $RepoId $ArtifactDir --commit-message $CommitMessage

if ($LASTEXITCODE -ne 0) {
    throw "hf upload failed with exit code $LASTEXITCODE."
}

Write-Host "Upload complete: https://huggingface.co/$RepoId" -ForegroundColor Green
