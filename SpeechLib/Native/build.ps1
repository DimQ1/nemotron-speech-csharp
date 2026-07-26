# Builds the native custom-op DLL (nemotron_swish_cpu.dll) that provides the
# missing Swish-24 CPU kernel for ONNX Runtime.
#
# Requires: CMake + Visual Studio 2022 (MSVC) — both already used by the repo.
# The script is invoked automatically by the NemotronSpeech.csproj pre-build
# target, or can be run manually:  pwsh NemotronSpeech/Native/build.ps1

[CmdletBinding()]
param(
    [string]$OrtVersion = "1.25.1"
)

$ErrorActionPreference = 'Stop'
$nativeDir = $PSScriptRoot
$buildDir = Join-Path $nativeDir 'build'
$dllPath = Join-Path $buildDir 'nemotron_swish_cpu.dll'
$srcPath = Join-Path $nativeDir 'swish_cpu.cpp'

# Skip the rebuild when the DLL is newer than all inputs (incremental build).
if ((Test-Path $dllPath) -and
    (Get-Item $dllPath).LastWriteTime -gt (Get-Item $srcPath).LastWriteTime -and
    (Get-Item $dllPath).LastWriteTime -gt (Get-Item (Join-Path $nativeDir 'CMakeLists.txt')).LastWriteTime) {
    Write-Host "nemotron_swish_cpu.dll is up to date."
    exit 0
}

$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $cmake) {
    Write-Warning "cmake not found - skipping native Swish custom-op build. " +
                  "Models with Swish-24 nodes will fail to load on CPU."
    exit 0
}

# Preferred path: CMake + VS 2022 generator.
cmake -S $nativeDir -B $buildDir -G "Visual Studio 17 2022" -A x64 `
      -DORT_VERSION=$OrtVersion 2>&1 | Out-Host
$cmakeOk = ($LASTEXITCODE -eq 0)

if ($cmakeOk) {
    cmake --build $buildDir --config Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)" }

    # CMake puts the Release DLL into build\Release for multi-config generators.
    $releaseDll = Join-Path $buildDir 'Release\nemotron_swish_cpu.dll'
    if (Test-Path $releaseDll) {
        Copy-Item $releaseDll $dllPath -Force
    }
}
else {
    # Fallback: this machine has VS 18 (2026) which CMake 3.30 does not know,
    # and the VS18 MSVC toolset lacks STL headers. Compile directly with
    # cl.exe, combining:
    #   - compiler:  VS18 VC\Tools\MSVC\<ver>\bin\Hostx64\x64
    #   - STL/CRT:   VS18 SDK\ScopeCppSDK\vc15\VC\{include,lib}
    #   - Win SDK:   Windows Kits\10\{Include,Lib}\<ver>
    Write-Host "CMake VS2022 generator unavailable, falling back to direct cl.exe build."

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vsPath = & $vswhere -latest -products * -property installationPath
    if (-not $vsPath) { throw "No Visual Studio installation found" }

    $msvcRoot = Join-Path $vsPath 'VC\Tools\MSVC'
    $msvcVer = (Get-ChildItem $msvcRoot | Sort-Object Name -Descending | Select-Object -First 1).Name
    $clBin = Join-Path $msvcRoot "$msvcVer\bin\Hostx64\x64"

    $scopeVc = Join-Path $vsPath 'SDK\ScopeCppSDK\vc15\VC'
    if (-not (Test-Path (Join-Path $scopeVc 'include\vector'))) {
        throw "STL headers not found under $scopeVc\include"
    }

    $sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
    $sdkVer = (Get-ChildItem (Join-Path $sdkRoot 'Include') |
               Where-Object { $_.Name -match '^\d' } |
               Sort-Object Name -Descending | Select-Object -First 1).Name
    $sdkInc = Join-Path $sdkRoot "Include\$sdkVer"
    $sdkLib = Join-Path $sdkRoot "Lib\$sdkVer"

    $ortRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.ml.onnxruntime\$OrtVersion"
    $ortInc = Join-Path $ortRoot 'build\native\include'
    $ortLib = Join-Path $ortRoot 'runtimes\win-x64\native'
    if (-not (Test-Path (Join-Path $ortInc 'onnxruntime_cxx_api.h'))) {
        throw "onnxruntime_cxx_api.h not found under $ortInc. Run 'dotnet restore' first."
    }

    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

    $env:PATH = "$clBin;$env:PATH"
    $env:INCLUDE = @(
        $ortInc,
        (Join-Path $scopeVc 'include'),
        (Join-Path $sdkInc 'ucrt'),
        (Join-Path $sdkInc 'um'),
        (Join-Path $sdkInc 'shared'),
        (Join-Path $sdkInc 'winrt')
    ) -join ';'
    # VS18 MSVC lib (onecore\x64) provides __CxxFrameHandler4/__GSHandlerCheck_EH4
    # which the older ScopeCppSDK libvcruntime.lib does not export.
    $env:LIB = @(
        $ortLib,
        (Join-Path $msvcRoot "$msvcVer\lib\onecore\x64"),
        (Join-Path $scopeVc 'lib'),
        (Join-Path $sdkLib 'ucrt\x64'),
        (Join-Path $sdkLib 'um\x64')
    ) -join ';'

    Push-Location $buildDir
    try {
        # libvcruntime.lib provides __CxxFrameHandler4 / __GSHandlerCheck_EH4
        # (it lives in the ScopeCppSDK VC\lib, not in the UCRT lib dir).
        # /fp:fast lets MSVC auto-vectorize the std::exp loop into packed AVX2
        # polynomial evaluation instead of per-element libm calls.
        & cl /LD /O2 /EHsc /std:c++17 /arch:AVX2 /fp:fast /MD `
            /Fe"$dllPath" /Fo"$buildDir\\" `
            "$srcPath" `
            /link onnxruntime.lib libvcruntime.lib `
            /IMPLIB:"$buildDir\nemotron_swish_cpu.lib" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "cl.exe build failed ($LASTEXITCODE)" }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Built $dllPath"
