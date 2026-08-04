# LVA (LocalVoiceAssistant) — план реализации

> Дата: 2026-08-03. Основа: ТЗ «Локальный голосовой ассистент» + HTML-прототип (скопирован в `docs/design/lva/index.html`, макеты в `docs/design/lva/mockups/`).
> Реализация идёт **внутри текущего репозитория** `nemotron-speech-csharp`: ASR-движок (SpeechLib.Nemotron + ONNX Runtime GenAI), менеджер моделей, WAV-корпус и тестовая инфраструктура уже существуют и переиспользуются.

## 1. Что уже есть в репозитории (переиспользуем)

| Возможность ТЗ | Существующий код | Статус |
|---|---|---|
| ASR Nemotron streaming, CPU int4, LatencyMode | `SpeechLib.Nemotron`, `SpeechLib/Interfaces/IStreamingSpeechRecognizer.cs`, `SpeechLib/ModelSession.cs` | ✅ готово, русский поддерживается |
| VAD | `use_vad` внутри GenAI-пайплайна + внешний Silero ONNX (скачан в `models/lva/vad/silero`) | ⚠ нужен внешний `IVadService` поверх Silero как источник истины |
| Менеджер загрузки моделей (HF, прогресс, хэши) | `VoiceType/Services/ModelDownloaderService.cs` | ✅ концепция, нужен порт в LVA.Models |
| Аудиозахват (Windows) | `SpeechLib.Audio.NAudio2/3` | ✅ desktop; Plugin.Maui.Audio — для MAUI |
| WAV-корпус для тестов | `Test-Audio/`, `VoiceType.Tests` | ✅ |
| UI-приложение Windows | `VoiceType.WinUI` (WinUI 3, диктофон) | ⚠ не MAUI, другой UX; берём паттерны (hotkeys, DI, MSIX) |

## 2. Решение о структуре

- **Целевой фреймворк:** `net10.0` (как все проекты репозитория; ТЗ писалось под .NET 9 — отклонение осознанное, ADR).
- **Новые проекты** в `src/LVA.*` (net10.0, Nullable+ImplicitUsings, file-scoped namespaces — по конвенциям репо):
  1. `LVA.Core` — контракты событий, `ConversationStateMachine`, `ContextBuffer`, `AssistantTask`, конфигурация. Без ML.
  2. `LVA.Nlu` — `SileroVadService`, `HypothesisStabilizer`, `IntentEngine` (L0/L1), `ActivationGate`, `IIntentModel`, MiniLM-эмбеддинги (OnnxRuntime + Microsoft.ML.Tokenizers).
  3. `LVA.Asr.NemotronAdapter` — адаптер `IStreamingSpeechRecognizer` → `IStreamingAsr` (ТЗ §4.2).
  4. `LVA.Orchestration` — `ToolRouter`(rules), `TaskTracker`, `NotificationHub`.
  5. `LVA.Plugins.Abstractions` — `IAssistantPlugin`, `PluginManifest`, `IPluginContext`.
  6. `LVA.Plugins.Host` — discovery по `*.lva-plugin.json`, `AssemblyLoadContext`, разрешения.
  7. `LVA.Plugins.WebSearch` — MAF-оркестрация (QueryPlanner → Search/Fetch → Researcher → Analyst), `ISearchAgent` для изоляции MAF.
  8. `LVA.Models` — менеджер моделей (манифест+SHA256, прогресс) на базе VoiceType-паттерна.
  9. `LVA.App` — .NET MAUI (Windows + Android + iOS): Чат/Питомец/Standalone, SkiaSharp-питомец, звуки, история.
  10. `LVA.Tests.*` — xUnit.
- **Регистрация в solution:** добавить в `NemotronSpeech.slnx`.

## 3. Этапы (строгий порядок, DoD из ТЗ §9)

### Этап A — Core (аналог Этапа 1 каркаса)
Контракты событий (`SpeechStarted/Ended`, `PartialTranscript`, `ActivationRequested`, `QuestionReady`, `TaskProgress`, `ToolResult`), `ConversationStateMachine` (Idle→ContextCapture→QuestionReady, cancel/timeout), `ContextBuffer` (кольцевой, дедупликация по косинусу >0.9, ретроспективный старт, автоочистка 90 с). Fake-реализации для тестов.
**DoD:** юнит-тесты state machine (все переходы §4.7) и буфера зелёные.

### Этап B — NLU (Этапы 2–3)
- `SileroVadService` (`models/lva/vad/silero/onnx/model.onnx`, 30 мс фреймы, потоковое состояние h/c).
- `NemotronStreamingAsr` адаптер над `IStreamingSpeechRecognizer`.
- `HypothesisStabilizer`: LCP + N=3; кэш префикса.
- `IIntentModel` + `MiniLmEmbeddingModel` (mean-pooling, L2-norm, косинус; tokenizer из `tokenizer.json` через Microsoft.ML.Tokenizers); L0-эвристики (вопросительные слова ru/en); гистерезис M=3/N=5.
**DoD:** WAV ru/en → частички в реальном времени; L1 ≤ 10 мс/вызов; тесты стабилизатора/гистерезиса.

### Этап C — Активация (Этап 4)
`ActivationGate` (семантика ≥0.85 + лексика из `intents.json`), три сигнала завершения, `QuestionReady`. Сценарный тест «обращение → вопрос в 2 подхода → выполни»; запись фона → 0 ложных активаций.

### Этап D — Оркестрация + хост плагинов (Этап 6)
Rules-роутер по `capabilities`; `TaskTracker` (Started→InProgress→Completed/Failed/Cancelled); `NotificationHub` (события для UI/звука); плагин-хост с ALC и проверкой `InternetAccess` (без него `HttpFactory` не выдаётся — тест).

### Этап E — WebSearch-плагин (Этап 7)
`ISearchAgent` (изоляция MAF), `IChatClient`-фабрика по конфигу, Brave/Bing провайдеры, Readability-fetch, workflow QueryPlanner→Researcher→Analyst, `SearchReport`, стриминг→`TaskProgress`, пост-валидация источников, fallback на BGE-M3-резюме. Mock `IChatClient` в интеграционных тестах.

### Этап F — MAUI UI (Этап 8)
Дизайн-токены из прототипа (цвета/радиусы/моушн §14.7 → `DesignTokens.xaml`). 4 режима отображения, единый `ConversationViewModel`; питомец на SkiaSharp (16 состояний, следящие зрачки, drag/pet/poke); standalone-окно always-on-top (Windows) + popup; хоткеи; светлая/тёмная тема.

### Этап G — L3, звуки, история (Этапы 9, 9A, 9B)
BGE-M3 `IContextAssembler` (async, α=0.6); `IAudioFeedbackService` (soundpack); `IChatHistoryStore` SQLite+FTS5 (<100 мс на 10k); TTS за `ITtsEngine` (Piper) — интерфейс сейчас, движок опционально.

## 4. Модели (уже скачаны в `models/lva/`)

| Роль | Модель | Файл | Размер |
|---|---|---|---|
| VAD | onnx-community/silero-vad | `vad/onnx/model.onnx` | 2.1 МБ |
| L1 | Xenova/paraphrase-multilingual-MiniLM-L12-v2 | `l1-minilm/onnx/model_int8.onnx` + tokenizer.json | 112.6 МБ |
| L3 | gpahal/bge-m3-onnx-int8 | `l3-bgem3/model_quantized.onnx` | 570 МБ |
| ASR | Nemotron cpu-int4-opt (в репо) | `models/asr/nemotron-3.5/onnx/cpu-int4-opt` | ~750 МБ |

L2 DistilBERT — дообучение откладывается (Этап 5, внешний Python-шаг); в MVP L1+L0+gate покрывают таксономию, `IIntentModel` позволяет подключить L2 конфигом позже.

## 5. Ключевые риски и решения

1. **Silero VAD из onnx-community** имеет входы `input/state/sr` (v5) — форма состояния проверяется тестом-загрузкой при Этапе B.
2. **MiniLM int8 от Xenova** — входы `input_ids/attention_mask` (+token_type_ids); проверяем `InferenceSession` метаданные, mean-pooling по attention_mask.
3. **BGE-M3 quantized (570 МБ)** — тяжёлый для мобильных; L3 опционален (`IntentEngine.L3.Enabled`), на мобильных выкл. по умолчанию.
4. **MAF preview** — версии пакетов фиксируем в `Directory.Packages.props` плагина; mock-тесты обязательны.
5. **MAUI always-on-top поверх окон** — только Windows API; Android overlay — за разрешением; iOS — нет (§3.3, не обходим).

## 6. Порядок коммитов
A → B → C → D → (E ∥ F) → G → тесты/доки. Каждый этап — зелёная сборка `dotnet build -p:GpuArch=CPU` + свои тесты.
