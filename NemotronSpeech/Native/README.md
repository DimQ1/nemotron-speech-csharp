# Native custom ops: Swish-24 CPU kernel

Нативная DLL `nemotron_swish_cpu.dll` регистрирует CPU-kernel для ONNX-оператора
`Swish` (opset 24, домен `ai.onnx`) через механизм ONNX Runtime Custom Operators.

## Зачем

- ORT ≤1.23 не знает opset 24 вовсе; ORT 1.25.0 в ранних RC не имел CPU-kernel
  для Swish-24. Модели, экспортированные с `target_opset: 24`, падали бы с
  "Missing Kernel" на CPU Execution Provider.
- **Проверено 2026-07-21:** `Microsoft.ML.OnnxRuntime` **1.25.1 уже содержит**
  встроенный CPU-kernel Swish-24 — для CPU-сборки проекта эта DLL не нужна.
- DLL остаётся запасным путём для конфигураций со старым транзитивным ORT
  (например `Microsoft.ML.OnnxRuntimeGenAI.Cuda` 0.14.1 тащит ORT ~1.23).

## Файлы

| Файл | Назначение |
|---|---|
| `swish_cpu.cpp` | C++ kernel: `Y = X / (1 + exp(-alpha * X))`, float32, атрибут `alpha` (default 1.0) |
| `CMakeLists.txt` | CMake-сборка (VS 2022 generator) |
| `build.ps1` | Сборка DLL; fallback на прямой вызов `cl.exe` для VS 18 (2026) |
| `CustomOpLibrary.cs` | Managed-сторона: `SessionOptions.RegisterCustomOpLibrary` один раз на процесс, по флагу `use_swish_custom_op` в `genai_config.json` |

## Сборка

Запускается автоматически как pre-build target `NemotronSpeech.csproj`
(только для `GpuArch=CPU`), либо вручную:

```powershell
pwsh NemotronSpeech/Native/build.ps1
```

Требования: CMake + VS 2022 **или** VS 18 (fallback-режим с прямым `cl.exe`;
STL/CRT берутся из `SDK\ScopeCppSDK`, EH-символы — из `VC\Tools\MSVC\*\lib\onecore`).
Заголовки `onnxruntime_cxx_api.h` и `onnxruntime.lib` — из NuGet-кэша
`Microsoft.ML.OnnxRuntime` 1.25.1 (нужен предварительный `dotnet restore`).

Результат: `Native/build/nemotron_swish_cpu.dll` (в git не попадает — `build/`
в `.gitignore`). При `dotnet build` DLL копируется рядом с `NemotronSpeech.exe`.

## Включение для модели

В `genai_config.json` модели:

```json
{
  "model": {
    "session": {
      "session_options": { "use_swish_custom_op": true }
    }
  }
}
```

`CustomOpLibrary.RegisterIfNeeded(modelPath)` вызывается из конструктора
`ModelSession` перед созданием ORT GenAI `Model`. Регистрация процесс-wide:
DLL загружается через временный `InferenceSession`, домен остаётся в ORT
environment и виден последующим GenAI-сессиям.

## Проверка

См. `Doc/opset24-analysis.md` (раздел поправки): тестовая модель `Swish`
opset 24 загружается и вычисляет `x*sigmoid(x)` с точностью 1e-5 как с
встроенным kernel (ORT 1.25.1), так и с этой DLL.
