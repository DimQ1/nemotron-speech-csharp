# WebGPU execution provider (plugin EP) — evaluation

Дата: 2026-09-28. Машина: Intel iGPU + NVIDIA GeForce RTX 5070 Ti Laptop GPU.

## Что сделано

WebGPU подключён как **plugin EP**: отдельная нативная библиотека
(`onnxruntime_providers_webgpu.dll`), которая регистрируется в рантайме на
process-wide `OrtEnv`. Ядро ONNX Runtime остаётся обычным CPU-пакетом — ни
пересборки, ни специальной сборки ORT не требуется.

| Файл | Изменение |
|---|---|
| `libraries/SpeechLib/src/SpeechLib.Providers/WebGpuExecutionProvider.cs` | Новый: регистрация плагина, перечисление адаптеров, сборка provider options, применение к `SessionOptions` и к GenAI `Config` |
| `libraries/SpeechLib/src/SpeechLib.Providers/ExecutionProviderSelector.cs` | `ProviderKind.WebGpu`; разбор `webgpu[...]`; деградация к CPU при недоступности |
| `libraries/SpeechLib/src/SpeechLib/Common.cs` | GenAI-путь: регистрация плагина + `AppendProvider("WebGPU")` с provider options |
| `libraries/SpeechLib/src/SpeechLib.Providers/SpeechLib.Providers.csproj` | `Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0 + копирование плагина, `dxcompiler.dll`, `dxil.dll` в `runtimes/win-x64/native` |
| `tools/WerEval/Program.cs` | `--ep <spec>` и `--list-gpus` |
| `build/bench-webgpu.ps1` | Матрица замеров CPU vs WebGPU |

## Использование

```
WerEval --list-gpus
WerEval parakeet <abs-model-dir> <abs-audio-dir> --ep cpu
WerEval parakeet <abs-model-dir> <abs-audio-dir> --ep "webgpu"        # по умолчанию
WerEval parakeet <abs-model-dir> <abs-audio-dir> --ep "webgpu:0"      # индекс адаптера из --list-gpus
WerEval parakeet <abs-model-dir> <abs-audio-dir> --ep "webgpu:hp"     # powerPreference=high-performance
WerEval parakeet <abs-model-dir> <abs-audio-dir> --ep "webgpu:lp"     # powerPreference=low-power
WerEval parakeet <abs-model-dir> <abs-audio-dir> --ep "webgpu:0,layout=NHWC,capture=1"
```

Приоритет provider: `cpu` → WebGPU (`webgpu*`) → `cuda` → `dml` → CPU (fallback).
Запрос WebGPU при отсутствующем плагине/адаптере **не роняет** сессию, а
деградирует к CPU с предупреждением — как и для устаревших `cuda`/`dml`.

Ключи в спецификации: `power`, `layout`, `capture`, `int64`, `cache`,
`forcecpu`, `maxssb`.

## Выбор видеокарты: что реально работает, а что нет

Плагин публикует **по одному `OrtEpDevice` на каждый GPU** (на этой машине —
`[0] NVIDIA 0x10DE:0x2F18`, `[1] Intel 0x8086:0x7D67`), и сессия добавляет
выбранный `OrtEpDevice`.

Однако в ORT 1.30.0 `Factory::CreateEpImpl` **не передаёт выбранный
hardware device** в `WebGpuProviderFactoryCreator::Create`; провайдер создаёт
контекст (`WebGpuContext::Initialize`) и просит адаптер у Dawn через
`requestAdapter` с полями `backendType` и `powerPreference`. То есть:

- выбор индекса `OrtEpDevice` — это выбор **записи EP-устройства**, а не
  гарантированное связывание с конкретным физическим адаптером;
- единственный документированный рычаг выбора адаптера —
  **`powerPreference`** (`high-performance` → дискретная, `low-power` →
  встроенная);
- детерминированный выбор карты возможен только через
  `webgpuInstance`/`webgpuDevice` (bring-your-own Dawn device, указатели в
  десятичном виде) — не реализовано.

Для UI это значит: список карт можно показывать, но выбор пункта списка
передавать в `powerPreference`, а не в индекс, иначе он не даёт эффекта.

## Результаты

Методика: `build/bench-webgpu.ps1`, `WerEval` на Common Voice 17 (`Test-Audio/cv17`),
25 файлов на прогон (Qwen3 — 5), абсолютные пути к моделям. Сырые строки:
`build/webgpu-bench-results.txt`.

| Модель | EP | WER | RTF | Скорость | Вывод |
|---|---|---|---|---|---|
| parakeet-tdt int4 (en) | cpu | 14.83% | 0.037 / 0.041 | 24–27x | базовый |
| parakeet-tdt int4 (en) | webgpu:0 (dGPU) | 14.83% | 0.028 | 36.1x | **+1.3–1.5x** |
| parakeet-tdt int4 (en) | webgpu:hp | 14.83% | 0.027 | 36.9x | **+1.4–1.5x** |
| parakeet-tdt int4 (en) | webgpu:lp (iGPU) | 14.83% | 0.461 | 2.2x | −12x к dGPU |
| nemotron int4-c056 (en) | cpu | 29.67% | 0.236 / 0.350 | 2.9–4.2x | базовый |
| nemotron int4-c056 (en) | webgpu:0 | 29.67% | 0.597 | 1.7x | **хуже в 1.7–2.5x** |
| nemotron int4-c056 (ru) | cpu | 10.53% | 0.229 | 4.4x | базовый |
| nemotron int4-c056 (ru) | webgpu:0 | 10.53% | 0.234 | 4.3x | паритет |
| qwen3 block-streaming (en) | cpu | 2.70% | 0.919 | 1.1x | базовый |
| qwen3 block-streaming (en) | webgpu:0 | — | — | — | **crash 0xC0000409** |
| qwen3 (en) | webgpu:0 | — | — | — | **crash 0xC0000409** |
| vibevoice 1.5b int4 | — | — | — | — | не измерялось (см. ниже) |

### Как читать

- **WER не изменился нигде**, где прогон дошёл до конца, включая GPU — качество
  распознавания WebGPU-путь не портит.
- **Parakeet TDT** — единственный выигрыш: 0.037/0.041 → 0.027/0.028, стабильно
  между прогонами. Это цельная оффлайн-транскрипция крупными тензорами, что для
  GPU идеально.
- **Nemotron (GenAI streaming)** — на `ru` паритет, на `en` деградация в 1.7–2.5x.
  Причина: FastConformer обрабатывает чанк 560 мс за шаг, тензоры мелкие, и
  накладные расходы GPU + host↔device копий превышают выигрыш.
- **Qwen3 (оба варианта)** — жёсткий fail-fast `0xC0000409`
  (STATUS_STACK_BUFFER_OVERRUN) без managed-исключения: процесс падает после
  создания сессий, на первом прогоне. Это баг нативного WebGPU-пути на этих
  графах, а не ошибка конфигурации; в логе — только предупреждения ORT о
  нераспределённых узлах.

### Шум замеров

Один и тот же бинарь на Nemotron CPU дал RTF 0.236 и 0.350 в двух прогонах
(разброс 48%). Поэтому считать доказанными можно только: (а) выигрыш Parakeet
(подтверждён 3 прогонами: 0.027–0.028 против 0.037–0.041) и (б) разницу
`hp`/`lp` (0.027 против 0.461 — 17x, это не шум). Остальные различия — в
пределах шума; для точных выводов нужны повторные прогоны и полный набор из 250
файлов.

### Почему VibeVoice не измерен

VibeVoice ожидает 24 кГц, а корпус CV17 — 16 кГц, поэтому у него нет записи в
`tools/WerEval` (см. `/memories/repo/vibevoice-session-caching.md`): качество
проверяется равенством транскриптов, а не WER. Дополнительный риск для WebGPU —
три int4-графа с внешними данными по 840 МБ, что упирается в лимит
`maxStorageBufferBindingSize` адаптера. Нужен отдельный harness.

## Защита от падения

`RecognizerFactory.Create` понижает запрос `webgpu*` до CPU для Qwen3
(streaming и обычной), потому что падение нативное и перехватить его нельзя:
сохранённая настройка не должна ронять приложение. Для остальных моделей запрос
проводится как есть — там ошибка не приводит к падению, только к невыгодной
производительности.

## Настройки приложений и пакетирование

В обоих приложениях в списке Execution Provider добавлены `webgpu:hp` и
`webgpu:lp`:

- `apps/VoiceType.WinUI/.../Views/SettingsWindow.xaml`;
- `apps/VoiceType.Uno/.../Presentation/SettingsViewModel.cs`.

Плагин и компилятор шейдеров уезжают вместе с приложением:

| Приложение | Раскладка нативных файлов |
|---|---|
| VoiceType.WinUI (MSIX, win-x64) | `onnxruntime_providers_webgpu.dll`, `dxcompiler.dll`, `dxil.dll` в корне вывода |
| VoiceType.Uno (WinAppSDK head) | `runtimes/win-x64/native/` |

`WebGpuExecutionProvider.ResolvePluginPath` ищет сначала
`runtimes/<rid>/native/`, затем корень вывода, поэтому обе раскладки подходят.
Если файла нет, провайдер молча деградирует к CPU с предупреждением — пункт
настроек безопасен.

Проверено сборкой (`Uno` head и `WinUI` Release собираются), но **не**
прогоном приложений с выбранным WebGPU: замеры сделаны через `WerEval`. На
Android плагина нет вообще (в пакете отсутствуют android-RID), там запрос
`webgpu` уйдёт в CPU.

## Рекомендации

1. В настройках предлагать WebGPU **как отдельный пункт** и, если он выбран,
   использовать `webgpu:hp` — это даёт дискретную карту.
2. Список адаптеров (`--list-gpus`) показывать как справку; передавать в
   `powerPreference`, а не индекс.
3. По умолчанию WebGPU включать только для Parakeet-подобных моделей
   (цельная оффлайн-транскрипция); для Nemotron оставлять CPU.
4. Qwen3 + WebGPU — запрещено (реализовано в `RecognizerFactory`).
5. Перед изменением версий: держать пару плагин 0.4.0 ↔ ORT 1.30.0, копировать
   `dxcompiler.dll`/`dxil.dll`, и после любого бампа перепроверять пункт 4 —
   падение может быть исправлено или, наоборот, появиться на других моделях.
