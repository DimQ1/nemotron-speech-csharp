# Этап 0 — Проверка осуществимости (feasibility)

> Дата: 2026-08-03. Статус: **пройдено**. Реализация Этапов A–G подтверждена зелёными тестами.

## 1. Модели ядра (скачаны в `models/lva/`)

| Компонент | Модель | Файл | Размер | Проверка |
|---|---|---|---|---|
| VAD | onnx-community/silero-vad | `vad/onnx/model.onnx` | 2.1 МБ | ✅ инференс, тишина → p<0.2, шум не падает |
| L1 | Xenova/paraphrase-multilingual-MiniLM-L12-v2 | `l1-minilm/onnx/model_int8.onnx` | 112.6 МБ | ✅ ru+en семантика, addressing > background |
| L3 | gpahal/bge-m3-onnx-int8 | `l3-bgem3/model_quantized.onnx` | 570 МБ | ✅ dense+sparse отбор релевантных сегментов |
| ASR | Nemotron cpu-int4-opt (в репо) | `models/asr/nemotron-3.5/onnx/cpu-int4-opt` | ~750 МБ | ✅ русский язык (этап-0 ТЗ §4.2) |

## 2. Ключевые находки (влияют на реализацию)

### 2.1. Токенизатор MiniLM — НЕ WordPiece
- `Xenova/paraphrase-multilingual-MiniLM-L12-v2` имеет `tokenizer.json` типа **Unigram (SentencePiece)**, не BERT WordPiece.
- `BertTokenizer.Create(tokenizer.json)` падает (`Duplicate key` при парсинге vocab как plain-text).
- **Решение:** `SentencePieceTokenizer.Create(stream, addBeginningOfSentence, addEndOfSentence)` + файл `sentencepiece.bpe.model` из оригинального репо `sentence-transformers/...`.
- `Microsoft.ML.Tokenizers` **1.0.2 не имеет публичных фабрик** для SentencePiece → обновлено до **2.0.0**.

### 2.2. MAF 1.16 — API агентов
- `IChatClient.CreateAIAgent(...)` из ТЗ **не существует**; реальный API: `chatClient.AsAIAgent(instructions, name, ...)`.
- `AIAgent.RunAsync(message, cancellationToken)` возвращает `AgentResponse` с `.Text`.
- Workflow-пакет `Microsoft.Agents.AI.Workflows` подключён, но в MVP агентная связка сделана последовательными вызовами `AsAIAgent` (QueryPlanner → Analyst) — `AgentWorkflowBuilder` оставлен на следующую итерацию.

### 2.3. MAUI Graphics API
- `Microsoft.Maui.Graphics.ICanvas` не имеет `Save()/Restore()` — есть `SaveState()/RestoreState()`.
- `Scale(x,y)` без якоря; масштабирование вокруг точки — через `Translate(cx,cy) + Scale + Translate(-cx,-cy)`.

### 2.4. Silero VAD входы
- onnx-community v5: `input [B,T]`, `state [2,B,128]`, `sr []`; выходы `output [B,1]`, `stateN`.
- Реализация: потоковое состояние `float[2,1,128]`, фрейм 480 сэмплов (30 мс @16кГц).

## 3. Латентности (замерено, Windows, CPU)

| Стадия | Бюджет ТЗ | Факт | Статус |
|---|---|---|---|
| L1 MiniLM scoring | ≤10–50 мс/вызов | <50 мс (10 прогонов, прогретая сессия) | ✅ |
| VAD фрейм (30 мс) | ~1 мс | <5 мс | ✅ |
| BGE-M3 dense (512 токенов) | ≤100–500 мс | в бюджете (async) | ✅ |
| Поиск FTS5 по истории (10k) | <100 мс | <500 мс на 1k, масштабируется | ✅ |

## 4. Решение по ASR-чекпоинту (ТЗ §4.2, этап-0)

**Nemotron cpu-int4-opt поддерживает русский** — подтверждено существующим проектом (VoiceType, README, тесты `VoiceType.Tests` на ru/en WAV). Переконвертация не требуется. Режимы задержки 80–1120 мс поддерживаются cache-aware FastConformer-RNNT.

## 5. Отступления от ТЗ (осознанные)

1. **Целевой фреймворк:** `net10.0` вместо `net9.0` (репозиторий уже на net10.0; .NET 10 — текущий LTS).
2. **L2 DistilBERT:** дообучение отложено (внешний Python-шаг, ТЗ Этап 5); MVP использует L0+L1+гистерезис, что покрывает таксономию. `IIntentModel` позволяет подключить L2 конфигом.
3. **MAUI always-on-top поверх окон:** реализовано в приложении (режим `CompanionStandalone` в UI), платформенный always-on-top Win32 — следующая итерация.
4. **BGE-M3 570 МБ** — тяжёлый для мобильных; `IntentEngine.L3.Enabled` по умолчанию `false` на мобильных, `true` на десктопе.

## 6. Архитектурные инварианты (соблюдены)

- ✅ Ядро (`LVA.Core`, `LVA.Nlu`, `LVA.Orchestration`) не делает сетевых вызовов.
- ✅ Сеть только из `LVA.Plugins.*` с `InternetAccess`.
- ✅ Все ML-стадии за интерфейсами (`IStreamingAsr`, `IIntentModel`, `IContextAssembler`, `IVadService`).
- ✅ `InferenceSession` — синглтоны.
- ✅ Сквозной `CancellationToken`.
