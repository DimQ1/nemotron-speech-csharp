# VoiceType — Режим голосовых команд (Voice Intent Mode): анализ и план реализации

**Ветка:** `shdmitry/voice-intent-mode`
**Статус:** План (без реализации)
**Дата:** 2026-08-05

---

## 1. Требование

Добавить в VoiceType (WPF) **опциональный, легко отключаемый** режим, в котором приложение:

1. **Понимает голосовые команды управления диктовкой.** Пользователь говорит «начать печатать текст», «стоп», «набери в VS Code» — приложение распознаёт намерение (intent) и включает/выключает text injection, а не печатает команду как текст.
2. **Запоминает целевые окна и поля.** Приложение помнит, в каких окнах и полях ввода оно печатало (процесс, заголовок окна, элемент управления).
3. **Уточняет неоднозначности.** «Набери следующий текст в Visual Studio Code» → если в VS Code ранее печатали в несколько полей, приложение спрашивает, в какое именно (диалог уточнения + голосовое подтверждение).
4. **Всё на CPU.** Модели намерений — локальные ONNX, инференс только на процессоре.
5. **Отдельное окно настроек.** Все настройки режима и моделей намерений — в отдельном окне.
6. **Полностью отключаемо.** Один главный тумблер; при выключении — ноль влияния на текущий конвейер диктовки, модель не загружается.

---

## 2. Анализ подхода LVA (e:\Learn\LVA)

LVA уже реализует каскад анализа намерений, который прямо переиспользуем концептуально:

### 2.1. Каскад L0 → L1 (+ гистерезис)

Из `src/LVA.Nlu/Intents/IntentEngine.cs`:

- **L0 — `HeuristicScorer`** (всегда, синхронно, <1 мс): лексические триггеры из таксономии, вопросительные слова RU/EN, знак `?`. Оценки 0.5–0.8.
- **L1 — `MiniLmEmbeddingModel`** (`IIntentModel`): `paraphrase-multilingual-MiniLM-L12-v2` INT8 ONNX (~112 МБ), SentencePiece-токенизатор, mean-pooling + L2-нормализация, косинусная близость к **кэшированным эмбеддингам exemplar-фраз** каждого интента. Порог `ThresholdAddressing = 0.85`.
- **Фьюжн:** `max(L0, L1)` по каждому интенту — L0 лексический, L1 семантический.
- **`HysteresisFilter` M-of-N** (3 из 5 по умолчанию): состояние включается, когда M из последних N оценок ≥ порога, и симметрично выключается. Устраняет мерцание решений на частичных гипотезах ASR.
- **L2 (DistilBERT-классификатор) в LVA отложен** (ADR 0001): `IIntentModel` позволяет добавить его конфигом без пересборки. Для VoiceType тоже не нужен в MVP.

### 2.2. Таксономия намерений

Из `src/LVA.Core/Intents/IntentTaxonomy.cs`: интенты описаны в редактируемом `intents.json` (`id`, `exemplars[]`, `lexicalTriggers[]`), есть `Default`. Пользователь может править exemplar-фразы — важная фича, переносим.

### 2.3. Контекст

`ContextBuffer` — кольцевой буфер сегментов речи с дедупликацией по косинусу >0.9. Для VoiceType нужен упрощённый аналог: последние N распознанных фраз + история целей инжекции.

### 2.4. Модели LVA (уже лежат локально)

| Модель | Путь | Размер | Роль |
|---|---|---|---|
| MiniLM-L12-v2 INT8 ONNX | `models/lva/embeddings/l1-minilm/onnx/model_int8.onnx` | 112.6 МБ | L1 intent-скоринг (CPU) |
| SentencePiece tokenizer | `models/lva/embeddings/l1-minilm/sentencepiece.bpe.model` | 4.8 МБ | Токенизация |
| BGE-M3 quantized | `models/lva/embeddings/l3-bgem3/` | 543.6 МБ | Тяжёлая, **не нужна** для VoiceType |
| Silero VAD | `models/lva/vad/silero/onnx/model.onnx` | 2.1 МБ | У VoiceType уже есть VAD в SpeechLib |

**Вывод по моделям:** для VoiceType достаточно MiniLM INT8 (112 МБ, CPU-инференс ~10–30 мс на фразу). BGE-M3 избыточен. Зависимости: `Microsoft.ML.OnnxRuntime` (CPU, уже тянется через SpeechLib) + `Microsoft.ML.Tokenizers` (новый пакет, ~2.0.0, как в LVA).

---

## 3. Текущая архитектура VoiceType и точки интеграции

### 3.1. Что есть сейчас

- **Инжекция:** `Services/TextInjector.cs` (static, SendInput Unicode / Clipboard), `MainViewModel.ApplyInjection()` шлёт дельты текста по мере распознавания.
- **Целевое окно:** `MainViewModel` захватывает `_injectionTargetWindow = GetForegroundWindow()` при включении инжекции; `CanInjectToTargetWindow()` + `DisableInjectionOnFocusChange` блокируют печать при смене фокуса. **Окно запоминается только на сессию, поля ввода не запоминаются вообще.**
- **Включение инжекции:** только вручную — чекбокс / хоткей `Ctrl+Shift+I` (`ToggleTextInjection()`).
- **Настройки:** `Models/AppSettings.cs` (JSON через `Services/SettingsService.cs`), окно `Views/SettingsWindow.xaml` + `SettingsViewModel`.
- **Распознавание:** `Services/RecognitionService.cs` → SpeechLib/Nemotron, события частичных/финальных гипотез в `MainViewModel`.
- **Загрузчик моделей:** `ModelDownloaderService` + `ModelDownloaderWindow` — переиспользуем для скачивания intent-модели.

### 3.2. Точки интеграции нового режима

```
Распознанный текст (partial/final)
        │
        ▼
┌─────────────────────┐   выкл   ┌──────────────────────────┐
│ VoiceIntentRouter   │ ───────► │ текущий путь (диктовка → │
│ (новый, опционально)│          │ TextInjector) — БЕЗ ИЗМ. │
└─────────────────────┘          └──────────────────────────┘
        │ вкл
        ▼
  L0 HeuristicScorer ──► L1 MiniLmIntentScorer ──► HysteresisFilter
        │
        ▼
  Intent: Dictation / StartTyping / StopTyping /
          SwitchTarget / ClarifyTarget / Cancel
        │
        ▼
  ┌────────────────────┐      ┌─────────────────────────┐
  │ InjectionController │────► │ WindowTargetHistory      │
  │ (вкл/выкл печать,   │      │ (процесс, окно, UIA-     │
  │  retarget окна)     │      │  элемент, время, счётчик)│
  └────────────────────┘      └─────────────────────────┘
                                        │ неоднозначность
                                        ▼
                              ClarifyTargetDialog (XAML)
                              + голосовой ответ «первое/второе»
```

**Ключевой принцип:** при `VoiceIntentModeEnabled = false` роутер не создаётся, события распознавания идут по текущему пути без единой дополнительной проверки кроме одного `if` — поведение приложения бит-в-бит как сейчас.

---

## 4. Дизайн

### 4.1. Новые компоненты (все в `apps/VoiceType/src/VoiceType/`)

| Файл | Назначение |
|---|---|
| `Services/Intents/VoiceIntentIds.cs` | Канонические id: `dictation`, `start_typing`, `stop_typing`, `switch_target`, `cancel` |
| `Services/Intents/VoiceIntentTaxonomy.cs` | Таксономия + `Default` (RU/EN exemplars) + загрузка из `data/voice-intents.json` |
| `Services/Intents/HeuristicScorer.cs` | Порт L0 из LVA: триггеры («начни печатать», «стоп диктовка», «набери в», "type this in", «отмена») |
| `Services/Intents/IIntentScorer.cs` | `Task<IntentScores> ScoreAsync(string text, CancellationToken)` — как `IIntentModel` в LVA |
| `Services/Intents/MiniLmIntentScorer.cs` | Порт L1: SentencePiece + ONNX + mean-pool + косинус к exemplar-кэшу |
| `Services/Intents/HysteresisFilter.cs` | Порт M-of-N фильтра из LVA |
| `Services/Intents/VoiceIntentRouter.cs` | Фасад: L0 → L1 (если может изменить исход) → гистерезис → событие `IntentDetected` |
| `Services/WindowTargetHistory.cs` | Журнал целей: `record WindowTarget(ProcessName, WindowTitle, AutomationId?, ControlType?, DateTimeOffset LastUsed, int UseCount)`; per-app топ-N; JSON-персистенция |
| `Services/ForegroundWindowInspector.cs` | Win32: `GetForegroundWindow`, `GetWindowThreadProcessId`, `GetWindowText` + UI Automation (`IUIAutomation.ElementFromHandle`, focused element → `AutomationId`, `ControlType`, `Name`) |
| `Services/InjectionController.cs` | Единая точка: включить/выключить печать, retarget на окно из истории, `SetForegroundWindow` + `AttachThreadInput` перед инжекцией |
| `ViewModels/VoiceCommandsSettingsViewModel.cs` | MVVM для нового окна настроек |
| `Views/VoiceCommandsSettingsWindow.xaml(.cs)` | Отдельное окно настроек режима |
| `Views/ClarifyTargetDialog.xaml(.cs)` + VM | Диалог «В какое поле печатать?» со списком полей из истории |

Зависимости в `VoiceType.csproj`: `Microsoft.ML.Tokenizers` (SentencePiece). ONNX Runtime уже есть транзитивно. UI Automation: `UIAutomationClient`/`UIAutomationTypes` (WPF Reference — бесплатно в WPF-проекте).

### 4.2. Конечный автомат режима

```
Off ──вкл тумблер──► Listening(диктовка как обычно)
Listening ──intent=start_typing──► Typing (инжекция вкл, цель = текущее окно)
Typing    ──intent=stop_typing──► Listening (инжекция выкл)
Typing    ──intent=switch_target──► Resolving
Resolving ──1 кандидат──► Typing (retarget)
Resolving ──N кандидатов──► Clarifying (диалог) ──выбор──► Typing
*         ──intent=cancel──► Listening
```

Команда, распознанная как intent, **не инжектится** — дельта текста команды отсекается в роутере до `ApplyInjection`.

### 4.3. Запоминание окон и полей

- Запись в `WindowTargetHistory` — **в момент каждой успешной инжекции** (в `InjectionController`): снимок `ForegroundWindowInspector` (процесс, заголовок, UIA focused element: `AutomationId`, `ControlType`, `Name`).
- Хранение: `data/window-targets.json`, топ 10 на процесс, LRU по `LastUsed`.
- Разрешение «набери в VS Code»: матч процесса (`Code.exe` ↔ «visual studio code»/«vs code» через таблицу алиасов в настройках) → кандидаты полей из истории → 1 кандидат = retarget молча; N кандидатов = `ClarifyTargetDialog` («В какое поле? 1: редактор, 2: терминал») + голосовой ответ («первое») парсится L0-триггерами.
- **Приватность:** журнал — только локальный JSON, очищается кнопкой в настройках; можно выключить запоминание отдельно.

### 4.4. Настройки (`AppSettings` + отдельное окно)

Новая секция `VoiceIntentSettings` в `AppSettings` (обратная совместимость JSON — все поля с дефолтами):

```csharp
public sealed class VoiceIntentSettings
{
    public bool Enabled { get; set; } = false;            // ГЛАВНЫЙ тумблер
    public string IntentModelPath { get; set; } = "";     // пусто = только L0
    public float ConfidenceThreshold { get; set; } = 0.85f;
    public int HysteresisM { get; set; } = 2;             // мягче, чем в LVA (3/5)
    public int HysteresisN { get; set; } = 3;             // — быстрее реакция
    public bool RememberTargets { get; set; } = true;
    public bool ConfirmTargetSwitch { get; set; } = true; // диалог уточнения
    public int HistoryPerProcess { get; set; } = 10;
    public Dictionary<string,string[]> AppAliases { get; set; } = new(); // "code.exe" -> ["vs code","visual studio code"]
    public string CustomIntentsFile { get; set; } = "";   // пользовательский intents.json
}
```

**Окно `VoiceCommandsSettingsWindow`** (открывается кнопкой из основного `SettingsWindow`, раздел «Voice Commands»):
- Master toggle + индикатор «режим активен/модель загружена»;
- выбор/скачивание intent-модели (кнопка → `ModelDownloaderWindow`, новая папка `voice-intents`);
- порог уверенности, гистерезис M/N;
- тумблеры «Запоминать цели», «Спрашивать при неоднозначности»;
- таблица алиасов приложений; редактор exemplar-фраз (простой многострочный TextBox на интент);
- журнал целей (просмотр + «Очистить»).

### 4.5. Отключение (требование «слегка отключить»)

1. `Enabled=false` (дефолт) → роутер не создаётся, модель не грузится, ни одного лишнего вызова на горячем пути — один `if` в `MainViewModel`.
2. Роутер `IDisposable`; выключение на лету освобождает ONNX-сессию (~120 МБ RAM).
3. При `IntentModelPath=""` работает чистый L0 (ноль новых зависимостей в рантайме).

---

## 5. План реализации по этапам

**Этап 0 — каркас (ветка уже создана)**
1. `VoiceIntentSettings` в `AppSettings.cs`; загрузка/сохранение; миграция не нужна (дефолты).
2. Пустое окно `VoiceCommandsSettingsWindow` + VM + кнопка в `SettingsWindow`.
3. Тест: настройки сериализуются, окно открывается.

**Этап 1 — L0-ядро без модели**
4. `VoiceIntentIds`, `VoiceIntentTaxonomy` (+`voice-intents.json`), порт `HeuristicScorer`, `HysteresisFilter`, `VoiceIntentRouter` (L0-only).
5. `InjectionController`: обёртка над включением/выключением инжекции в `MainViewModel` (рефакторинг без смены поведения).
6. Конечный автомат Off/Listening/Typing; команды старт/стоп/отмена.
7. Unit-тесты `Unit_VoiceIntent*`: триггеры RU/EN, гистерезис, «команда не печатается».

**Этап 2 — L1 MiniLM**
8. `Microsoft.ML.Tokenizers` в csproj; порт `MiniLmIntentScorer`; скачивание модели через `ModelDownloaderService` (папка `voice-intents/l1-minilm`); ленивые загрузка/выгрузка.
9. Порог/гистерезис из настроек; E2E-тест скоринга на реальной модели (маркер `E2E_`).

**Этап 3 — память целей и retarget**
10. `ForegroundWindowInspector` (Win32 + UIA), `WindowTargetHistory` (+JSON), запись при инжекции.
11. Разрешение `switch_target`: алиасы → кандидаты → `SetForegroundWindow`/`AttachThreadInput` retarget.
12. Unit-тесты истории (LRU, сериализация); ручная проверка retarget на Notepad/VS Code.

**Этап 4 — уточнение неоднозначностей**
13. `ClarifyTargetDialog` + VM; привязка кандидатов; голосовой отбор («первое/второе», повтор).
14. Настройки алиасов и exemplar-фраз в окне; кнопка «Очистить журнал».

**Этап 5 — полировка**
15. Статус в `MainWindow` («Voice commands: on, model loaded»), README-раздел, проверка «выключено = бит-в-бит старое поведение».

## 6. Тестирование

- `dotnet test ... --filter "FullyQualifiedName~Unit_"`: L0-скорер, гистерезис, роутер (мок скорера), история целей, сериализация настроек.
- `E2E_`: MiniLM-скоринг на реальной модели (как `E2E_WordTimingsRegressionTests`).
- Ручные: сценарии из требования (старт/стоп голосом; «набери в VS Code» → уточнение; выключение режима на лету).

### 6.1. Инструкция: прогон на простом приложении (Notepad)

Цель: проверить сквозной цикл голосовых команд на приложении с минимальным числом полей ввода.

**Тестовое приложение:** `notepad.exe` (одно поле редактирования, UIA `Edit` с `AutomationId=15`). Дополнительный вариант с несколькими полями — тест-харнесс `tools/IntentTestHarness` (WPF-окно с 3 TextBox: «Поле 1/2/3»), чтобы проверить запоминание полей и диалог уточнения без VS Code.

**Сценарий A — Notepad (одно поле):**
1. Открыть Notepad, сфокусировать поле ввода.
2. Включить Voice Intent Mode в настройках (L0-only, без модели).
3. Продиктовать: «начни печатать текст» → инжекция включается, статус в MainWindow меняется, сама команда не печатается.
4. Продиктовать произвольный текст → появляется в Notepad.
5. Продиктовать: «стоп диктовка» → инжекция выключается, последующий текст не печатается.
6. Продиктовать: «отмена» → режим остаётся в Listening.
7. Проверить `data/window-targets.json`: записана цель `notepad.exe` с UIA-элементом.

**Сценарий B — тест-харнесс (несколько полей):**
1. Запустить `IntentTestHarness`, продиктовать текст в «Поле 1», затем в «Поле 2» (переключая фокус мышью) → в журнале две цели одного процесса.
2. Продиктовать: «набери в тест харнесс» → 2 кандидата → `ClarifyTargetDialog` со списком «Поле 1/Поле 2».
3. Ответить голосом «первое» → фокус и инжекция уходят в «Поле 1».

**Сценарий C — отключение:**
1. Выключить master-toggle на лету → ONNX-сессия выгружена (память), инжекция работает как раньше вручную.
2. Перезапуск с `Enabled=false` → роутер не создаётся, поведение бит-в-бит как в master.

**Автоматизация:** сценарии A/B частично покрываются unit-тестами роутера и истории; E2E-инжекция в Notepad — вручную (UIA/SendInput не стабильны в CI).

## 7. Риски и допущения

| Риск | Митигация |
|---|---|
| Команда уходит в печать до классификации (стриминг) | Классифицировать на сегментных границах (VAD/финальная гипотеза); настройка «буферизовать первые N слов» при включённом режиме |
| UIA недоступен в части приложений (игры, elevated) | Fallback: только окно без поля; журнал помечает `AutomationId=null` |
| `SetForegroundWindow` блокируется Windows | `AttachThreadInput` + `AllowSetForegroundWindow`; при неудаче — статус «переключитесь в окно вручную» |
| MiniLM 112 МБ RAM | Ленивая загрузка, выгрузка при выключении; L0-only режим без модели |
| Ложные срабатывания команд | Гистерезис, порог 0.85, пользовательские exemplars, журнал срабатываний |
