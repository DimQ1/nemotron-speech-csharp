cmake_minimum_required(VERSION 3.20)
project(NemotronSwishCustomOp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ONNX Runtime NuGet package that provides headers (build/native/include) and
# the import library (runtimes/win-x64/native/onnxruntime.lib).
# Version must match Microsoft.ML.OnnxRuntime in NemotronSpeech.csproj.
set(ORT_VERSION "1.25.1" CACHE STRING "Microsoft.ML.OnnxRuntime package version")
set(ORT_PACKAGE_ROOT "$ENV{USERPROFILE}/.nuget/packages/microsoft.ml.onnxruntime/${ORT_VERSION}"
    CACHE PATH "Root of the Microsoft.ML.OnnxRuntime NuGet package")

if(NOT EXISTS "${ORT_PACKAGE_ROOT}/build/native/include/onnxruntime_cxx_api.h")
    message(FATAL_ERROR
        "onnxruntime_cxx_api.h not found under ${ORT_PACKAGE_ROOT}. "
        "Run 'dotnet restore' first so the Microsoft.ML.OnnxRuntime ${ORT_VERSION} "
        "package is present in the NuGet cache.")
endif()

add_library(nemotron_swish_cpu SHARED swish_cpu.cpp)

target_include_directories(nemotron_swish_cpu PRIVATE
    "${ORT_PACKAGE_ROOT}/build/native/include")
target_link_directories(nemotron_swish_cpu PRIVATE
    "${ORT_PACKAGE_ROOT}/runtimes/win-x64/native")
target_link_libraries(nemotron_swish_cpu PRIVATE onnxruntime)

if(MSVC)
    # /fp:fast lets MSVC auto-vectorize the std::exp loop into packed AVX2
    # polynomial evaluation instead of per-element libm calls.
    target_compile_options(nemotron_swish_cpu PRIVATE /O2 /EHsc /arch:AVX2 /fp:fast)
endif()

# The DLL must sit next to the managed binaries that load it via
# SessionOptions.RegisterCustomOpsLibrary.
set_target_properties(nemotron_swish_cpu PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
