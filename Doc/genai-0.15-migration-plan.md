# План миграции: ORT GenAI 0.14.1 → 0.15 (streaming `nemotron_speech`)

> Ветка: `feature/genai-0.15-streaming-migration`
> Дата: 2026-07-29
> Статус: **план, код не трогаем**

## 0. Проверка доступности пакетов (факт, 2026-07-29)

| Пакет | Источник | Последняя версия | Вывод |
|---|---|---|---|
| `Microsoft.ML.OnnxRuntimeGenAI` | nuget.org (stable) | **0.14.1** | 0.15 stable ещё НЕ выпущен |
| `Microsoft.ML.OnnxRuntimeGenAI.Cuda` | nuget.org (stable) | **0.14.1** | то же |
| `Microsoft.ML.OnnxRuntimeGenAI.DirectML` | nuget.org (stable) | **0.14.1** | то же |
| `Microsoft.ML.OnnxRuntimeGenAI` | ORT-Nightly | **0.15.0-dev202607231321155** | ✅ доступен (CPU) |
| `Microsoft.ML.OnnxRuntimeGenAI.Cuda` | ORT-Nightly | **0.15.0-dev202607231321155** | ✅ доступен |
| `Microsoft.ML.OnnxRuntimeGenAI.DirectML` | ORT-Nightly | — (нет 0.15) | ❌ DML остаётся на 0.14.1 |
| `Microsoft.ML.OnnxRuntimeGenAI.WinML` | ORT-Nightly | 0.14.1 | DML-путь через WinML, без 0.15 |
| `Microsoft.ML.OnnxRuntime` | nuget.org (stable) | **1.28.0** | 1.26–1.28 вышли |
| `Microsoft.ML.OnnxRuntime` | ORT-Nightly | 1.29.0-dev-20260708 | nightly |

**Ключевые выводы:**

1. Миграция возможна только на **nightly-фиде** (он уже подключён в `NemotronSpeech/nuget.config` для Blackwell). Stable 0.15 ждать не нужно — API в nightly уже содержит всё нужное (`StreamingProcessor`, VAD, `nemotron_speech` модель; наша кодовая база уже использует этот API, т.к. проект изначально писался по нему — см. `ModelSession`).
2. **DML-ветка блокируется**: в nightly нет GenAI.DirectML 0.15. Варианты: оставить DML на 0.14.1 (отдельная ветка `<Choose>`), либо перейти DML-пользователей на WinML-пакет, либо вывести DML из поддержки до stable 0.15.
3. `Microsoft.ML.OnnxRuntime.EP.Cuda` (plugin EP из ORT 1.26+) на nuget.org **отсутствует** (404) и в ORT-Nightly не опубликован — унификация GPU-веток через plugin EP пока откладывается.

### 0.1. Plugin EP — детальная проверка доступности (2026-07-29)

| Пакет | nuget.org | PyPI | ORT-Nightly | Статус |
|---|---|---|---|---|
| `Microsoft.ML.OnnxRuntime.EP.WebGpu` | ✅ **0.2.1** | ✅ 0.2.1 (`onnxruntime-ep-webgpu`) | ✅ 0.2.1 | Единственный опубликованный plugin EP |
| `Microsoft.ML.OnnxRuntime.EP.Cuda` | ❌ | ❌ (`onnxruntime-ep-cuda12/13`) | ❌ | Код готов в main, пакет не опубликован |
| `Intel.ML.OnnxRuntime.EP.OpenVINO` | ✅ 1.6.1 | — | — | Intel-публикация, отдельный вендор |

**CUDA plugin EP (не опубликован, но код готов в main):**
- C# обёртка `Microsoft.ML.OnnxRuntime.EP.Cuda` (`plugin-ep-cuda/csharp/`, класс `CudaEp` с `GetLibraryPath()`/`GetEpName()`), `pack_nuget.py`, тесты `CudaPluginEpTests` (регистрация, inference, IoBinding, auto EP selection).
- Минимальная версия рантайма (`plugin-ep-cuda/MIN_ONNXRUNTIME_VERSION`): **ORT ≥ 1.24.4**.
- Python-пакет `onnxruntime` в main умеет авто-регистрировать bundled CUDA plugin (`cuda-plugin-ep=1`) — Microsoft готовит переход CUDA EP «из коробки в плагин».
- Вывод: мониторить публикацию; вероятно, выйдет с ORT 1.29.

**WebGPU plugin EP (доступен):**
- Нативные файлы (win-x64/win-arm64): `onnxruntime_providers_webgpu.dll` + `dxil.dll` + `dxcompiler.dll` (DirectX Shader Compiler для WGSL→DXIL). Dawn слинкован монолитно.
- Минимальная версия рантайма: **ORT ≥ 1.24.4** (совместимость проверяется нативным кодом при регистрации, жёсткой зависимости в nuspec нет).
- Регистрация из C#:
  ```csharp
  OrtEnv.Instance().RegisterExecutionProviderLibrary("webgpu", WebGpuEp.GetLibraryPath());
  var device = OrtEnv.Instance().GetEpDevices().First(d => d.EpName == WebGpuEp.GetEpName());
  sessionOptions.AppendExecutionProvider(env, new[] { device }, new Dictionary<string,string>());
  ```

### 0.2. WebGPU EP в WinUI 3 — работает ли? (исследование 2026-07-29)

**Да, работает.** WebGPU EP — headless compute через Dawn, не требует окна, surface, HWND или WinRT/XAML-интеграции:

1. **Нет зависимости от UI.** `WebGpuContext.Initialize` создаёт `wgpu::Instance/Adapter/Device` напрямую через Dawn. Окно нужно только опциональному `WebGpuPIXFrameGenerator` (PIX-профилирование, в релизе выключено). В сборке Dawn отключены `DAWN_SUPPORTS_GLFW_FOR_WINDOWING=OFF`, `DAWN_USE_WINDOWS_UI=OFF`.
2. **Бэкенд на Windows — D3D12** (`WGPUBackendType_D3D12` по умолчанию) — тот же GPU-путь, что у DirectML, но через Dawn/WGSL. Работает на любом GPU с драйвером D3D12 (NVIDIA/AMD/Intel).
3. **MSIX-совместимость.** DLL грузятся обычным `LoadLibrary` из папки приложения — в packaged MSIX разрешено. Нужно только убедиться, что 3 DLL попадают в AppX (у нас кастомный контент-пайплайн с ручными `Delete` в `VoiceType.WinUI.csproj` — добавить проверку).
4. **Ограничения:**
   - `ConcurrentRunSupported() = false` (глобальное состояние Dawn) — для single-session диктовки не проблема.
   - JIT-компиляция WGSL→DXIL на старте сессии — первый запуск медленнее, кэша pipeline между запусками нет.
   - Покрытие операторов: Conv1D/MatMul/Attention/LayerNorm есть; **MatMulNBits (INT4)** заявлен; opset-24 (`Swish`, `TensorScatter`) — под вопросом, fallback на CPU-EP спасёт.
   - Производительность ожидаемо ~DML-уровня или ниже — нужен бенчмарк RTF на нашей модели.

**Вывод:** WebGPU plugin EP — жизнеспособная замена заблокированной DML-ветки (см. Шаг 5, вариант D).

### Что реально нового даёт 0.15 относительно текущего кода

Наш `ModelSession` уже использует `StreamingProcessor` + `SetOption("use_vad")` + `SetRuntimeOption("lang_id")` — этот API пришёл из nightly 0.15 и бэкпортирован в проект. Значит, миграция — это в основном **версии + нативные бинарники + отказ от костылей**, а не переписывание кода:

- ORT 1.26–1.28 в транзитиве: нативный **Swish-24 CPU-кернел** в основном пакете (проверить!), что позволит удалить кастомный `nemotron_swish_cpu.dll`.
- `SessionOptionsSetEpSelectionPolicy`, `GetHardwareDeviceEpIncompatibilityDetails` — диагностика EP-фолбэков.
- EP graph capture (`SessionReleaseCapturedGraph`, API v27) — потенциальный перф-выигрыш на фиксированных чанках.
- GenAI 0.15: `NemotronSpeechState` с `ResetStreamingState`, улучшенный Silero-VAD (consecutive silence), метрики пропущенных чанков.

---

## 1. Цели миграции

1. Перевести CPU и Standard(CUDA) ветки на `0.15.0-dev202607231321155` (или новее nightly на момент старта).
2. Обновить транзитивный ORT: CPU → `Microsoft.ML.OnnxRuntime 1.28.0` (stable) вместо override 1.25.1; CUDA — что тянет GenAI.Cuda 0.15 (проверить, вероятно ORT 1.27/1.28).
3. **Удалить кастомный Swish-оп** (`SpeechLib/Native/swish_cpu.cpp`, `CustomOpLibrary.cs`, `BuildSwishCustomOp` target), если ORT 1.28 содержит Swish-24 CPU-кернел нативно.
4. **Удалить target `CopyOrt1251NativeAssets`** и прямой `PackageReference` на ORT 1.25.1 — оставить транзитивную версию от GenAI.
5. Упростить `<Choose>` по `GpuArch`: убрать ветку `Blackwell` (nightly 0.15 для CUDA 13 совместим с RTX 50; проверить наличие sm_120 в бинарниках).
6. DML: принять решение (см. §5).
7. Прогнать регрессию: unit + E2E WordTimings + бенчмарк CPU (BenchmarkSuite1).

## 2. Нецели (out of scope)

- Миграция на CUDA Plugin EP (`Microsoft.ML.OnnxRuntime.EP.Cuda`) — пакет не опубликован. Отдельная задача после его релиза.
- Переход на EP graph capture — отдельный эксперимент после основной миграции.
- Замена собственного аудио-пайплайна (NAudio, ConcurrentQueueWrapper) — не трогаем.

---

## 3. Пошаговый план

### Шаг 0. Подготовка (без изменений кода)

- [ ] Убедиться, что в `NemotronSpeech/nuget.config` есть ORT-Nightly feed (есть).
- [ ] Проверить, что `VoiceType.WinUI` и `VoiceType` (WPF) не пинуют ORT/GenAI отдельно от SpeechLib — проверить `VoiceType.WinUI.csproj` (там есть `Microsoft.ML.OnnxRuntime 1.25.1` с `ExcludeAssets="native"` и хардкод пути `microsoft.ml.onnxruntime\1.25.1` в `_Ort1251Cache`, строки ~129, ~180 — **потребует синхронного обновления**).

### Шаг 1. Проверка Swish-24 в ORT 1.28 stable (блокер для удаления кастом-опа)

- [ ] Скачать `Microsoft.ML.OnnxRuntime 1.28.0`, распаковать, проверить `onnxruntime.dll` на наличие Swish-24 CPU-кернела:
  - либо прогнать `converter/test_attn24.onnx` / минимальную opset-24 модель на чистом ORT 1.28 без кастом-опа;
  - либо поискать `Swish` в `docs/OperatorKernels.md` репозитория onnxruntime для тега v1.28.0.
- [ ] **Decision gate:**
  - Swish есть → удаляем кастом-оп (Шаг 4).
  - Swish нет → оставляем `CustomOpLibrary` и `nemotron_swish_cpu.dll` как есть, в плане пометить «повторить проверку на ORT 1.29 stable».

### Шаг 2. Обновление `SpeechLib/SpeechLib.csproj`

- [ ] Ветка `Blackwell`: заменить `0.15.0-dev-*` на конкретный пин `0.15.0-dev202607231321155` (воспроизводимость).
- [ ] Ветка `CPU`: `Microsoft.ML.OnnxRuntimeGenAI` → `0.15.0-dev202607231321155`; **удалить** прямой `PackageReference Microsoft.ML.OnnxRuntime 1.25.1` и target `CopyOrt1251NativeAssets` — но только после проверки, какую версию ORT тянет GenAI 0.15 (должна быть ≥ 1.27; проверить через `dotnet list package --include-transitive`).
- [ ] Ветка `Standard`: `Microsoft.ML.OnnxRuntimeGenAI.Cuda` → `0.15.0-dev202607231321155`.
- [ ] Ветка `DML`: оставить `0.14.1` (см. §5) — добавить комментарий о блокировке.
- [ ] Рассмотреть слияние веток `Standard` и `Blackwell` в одну (`0.15.0-dev...` для всех CUDA): проверить, что nightly 0.15 содержит kernели и для sm_75..sm_90 (CUDA 12) и для sm_120 (CUDA 13). Если в пакете два набора DLL — упрощаем `<Choose>` до трёх веток (CPU / CUDA / DML).

### Шаг 3. Синхронное обновление зависимых проектов

- [ ] `VoiceType.WinUI/VoiceType.WinUI.csproj`:
  - обновить `Microsoft.ML.OnnxRuntime` 1.25.1 → версию транзитива GenAI 0.15 (сохранить `ExcludeAssets="native"`, если это требование MSIX-паковщика);
  - обновить `_Ort1251Cache` (строка ~180) на новый путь версии — лучше заменить хардкод `$(USERPROFILE)\.nuget\packages\...\1.25.1\` на `$(PkgMicrosoft_ML_OnnxRuntime)` через `GeneratePathProperty="true"`;
  - пересмотреть удаления `onnxruntime_providers_cuda.dll` / `onnxruntime-genai-cuda.dll` в AppX (строки ~195-197) — актуальны ли для новых DLL.
- [ ] `VoiceType/VoiceType.csproj` (WPF): проверить отсутствие собственных пинов ORT.
- [ ] `BenchmarkSuite1/BenchmarkSuite1.csproj`, `DeepFilterNet*`, `DiarizationConverter` — проверить, не пинуют ли они ORT (конфликт версий в одной солюции).
- [ ] `dotnet restore NemotronSpeech.slnx` + `dotnet list package --include-transitive | findstr /I "onnxruntime"` — зафиксировать итоговое дерево версий в плане-коммите.

### Шаг 4. Удаление Swish кастом-опа (если Шаг 1 = «есть в ORT»)

- [ ] Удалить из `SpeechLib.csproj`: target `BuildSwishCustomOp`, `<None Include="Native\build\nemotron_swish_cpu.dll">`.
- [ ] Удалить файлы: `SpeechLib/Native/swish_cpu.cpp`, `CMakeLists.txt`, `build.ps1`, `README.md` (папку `Native/` целиком, если больше ничего не останется), `SpeechLib/Native/CustomOpLibrary.cs`.
- [ ] Удалить вызов `CustomOpLibrary.RegisterIfNeeded(modelPath)` в `ModelSession.cs` (строка ~67).
- [ ] Удалить опцию `use_swish_custom_op` из `genai_config.json` всех моделей в `modules/` и из документации конвертера.
- [ ] Обновить `Doc/opset24-analysis.md`, `Doc/opset24-conversion.md` (пометить, что с ORT ≥ 1.28 кастом-оп не нужен).
- [ ] Обновить `AGENTS.md` (раздел Critical Pitfalls) и `README.md`.

### Шаг 5. Решение по DML

- [ ] Вариант A (рекомендуется): DML остаётся на GenAI.DirectML 0.14.1; в `<Choose>` оставить ветку с комментарием «нет 0.15 в nightly, ждём stable». Риск: рассинхрон нативных DLL при сборке DML из той же solюции — проверить, что DML-сборка не подхватывает ORT 1.28 managed + 1.23 native.
- [ ] Вариант B: DML-пользователей перевести на `Microsoft.ML.OnnxRuntimeGenAI.WinML` (тоже 0.14.1 — смысла нет).
- [ ] Вариант C: временно убрать `GpuArch=DML` из документации до stable 0.15.
- [ ] Вариант D (эксперимент, новое): заменить DML на **WebGPU plugin EP** — `GpuArch=WebGPU` (см. §0.2). Работает в WinUI 3/MSIX, любой GPU через D3D12/Dawn, ORT ≥ 1.24.4 уже удовлетворён.

### Шаг 5a. Эксперимент: `GpuArch=WebGPU` (замена DML, опционально)

- [ ] Добавить ветку `WebGPU` в `<Choose>` `SpeechLib.csproj`: `Microsoft.ML.OnnxRuntime 1.28.0` + `Microsoft.ML.OnnxRuntime.EP.WebGpu 0.2.1` + GenAI CPU 0.15 nightly (управляемая обёртка) — GenAI создаёт сессии через ORT, plugin EP регистрируется на уровне ORT env.
- [ ] В `Common.GetConfig` (или новом `EpPluginRegistrar`) добавить регистрацию: `OrtEnv.Instance().RegisterExecutionProviderLibrary("webgpu", WebGpuEp.GetLibraryPath())` + выбор `OrtEpDevice` по `EpName`, с fallback на CPU при отсутствии устройства.
- [ ] Проверить, как GenAI `Config` принимает EP: если genai_config.json поддерживает только встроенные имена провайдеров, попробовать `AppendExecutionProvider` через ORT API до создания GenAI-сессии либо provider option `webgpu`.
- [ ] Проверка покрытия графа: `Session_GetEpGraphAssignmentInfo` — какие узлы encoder/decoder/joiner ушли на WebGPU, что осталось на CPU.
- [ ] Прогнать: FP32-модель (opset 21 и opset 24) и INT4-модель; замерить RTF на `BenchmarkSuite1` + WordTimings E2E.
- [ ] WinUI: проверить попадание `onnxruntime_providers_webgpu.dll`, `dxil.dll`, `dxcompiler.dll` в AppX; прогнать `build-store-release.ps1`.
- [ ] Decision gate: RTF ≥ DML и WER без регрессии → WebGPU заменяет DML-ветку; иначе остаёмся на варианте A.

### Шаг 6. Проверка новых возможностей GenAI 0.15 (опционально, отдельными коммитами)

- [ ] `ResetStreamingState` — проверить, нужен ли нам сброс состояния между сессиями диктовки без пересоздания `ModelSession` (сейчас VoiceType пересоздаёт сессию?).
- [ ] VAD-метрики (chunks processed/skipped) — вывести в CLI-статистику (`Program.cs`) и/или в VoiceType статус.
- [ ] `SessionOptionsSetEpSelectionPolicy` — заменить ручной фолбэк EP в `Common.GetConfig` на политику `MaxPerformance`/`MinimalPower`? (эксперимент).
- [ ] `GetHardwareDeviceEpIncompatibilityDetails` — улучшить диагностику «CUDA недоступна» в VoiceType.

### Шаг 7. Валидация

- [ ] Сборка всех 4-х конфигураций: `dotnet build NemotronSpeech.slnx -c Release` (Standard), `-p:GpuArch=CPU`, `-p:GpuArch=Blackwell`, `-p:GpuArch=DML`.
- [ ] `dotnet test VoiceType.Tests --filter "FullyQualifiedName~Unit_"` — без сети.
- [ ] `dotnet test VoiceType.Tests -c Release --filter "FullyQualifiedName~WordTimings"` — E2E с baseline `sample-0-wordtimings-baseline.txt` (регрессия точности/таймингов).
- [ ] Бенчмарк: `BenchmarkSuite1` (ModelSessionCpuBenchmark) до/после — зафиксировать дельту RTF в этом документе.
- [ ] Ручной прогон CLI: `NemotronSpeech` на `Test-Audio/` (CPU и CUDA).
- [ ] Ручной прогон VoiceType (WPF) и VoiceType.WinUI: диктовка 1–2 мин, проверить VAD, переключение языка, горячие клавиши.

### Шаг 8. Документация и коммиты

- [ ] Коммиты по шагам: `chore(deps): bump ORT GenAI to 0.15.0-dev (CPU/CUDA)` → `refactor: remove Swish-24 custom op (ORT 1.28 native)` → `chore(winui): sync ORT native assets with GenAI 0.15` → `docs: update opset24/AGENTS/README`.
- [ ] Обновить `AGENTS.md`: версии пакетов, удалить pitfall про `CopyOrt1251NativeAssets`, при необходимости добавить про nightly-пин.
- [ ] Обновить `README.md` (build commands: возможно, минус ветка Blackwell).
- [ ] В этом файле заполнить раздел «Результаты» (итоговые версии, дельта бенчмарка, решение по DML).

---

## 4. Риски

| Риск | Вероятность | Митигация |
|---|---|---|
| Nightly 0.15 нестабилен (регрессия качества ASR) | Средняя | WordTimings E2E baseline + бенчмарк до/после; откат на 0.14.1 одним revert |
| ORT 1.28 всё ещё без Swish-24 CPU | Средняя | Шаг 1 — decision gate; оставляем кастом-оп |
| GenAI 0.15 тянет ORT, конфликтующий с пином в VoiceType.WinUI | Высокая | Шаг 3 — синхронное обновление, проверка transitive tree |
| DML-ветка ломается от смешения 0.14.1/0.15 DLL | Средняя | Изоляция: DML собирается отдельной конфигурацией; проверка `dotnet list package` для DML |
| Nightly-пакет исчезнет из фида (retention) | Низкая | Пин конкретной версии + сохранить `.nupkg` в `Artifacts/` |
| MSIX-паковка WinUI ломается от новых DLL | Средняя | Прогон `build-store-release.ps1` перед мерджем |

## 5. Открытые вопросы

1. DML: оставляем 0.14.1, WebGPU (вариант D) или выпиливаем до stable? (Шаг 5/5a)
2. Blackwell и Standard — одна CUDA-ветка на nightly 0.15? (Шаг 2)
3. Нужен ли `ResetStreamingState` в VoiceType (переиспользование сессии между диктовками)?
4. После выхода **stable** GenAI 0.15 — повторная миграция nightly → stable (отдельная маленькая задача).
5. После публикации `Microsoft.ML.OnnxRuntime.EP.Cuda` — унификация Standard/Blackwell через plugin EP (ожидается ~ORT 1.29).

## 6. Результаты исследования (2026-07-29)

- GenAI 0.15: доступен только nightly `0.15.0-dev202607231321155` (CPU + CUDA); stable — 0.14.1; DirectML 0.15 отсутствует.
- ORT stable: 1.28.0; nightly: 1.29.0-dev.
- Plugin EP: опубликован только `Microsoft.ML.OnnxRuntime.EP.WebGpu 0.2.1` (min ORT 1.24.4); `EP.Cuda` — код готов в main, пакет не опубликован нигде.
- WebGPU EP в WinUI 3: **работает** (headless Dawn/D3D12, MSIX-совместим, без UI-зависимостей) — см. §0.2.
- Итоговые версии пакетов после миграции: _TBD_
- Swish-24 в ORT 1.28: _TBD (Шаг 1)_
- Бенчмарк CPU до/после (RTF): _TBD_
- Решение DML: _TBD (Шаг 5/5a)_
