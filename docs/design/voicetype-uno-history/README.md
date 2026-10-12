# VoiceType.Uno — история записей: воспроизведение, чаты с Gemma, перевод, версии распознавания, спикеры, очередь заданий

Статус: **дизайн** (2026-10-12). Ревизия 2: учтены решения пользователя по открытым вопросам (§16, решения 1–8). Ревизия 3: офлайн-диаризация Nemotron 3 Diarization (ONNX), фоновая переобработка с анализом, живые метки спикеров за флагом (§10, §11; решения 9–11). Часть ядра истории уже реализована в `VoiceType.Uno.Core/Services/History/`; дельты ревизии 3 даны относительно этого кода.
Область: `apps/VoiceType.Uno` — головы `net10.0-android`, `net10.0-desktop` (Skia), `net10.0-windows10.0.26100` (WinUI 3).
Продолжает [дизайн мобильного редизайна](../voicetype-uno-mobile/README.md). Отсюда взяты сетка, брейкпоинты, безопасные зоны,
правило 44×44, боковое меню, нижняя панель записи и страница «Модели» с вкладками.

| Файл | Что это |
|---|---|
| [mockup.html](mockup.html) | Интерактивный макет. Экраны: «Сохранено» на главном экране, меню (History · Chats · Queue), список истории (поиск, фильтр, выбор), карточка записи (Transcript с плеером и режимом правки, Chats, Translate, Versions + diff), страница Chats (список + беседа с несколькими записями), Queue, Settings › History. Листы и диалоги: Re-recognize, пакетное перераспознавание с расписанием, Filter, подтверждение удаления, Rename, Export, Add recordings, Sources, Add to chat. Ревизия 3: транскрипт со спикерами и фильтром, лист Speakers (rename, merge), лист Reprocess, Versions с цепочкой, Insights, Queue с цепочкой и автопереобработкой, Models › Speakers, главный экран с живыми метками и их отставанием. Открывать в браузере, параметры описаны в шапке файла |
| [frame.html](frame.html) | Обёртка, которая задаёт точный viewport (копия из `voicetype-uno-mobile`) |
| [render.ps1](render.ps1) | Рендер 125 сценариев в headless Edge и автоматическая проверка раскладки (`-Only <подстрока>` запускает часть сценариев) |
| [layout-check.json](layout-check.json) | Отчёт последней проверки: **125/125 OK** |
| [screenshots/](screenshots/) | Скриншоты всех сценариев |

Пометка **[не проверено]** стоит у всего, что не подтверждено кодом, пакетом или документацией.

---

## 1. Цели и не-цели

**Цели**

1. **История.** Список прошлых сессий: дата/время, длительность, модель, язык, заголовок (свой или от Gemma), признаки перевода, чатов, режима Inject. Поиск, фильтр, удаление одной записи и нескольких — с подтверждением, которое можно отключить («Don't ask again»).
2. **Воспроизведение с транскриптом.** Плеер синхронизирован с текстом. Текущий сегмент подсвечен, текущее слово подчёркнуто. Тап по сегменту перематывает к нему.
3. **Чаты с Gemma по записям.** Чат — самостоятельная сущность: в нём 1..N записей, их можно добавлять и убирать в любой момент. Свои вопросы и быстрые подсказки, ответ потоком, ссылки «запись + время». Чаты хранятся отдельно от записей и переживают перезапуск. Всё работает на устройстве через Microsoft Agent Framework поверх LiteRT-LM.
4. **Офлайн-перевод сохранённого транскрипта** тем же путём Gemma, что и живой перевод. Перевод хранится рядом с записью, его можно перезапустить на другой язык.
5. **Повторное распознавание** сохранённого аудио другой или той же моделью ASR, в том числе пакетом из нескольких записей. Каждая версия транскрипта хранит модель, язык, настройки и версию приложения. Версии можно переключать и сравнивать (diff), одну из них выбрать текущей. Переводы и чаты ссылаются на версию, из которой сделаны.
6. **Правка транскрипта с версиями.** Правка никогда не меняет существующую версию: сохранение создаёт новую версию `edited`.
7. **Очередь заданий.** Перераспознавание, перевод, заголовки и сводки идут через одну постоянную очередь: одно тяжёлое задание за раз, условия запуска (сейчас, когда простаивает, на зарядке, в окне времени), пауза, отмена, порядок, продолжение после перезапуска.
8. **Экспорт:** чат как Markdown-документ; аудио с `.lrc` и `.srt` для обычных плееров; `.txt` с таймкодами.
9. **Точки входа:** пункты меню History, Chats, Queue и снэкбар «Saved to History · Open» после остановки записи. Без перекрытий на 320–412 px, 800×360 и 1280×800.
10. **Спикеры офлайн** (ревизия 3): «кто когда говорил» моделью Nemotron 3 Diarization, сконвертированной в ONNX и работающей на устройстве. Метки на уровне сегмента и слова, имена и цвета от пользователя, Rename и Merge, фильтр по спикеру в плеере, имена в чатах и экспорте. Версии неизменяемы: видно, какая ASR и какая диаризация дали текст (§10).
11. **Фоновая переобработка** (ревизия 3): одна цепочка «перераспознать → спикеры → анализ Gemma» через ту же очередь и расписание, пакетом по многим записям или автоматически для новых записей ночью на зарядке; результат — новая версия (§11).
12. **Живые метки спикеров** (ревизия 3, условно): включаются, только если тесты производительности на устройстве позволяют; выключатель в меню; при отставании — фоновая диаризация после Stop (§10.7).

**Не-цели (в этой итерации):** синхронизация между устройствами и облако; импорт чужих аудиофайлов (архитектура позволяет, UI нет); история в WinUI-приложении (решение 8: только Uno, но формат папок остаётся переносимым); склейка и разбиение сегментов при правке. Для спикеров: узнавание людей между записями по голосу (это биометрия), больше 8 различимых голосов, раздельное распознавание перекрывающейся речи (multitalker / speaker-masked ASR из гайда модели), облачная диаризация.

## 2. Что есть в коде сейчас

| Факт | Где | Что из этого следует |
|---|---|---|
| `AppPaths.SessionsDir` (`<DataRoot>/sessions`) и `EnsureSessionsDir()` есть, но нигде не вызываются. `AppSettings.SessionsPath` и `SaveAudioMp3` объявлены и тоже не используются | `VoiceType.Uno.Core/Services/AppPaths.cs`, `AppSettings.cs` | Папка истории уже предусмотрена, миграции нет |
| В WinUI есть `SessionManager`: плоские `*.json` (`RecognitionSession`: Id, StartedAt/EndedAt, Language, EngineProvider, RecognizedText, AudioFilePath) и необязательный MP3 через `IAudioRecorder` (`NAudio3AudioRecorder`, только Windows). UI истории нет | `apps/VoiceType.WinUI/.../Services/SessionManager.cs`, `Models/RecognitionSession.cs`, `MainViewModel.PersistSession` | Идею «сохранить при Stop в фоне» берём. Формат плоский, без версий и таймкодов, поэтому не переносим его |
| `RecognitionService.ProcessLoop` получает `float[]`-батчи **уже на частоте распознавателя**: источник создаётся как `Create(mode, _recognizer.SampleRate)`. Батчи при mute отбрасываются | `VoiceType.Uno/Services/RecognitionService.cs` | Аудио пишем отсюда (tee). Сохраняется ровно то, что слышала модель, а сэмпловый счётчик — общая шкала времени для аудио и сегментов |
| `UtterancePipeline` (Parakeet TDT) коммитит фразы по паузам (`StreamingResult.Final`). У `PlainPipeline` (Nemotron и др.) границ фраз нет | там же | Сегменты = фразы Parakeet. Для plain-моделей сегменты режем по знакам конца предложения и по паузам ≥ 1 с |
| Словные таймкоды сейчас есть только приближённые: `Transcriber.AddWordTimings` (NAudio3, CLI) распределяет время чанка по словам пропорционально токенам | `SpeechLib.Audio.NAudio3/Transcriber.cs`, `SpeechLib/Models/WordTiming.cs` | Время сегмента точное (по сэмплам), время слова — оценка. Точные слова для Parakeet — отдельный этап (§9.3) |
| `LiteRTLmNativeTranslator` сам владеет `LiteRtEngine`, на каждый перевод создаёт новую conversation; greedy-сэмплер с повтором без сэмплера при `LiteRtException` | `SpeechLib.LiteRT.Native/LiteRTLmNativeTranslator.cs` | В LiteRtLmSharp **жив только один движок на процесс** (см. §7.1). Чат и перевод обязаны делить один движок |
| LiteRtLmSharp **1.4.0** (натив v0.18.0, сборка форка: апстримный 1.3.0 на nuget.org улучшений не содержит; + runtime win-x64, linux-x64, android-arm64 1.4.0), контекст 2048 у переводчика | `SpeechLib.LiteRT.Native.csproj`, `LiteRTLmNativeOptions.cs` | Для чата нужен свой бюджет контекста (§7.4) |
| Известный сбой: явный сэмплер `Greedy`/`TopK` даёт `UNIMPLEMENTED: Sampler type: 3 not implemented yet` — это **не** нехватка RAM, а отсутствие стратегии на CPU-бэкенде (только `TopP`) | `LiteRTLmNativeTranslator`, замеры в [research/litert/engine-improvements.md](../../research/litert/engine-improvements.md) §3.4 и §5a | В чате явный сэмплер не задаём, используем сэмплер файла модели; в 1.4.0 это `LiteRtSamplerNotSupportedException`, альтернатива — `LiteRtSamplerParams.NearGreedy` (§7.6) |
| Ядро истории частично реализовано: `RecordingManifest` (schema 1, `[JsonExtensionData]` у всех классов), `TranscriptDocument`, `JobModels` (`HistoryJobKind`: Rerecognize, Translate, AutoTitle, Summarize; `JobCondition` с одним `Kind`; `LenientEnumConverter`), `JobScheduler`, `JobQueue` | `VoiceType.Uno.Core/Services/History/` | Дельты ревизии 3 (§10.4, §11.4) — необязательные поля и два новых вида заданий. Новые поля манифеста старое приложение сохранит; неизвестный вид задания — нет (поэтому schema 2 у очереди) |
| Чистый C#-порт обновления кэша Streaming Sortformer v2.1 (`SortformerStreamingState`, `SortformerStreamingUpdater`) без хоста и моделей | `tools/converters/SortformerStreaming/` | Основа рантайма диаризации (§10.3), если логика кэша Nemotron 3 совпадает |

## 3. Пользовательские сценарии

1. **Записал → открыл.** Stop → запись сохраняется в фоне → над панелью команд появляется снэкбар `Saved to History · 12:34 · audio + transcript  [Open] [×]`. Снэкбар держится 8 с и исчезает при следующем Record. `Open` открывает карточку записи на вкладке Transcript.
2. **Найти старую запись.** Меню → History → поиск «budget». Ищется по заголовку (своему и сгенерированному), транскрипту (текущая версия), переводам и чатам, в которые входит запись. Совпадение из чата показывается строкой-сниппетом. Фильтр (лист): с переводом / используется в чате / есть аудио / диктовки Inject, язык, модель, сортировка.
3. **Прослушать.** Play → текст следует за плеером. Тап по сегменту → перемотка. Кнопки ±10 с, скорость 0.75–2×. Кнопка «Follow» возвращает автопрокрутку, если пользователь прокрутил текст сам.
4. **Спросить по одной записи.** В карточке записи `✦` (Ask Gemma) → создаётся чат с этой записью и открывается страница чата → быстрая подсказка `Action items` или свой вопрос → ответ идёт потоком, Send превращается в Stop. Под ответом чипы-источники `Weekly sync 01:30`: тап открывает запись на Transcript и перематывает. Чат сохраняется после каждого ответа.
5. **Спросить по нескольким записям.** В чате `+ Add recording` (или в карточке записи `Add to chat…`) → отметить ещё две записи → в ленте появляется событие «Supplier call and Interview Maria added · Gemma now reads 3 recordings». Следующий ответ опирается на сводки всех трёх и найденные фрагменты, ссылки показывают запись и время. Лишнюю запись убирают в листе Sources.
6. **Перевести.** Вкладка Translate → `→ Russian ▾` → прогресс по предложениям с Cancel. Готовый перевод синхронизирован с плеером. Можно перевести ещё на один язык: переводы копятся, выбор в том же списке.
7. **Перераспознать.** Versions → `Re-recognize audio…` → лист: установленная модель, язык, VAD, «сделать текущей», когда запускать → Start. Задание уходит в очередь (уведомление на Android). Появляется v3 → `Compare` показывает diff с v2 → `Make current`.
8. **Перераспознать пачкой ночью.** History → долгий тап → выбрать 3 записи → `↻` → лист «Re-recognize 3 recordings»: модель, язык «как у каждой записи», `In a time window 01:00–06:00` + `Only while charging` → Schedule. Меню → Queue показывает пакет с условиями `Tonight 01:00–06:00`, `When charging`; его можно запустить сейчас, изменить расписание, поставить на паузу, отменить, переставить.
9. **Поправить текст.** Transcript → `✎` → сегменты становятся полями → правка → `Save as new version` → появляется v3 `edited` (родитель v2), она текущая; v2 не изменилась и доступна в Versions.
10. **Переименовать.** Через несколько минут после сохранения Gemma даёт заголовок (задание низкого приоритета). `⋯ → Rename` — свой заголовок; подсказка «Generated by Gemma» и `Regenerate`. Свой заголовок Gemma никогда не перезаписывает.
11. **Экспортировать.** `⋯ → Export` → отметить `.txt` с таймкодами и «Audio for media players» → Share или Save to…: получаются `…ogg`, `…lrc`, `…srt` с одним именем. Из чата: `⋯ → Export` → Markdown.
12. **Почистить место.** History → выбор нескольких → Delete → диалог «Delete 3 recordings?» с флажком «Don't ask again». Или Settings › History: срок хранения аудио (7…365 дней или Forever) и лимит места. Текст, версии, переводы и чаты автоматически не удаляются никогда.
13. **Кто что сказал.** После Stop запись сохраняется, а когда приложение простаивает, задание `diarize` размечает спикеров (`Speakers.AfterStop = Best`). Transcript показывает `● Mark`, `● Anna`, `● Speaker 3` над репликами. `4 speakers ▾` → лист Speakers → тап по `Speaker 3` → имя «Oleg» (подсказка Gemma `Gemma suggests “Oleg”` — одним тапом). `Speaker 4` говорит 38 с → `Merge…` с Mark. Фильтр `Anna only` → плеер перескакивает к следующей реплике Анны.
14. **Переобработать ночью.** Versions → `Reprocess…` → Parakeet FP32 + Detect speakers (Best) + Summary → `When: Tonight 01:00–06:00 · while charging` → Schedule. Утром: v4 текущая, со спикерами; во вкладке Chats записи — карточка Insights со сводкой, задачами по людям и временем речи; первый вопрос в чате отвечается без подготовки сводок.
15. **Автоматически для всех новых записей.** Settings › Speakers and reprocessing → `Auto-reprocess new recordings` + профиль. Каждая новая запись получает цепочку; Queue показывает `Auto-reprocess · 2 new recordings` с условиями.
16. **Живые метки (если устройство тянет).** Models › Speakers → `Test this device` → «OK for live labels» → меню → MODE → `Live speaker labels`. Во время записи над фразами появляются имена через ~1 с (десктоп) или ~10 с (телефон, режим Delayed). Если устройство не успевает — плашка «Speaker labels paused… will be added after Stop», после Stop работает фоновая диаризация.

## 4. Информационная архитектура

**Решение: три верхнеуровневых раздела в меню — History, Chats, Queue.** Карточка записи — четыре вкладки `Transcript | Chats | Translate | Versions`. Беседа живёт на странице Chats, а не внутри записи.

```
Главный экран ──(снэкбар Open)──────────────┐
Меню ≡ ─ RECORDINGS ─┬─ History ─→ список ──┴─→ Recording ─┬─ Transcript  [плеер] ── ✎ → режим правки [Cancel | Save as new version]
                     │                                      │                ── 👥 speakers ▾ → лист Speakers (rename, merge, цвет, Show only)
                     │                                      ├─ Chats       (Insights · чаты с этой записью · Ask Gemma · Add to chat…)
                     │                                      ├─ Translate   [плеер]
                     │                                      └─ Versions    (Reprocess… · Re-recognize… · diff → [плеер])
                     │            шапка записи: ✦ Ask Gemma → новый чат;  ⋯ → Rename, Edit transcript, Reprocess…, Export…, Add to chat…, Delete
                     ├─ Chats ───→ список чатов ─→ Chat (источники 1..N · сообщения · поле ввода)
                     │                               ⋯ → Rename, Export (Markdown/HTML), Delete
                     └─ Queue (бейдж) ─→ Running · Scheduled · Waiting · Done today (цепочки Reprocess — одной карточкой)
     MODE ─→ … · Live speaker labels (выключатель, ревизия 3)
     Models ─→ Speech · Speakers (Nemotron 3 Diarization, Test this device) · Gemma
     Settings ─→ History and storage · Speakers and reprocessing
Листы: Filter, Re-recognize, Reprocess, Reprocess N (пакет), Speakers (+ Merge), Export, Rename, Add recordings, Sources, Add to chat
Диалог: Delete … ? [☐ Don't ask again]
```

Почему так:

- **Чат перестал быть вкладкой записи.** Решение 5: чат может читать несколько записей, записи добавляются и убираются свободно. Вкладка внутри одной записи подразумевает владение («чат этой записи») и не отвечает на вопрос, где открыть чат по трём встречам. Поэтому беседа — страница верхнего уровня, а у записи остаётся индекс её чатов.
- **Почему у записи всё-таки есть вкладка Chats, а не только кнопка.** Пользователь приходит к чату от записи («что мы обсуждали с этой записью?»). Вкладка показывает чаты, в которые входит запись, и две кнопки: `Ask Gemma` (новый чат с этой записью) и `Add to chat…` (в существующий). Это тот же сегментированный контрол, поэтому новая навигация не нужна. Вкладка стоит второй, где раньше был Chat: место привычное.
- **`✦ Ask Gemma` ещё и в шапке записи.** Самое частое действие доступно с любой вкладки одной кнопкой, без перехода на Chats.
- **Плеер не на всех вкладках.** На Transcript, Translate и в diff он нужен, потому что текст привязан ко времени. На Chats и в списке версий его нет. В режиме правки плеера нет: низ занимает панель `Cancel | Save as new version`.
- **Версии — отдельная вкладка.** Сравнение и выбор текущей версии — полноценная задача со списком и diff. Выбор версии на Transcript (`v2 · Parakeet FP32 ▾`) ведёт туда же. Правленые версии появляются там же с меткой `Edited from v2`.
- **Queue — пункт меню, а не вкладка History.** В очереди задания разных видов (перевод, сводки для чатов, заголовки), не только по записям. Бейдж с числом активных заданий виден в меню.
- **Master-detail от 840 px** (десктоп, планшет) у History и у Chats: список 360 px слева, карточка или беседа справа. Без выбора справа заглушка. Queue и Settings — одна колонка шириной до 720 px по центру. Ниже 840 px — отдельные экраны со стрелкой «назад» (системный Back на Android).

### 4.1 Меню и главный экран

- В меню новая секция **RECORDINGS** первой: `History · 24 recordings · 1.2 GB`, `Chats · 5 chats with Gemma, across recordings`, `Queue · 1 running · 4 waiting` с бейджем `5`. У `Settings` подпись «Recording history, audio retention», пункт открывает Settings › History.
- В секции **MODE** новый выключатель `Live speaker labels · Experimental · who is speaking` (§10.7). Пункт `Models` открывает страницу моделей с вкладками Speech · Speakers · Gemma (§10.8).
- **Снэкбар** — отдельная строка сетки главного экрана над панелью команд (`RowDefinitions="Auto,*,Auto,Auto"`), а не оверлей. Поэтому он не может закрыть Record, Mute или Copy. При показе транскрипт сжимается. Проверка следит, чтобы снэкбар не заходил на панель команд. Если `SaveInjectDictations = false` и сессия шла в режиме Inject, запись не сохраняется и снэкбара нет.

### 4.2 History (список)

| Элемент | Решение |
|---|---|
| Шапка | `← History`, подзаголовок `24 recordings · 1.2 GB` |
| Поиск | Поле 44 px на всю ширину + кнопка фильтра 48×48 с точкой, если фильтр активен. Ищет по заголовкам, текущему транскрипту, переводам, чатам с этой записью (§5.8) |
| Активные фильтры | Чипы 44 px с крестиком под поиском, только когда фильтр есть |
| Группы | Today / Yesterday / Earlier (по локальной дате) |
| Строка | Кнопка-строка ≥ 76 px: пилюля длительности · заголовок в 2 строки · `14:02 · 12:34 · Parakeet FP32 · EN` · чипы `→ RU`, `2 chats`, `v2`, `Inject`, `v2 queued`, `Title queued`, `No audio`. Справа отдельная кнопка `⋯` 44×44: Rename, Add to chat…, Re-recognize…, Export…, Select, Delete |
| Выбор | Долгий тап (или `Select` в `⋯`) → режим выбора: шапка `× 3 selected · 41 MB of audio [↻] [☑ all] [🗑]`. `↻` открывает лист пакетного перераспознавания (§9.5), `🗑` — диалог подтверждения (§4.7) |
| Хранилище | Карточка в конце списка: `Storage · 1.2 GB of 2 GB — audio kept 30 days, transcripts forever [Manage]` → Settings › History |

Заголовок строки — действующий заголовок записи: свой (`title`), иначе сгенерированный Gemma (`autoTitle`), иначе первые ~8 слов текущей версии (§5.7).

### 4.3 Recording (карточка)

**Шапка:** `←` · заголовок · `Mon 12 Oct, 14:02 · 12:34 · EN` · `✦` (Ask Gemma) · `⋯` (Rename, Edit transcript, Export…, Add to chat…, Delete).

**Transcript.** Панель: `v2 · Parakeet FP32 ▾` (переход на Versions) · `✎` Edit · Follow · Copy. Список сегментов: время 52 px + текст. Вся строка — цель касания ≥ 44 px. Текущий сегмент: акцентная полоса слева и фон `chip-on`, текущее слово подчёркнуто.
Перемотка к слову делается тапом по слову *внутри уже активного сегмента* (hit-test по `WordTiming`). Слово меньше 44 px, поэтому это вторичное действие, а основная цель касания — строка.

**Режим правки (Transcript → `✎`).** Решение 3. Шапка становится `× Edit transcript` + Undo, вкладки и плеер скрываются, каждый сегмент — поле ввода (время слева остаётся, изменённые сегменты помечены точкой). Под списком панель `2 segments changed · [Cancel] [Save as new version]`; на ширине < 360 px подпись скрывается, кнопки остаются. Строка над списком: «Editing v2 · Save creates v3 · edited; v2 stays as it is». Сохранение создаёт новую версию (§5.3), делает её текущей и возвращает на Transcript. `×`/Back с несохранёнными правками спрашивает «Discard changes?». Править можно любую версию, родителем становится она.

**Плеер.** Строка перемотки высотой 44 px (дорожка 4 px, засечки на границах сегментов). Под ней `Grid "minmax(0,1fr) auto auto auto minmax(0,1fr)"`: время · −10 · **Play 56×56** · +10 · скорость. Play математически по центру, так же как Record на главном экране. На низких окнах (< 480 px) плеер в одну строку: Play · ±10 · перемотка · время · скорость. Если аудио удалено политикой хранения, плеера нет, вместо него строка «Audio removed after 30 days · transcript only».

**Chats.** Две кнопки в ряд: `✦ Ask Gemma` (primary: новый чат с этой записью, сразу открывается страница чата) и `+ Add to chat…` (лист: `New chat with this recording` или список существующих чатов; те, где запись уже есть, недоступны с подписью «Already includes it»). Ниже `In 2 chats` и карточки чатов: заголовок, `3 recordings · 12 messages · 15:40` или `Only this recording · …`, последняя строка ответа. `⋯` у карточки: Open, Remove this recording from the chat. Подпись внизу объясняет, что чаты живут на странице Chats.

**Translate.** `→ Russian · from v2 ▾`: список существующих переводов (язык, версия, пометка `outdated`, если версия не текущая) + `New language…`. Кнопки `Show original` (двуязычный режим: оригинал мелко под переводом) и `Translate again`. Во время работы прогресс `Translating to German · 6 / 14 sentences [Cancel]`, переведённые сегменты появляются сразу. Перевод — задание очереди (§9.5); Cancel отменяет это задание. Сегменты перевода синхронизированы с плеером: у них тот же индекс и таймкод, что у сегментов версии.

**Versions.** Две кнопки (недоступны без аудио): `Reprocess…` — цепочка «перераспознать → спикеры → анализ» (§11) и `Re-recognize…` — прежний лист. Запущенное задание показывается карточкой с прогрессом, Cancel и подписью `job in Queue`. Карточки версий: `v2 · Parakeet TDT 0.6B v3 — full precision (FP32)`, чипы `● Current`, `Re-recognized` / `Recorded live` / `Edited from v2`, сетка `Language · VAD · Created · App · Words · vs v1 3.4% diff`, строка «Translation RU and 2 chats use this version», действия `Make current`, `Compare`, `⋯` (Delete version, Export).
**Diff:** выбор `v1 · Live ▾ → v2 · FP32 ▾`, статистика `7 words changed in 5 of 14 segments · 3.4% of words`, переключатель `Changes only`. Удалённые слова — красным зачёркнутым, вставленные — зелёным. Тап по сегменту проигрывает это место, так что разницу можно проверить на слух. От 840 px есть режим «side by side». Для `edited`-версии по умолчанию сравнение с родителем.

**Спикеры** (ревизия 3): разметка в Transcript, фильтр, лист Speakers, Insights во вкладке Chats, поля спикеров в карточках Versions — §10.8, §11.3.

**Лист Re-recognize** (снизу на телефоне, по центру на широком или низком окне): радио-список **установленных** моделей с пометкой, какой версией каждая уже использована, и WER из каталога · язык · VAD · `Make it current when done` · `When: Now — after the running job ▾` (те же условия, что у пакета, §9.5) · оценка `≈ 1 min 40 s on this device · runs from the Queue` (RTF модели из `ModelResearch.Speed` × длительность) · Cancel / Start.

### 4.4 Chats (страница верхнего уровня)

**Список.** `← Chats · 5 chats · Gemma 4 E2B on this device` и `+` (новый чат: сразу открывается лист Add recordings). Строка чата: иконка, заголовок, `3 recordings · 12 messages · 15:40` или имя единственной записи, последняя строка ответа; `⋯`: Rename, Export, Delete.

**Беседа.**

- Шапка: `←` · заголовок чата (`autoTitle` по первому вопросу или свой) · `3 recordings · 12 messages · on this device` · `⋯` (Rename, Export as Markdown/HTML, Delete).
- **Источники.** От 600 px ширины и 480 px высоты — чипы `Weekly sync v2`, `Supplier call v1`, … и пунктирный `+ Add recording`, под ними строка контекста «Reads a summary of each recording + the passages closest to your question» (для одной записи — «Reads transcript v2 (current) · 12:34»). Тап по чипу: Open recording, Use current version (если версия устарела), Remove from chat. **На узких и низких окнах** вместо чипов одна строка `▤ 3 recordings  Weekly sync v2, Supplier call v1, … ▾` + кнопка `+` 44×44: чипы на 320 px занимали две строки и не масштабировались бы на 8 записей. Строка открывает лист **Sources**: записи с версией и временем добавления (`v1 · added 15:31`), у каждой `×` (убрать) и тап — открыть; внизу `Add recording`. Подпись: «Removed recordings are no longer read. Earlier answers keep their citations».
- **Лист Add recordings:** поиск, список записей с флажками (уже входящие — недоступны, «Already in this chat»), подпись `+2 · first answer prepares summaries (≈ 2 min)`, `Add 2`. Добавляется текущая версия записи.
- **Сообщения.** Как раньше: пузырь пользователя справа, ответ слева с markdown-подмножеством. Чипы-ссылки показывают **запись и время**: `Weekly sync 03:20`, `Supplier call 12:05` (≥ 44 px высотой, ширина по тексту). Тап открывает запись на Transcript и перематывает. Подпись `From 3 recordings · summaries + 6 passages · 14.2 s` или `From Weekly sync v2 · 9.1 s`. Добавление и удаление источников видно в ленте строкой-событием по центру: «Supplier call and Interview Maria added · Gemma now reads 3 recordings».
- Пустой чат (создан через Ask Gemma): заголовок «Ask Gemma about this recording», 5 быстрых подсказок (кнопки 48 px, в 2 колонки от 560 px).
- Поле ввода: `✦` (подсказки) · текст · Send/Stop. При открытой клавиатуре источники скрываются, шапка ужимается до 48 px.

### 4.5 Queue

`← Queue · 1 running · 4 waiting · one heavy job at a time` и `⏸` (пауза всей очереди). Секции:

- **Running** — одна карточка: вид и цель (`Re-recognize · Weekly sync — budget and hiring`), модель и результат (`Nemotron 3.5 ASR INT4 · EN · creates v3`), прогресс, `42% · 5:16 of 12:34 · ≈ 1 min left`. Если идёт живая запись — жёлтая метка `Live recording in progress · slower, uses battery`. Pause, Cancel.
- **Scheduled** — пакеты и задания с условием, которое ещё не выполнено: `Batch · Re-recognize 3 recordings`, параметры, список записей, чипы условий `☾ Tonight 01:00–06:00`, `⚡ When charging`; `Edit schedule` (тот же лист, что при создании), `Run now`.
- **Waiting** — готовы к запуску, ждут своей очереди: `Translate → German · Interview with Maria` с `When idle`; `Prepare chat · Q4 planning…` (сводки для чата) на паузе с `Resume`; `Titles · 2 recordings` с `Low priority`.
- **Done today** — свёрнутая строка `3 completed today`; подпись «Progress is saved: a closed app resumes where it stopped».

Цепочка Reprocess (§11.4) — одна карточка с шагами ✓ / ● / ○ и `Cancel chain`; автопереобработка новых записей — карточка в Scheduled.

Порядок меняется ручкой перетаскивания слева (44×44); то же доступно без жестов через `⋯ → Move up / Move down` (доступность). `⋯` у задания: Run now, Edit schedule, Pause/Resume, Move up/down, Cancel; у пакета ещё Cancel batch.

### 4.6 Settings › History and storage

| Группа | Строка | Значения (по умолчанию) |
|---|---|---|
| Recording history | Save recordings to History | on/off (**on**) |
| | Save Inject text dictations | on/off (**on**) — решение 2 |
| | Ask before deleting | on/off (**on**) — включает обратно «Don't ask again» |
| | Titles by Gemma | on/off (**on**) — решение 4 |
| Audio storage | Keep audio | 7 · **30** · 90 · 180 · 365 days · Forever |
| | Storage limit for audio | 0.5 · 1 · **2** · 5 · 10 GB · No limit; под строкой полоса заполнения `Audio 1.1 GB of 2 GB · text and chats 96 MB · 41 GB free on device` |
| | Audio quality | **Standard** (Opus, ~11 MB/h) · High (WAV, 115 MB/h) |
| Speakers and reprocessing | Live speaker labels · Speakers after Stop · Auto-reprocess new recordings · Reprocess with · When | см. §11.5 (ревизия 3) |
| Background jobs | Run jobs during live recording | on/off (**on**; на Android подпись «slower and uses more battery») |
| — | Delete all history | красная строка, всегда с подтверждением (флажок «Don't ask again» здесь не показывается) |

Подпись к `Keep audio`: «Text, versions, translations and chats are kept forever» — автоматически удаляется только аудио.

### 4.7 Диалоги и листы

- **Подтверждение удаления** — центрированный диалог на любой ширине (не лист: для разрушительного «да/нет» это принятый на обеих платформах вид — Material `AlertDialog`, WinUI `ContentDialog`). `Delete 3 recordings?` / `Delete this recording?` / `Delete version v1?` / `Delete chat?` / `Delete audio, keep text?`; текст говорит, что удаляется и что остаётся («Chats keep their messages but lose these sources»); флажок `Don't ask again` с подписью «Turn it back on in Settings › History»; `Cancel` · красная `Delete`. Отмеченный флажок пишет `History.ConfirmDelete = false`. Без подтверждения удаление мягкое: снэкбар `Deleted · Undo` на 8 с (папка переносится в `<DataRoot>/trash/` и вычищается после закрытия снэкбара или при следующем запуске). «Delete all history» спрашивает всегда.
- **Rename** — лист (на телефоне снизу над клавиатурой, на широком окне диалог): поле с текущим заголовком и `×` очистки; подсказка `✦ Generated by Gemma from v2. Your own title is never replaced.` + `Regenerate` (ставит задание `autoTitle` с обычным приоритетом). Пустое поле при сохранении = сбросить свой заголовок и вернуться к сгенерированному. Тот же лист для чатов.
- **Export** (§6.6) — флажки форматов, выбор аудиоформата, итог `4 files · 2.4 MB`, кнопки `Save to…` и `Share`.

## 5. Модель данных

### 5.1 Расположение

`AppPaths` (VoiceType.Uno.Core) расширяется: `SessionsDir` (есть), **`ChatsDir` = `<DataRoot>/chats`**, **`JobsDir` = `<DataRoot>/jobs`**, плюс служебный `TrashDir` = `<DataRoot>/trash` для мягкого удаления.

```
<DataRoot>/
  sessions/                                AppPaths.SessionsDir (или AppSettings.SessionsPath, если задан)
    index.json                             кэш списка (пересобирается из manifest.json и chats/*/chat.json; источник истины — папки)
    2026-10-12_14-02-11_7f3a9c/            <локальное время старта>_<id 6 hex>
      manifest.json                        метаданные, заголовки, режим, версии, переводы (схема §5.2)
      audio.ogg                            Opus 16 kHz mono (§6); отсутствует, если аудио выключено/удалено
      audio.ogg.part                       только во время записи
      transcripts/
        v1.json                            сегменты + слова (§5.3)
        v2.json
        v3.json                            kind "edited", parent "v2"
        v4.json.part                       только пока идёт перераспознавание: закоммиченные сегменты = точка продолжения
      translations/
        t1.ru.v2.json                      перевод версии v2 на русский
      analysis/
        v2.chunks.json                     частичные сводки длинного транскрипта (§7.4) — общие для всех чатов
        v2.summary.json                    сводка записи целиком для чатов с несколькими записями (§7.4)
        v2.s2.insights.json                анализ версии со спикерами: сводка, задачи, время речи (§11.3)
      diarization/
        r1.json                            прогон диаризации: реплики + вероятности, от версии не зависит (§10.4)
      speakers/
        v2.r2.json                         слой: спикеры сегментов и слов версии v2 по прогону r2 (§10.4)
      .lock                                есть, пока запись пишется/обрабатывается
  chats/                                   AppPaths.ChatsDir — чаты больше не лежат в папке записи (решение 5)
    c-20261012-1531-b4e1/
      chat.json                            источники, сессия агента, отображаемая лента (§5.5)
  jobs/                                    AppPaths.JobsDir
    queue.json                             постоянная очередь заданий (§5.6)
  trash/                                   мягко удалённые папки до закрытия снэкбара Undo
```

Правила записи: каждый JSON пишется во временный файл и атомарно переименовывается (`File.Replace`/`File.Move(overwrite)`). У каждого хранилища один писатель, как `SettingsStore` для настроек: `SessionStore` (папки записей), **`ChatStore`** (папки чатов), **`JobStore`** (`queue.json`). Повреждённая папка не скрывает остальные: её пропускаем и пишем в лог, как это делает WinUI `SessionManager.LoadSessions`.

**Почему чаты вынесены из папки записи.** Чат ссылается на 1..N записей, источники меняются. Если хранить его внутри одной из записей, удаление этой записи удалило бы и общий чат, а «владелец» был бы случайным. Связь хранится только в одну сторону: `chat.json → sources[].recordingId`. Обратный индекс «запись → её чаты» (для вкладки Chats и чипа `2 chats`) — кэш в `index.json`, пересобирается из `chats/*/chat.json`.

### 5.2 `manifest.json`

```jsonc
{
  "schema": 2,                             // было 1: добавлены title/autoTitle, mode; удалены chats и jobs
  "id": "7f3a9c",
  "title": null,                           // заголовок пользователя (Rename); null — не задан
  "autoTitle": {                           // заголовок от Gemma (задание autoTitle); никогда не трогает title
    "text": "Weekly sync — budget and hiring",
    "sourceVersion": "v2", "model": "gemma-4-E2B-it.litertlm", "promptVersion": 1,
    "createdAt": "2026-10-12T14:19:40+03:00"
  },                                       // действующий заголовок = title ?? autoTitle.text ?? первые ~8 слов текущей версии
  "mode": { "injectText": false, "translation": true },   // режимы на старте сессии (решение 2)
  "startedAt": "2026-10-12T14:02:11+03:00",
  "endedAt":   "2026-10-12T14:15:02+03:00",
  "audio": {
    "file": "audio.ogg", "codec": "opus", "sampleRate": 16000, "channels": 1,
    "bitrate": 24000, "durationSec": 754.2, "bytes": 2270000,
    "source": "Mic",                       // AppSettings.AudioSource: Mic | Loopback | Mix
    "gaps": [ { "atSec": 312.4, "wallSec": 41.0, "reason": "muted" } ],  // mute: аудио нет, шкала не прерывается
    "removedAt": null                       // заполняется политикой хранения
  },
  "currentVersion": "v3",
  "versions": [
    {
      "id": "v1", "kind": "live",           // live | rerecognized | edited
      "file": "transcripts/v1.json", "createdAt": "2026-10-12T14:15:02+03:00",
      "model": {
        "catalogRepoId": "DimQ1/parakeet-tdt-0.6b-v3-onnx",   // ModelDescriptor.RepoId (null для не-каталожной папки)
        "folder": "parakeet-tdt-0.6b-v3-onnx-int4",           // ModelDescriptor.SubfolderName / имя папки
        "title": "Parakeet TDT 0.6B v3 — 4-bit (INT4)",       // ModelCatalog.DescribeFolder на момент записи
        "precision": "Int4", "variant": "INT4",               // ModelPrecision, ModelDescriptor.Variant
        "family": "ParakeetTdt"                               // тип распознавателя из RecognizerFactory
      },
      "language": { "requested": "auto", "detected": "en" },
      "settings": { "useVad": true, "repetitionPenalty": 1.1, "executionProvider": "cpu",
                    "chunkSec": 0.32, "stopHistoryEouSec": 0.8 },   // всё, что влияет на результат/WER
      "app": { "name": "VoiceType.Uno", "version": "1.0", "platform": "android-arm64" },
      "stats": { "words": 1839, "segments": 14, "rtf": null, "elapsedSec": null },
      "parent": null
    },
    { "id": "v2", "kind": "rerecognized", "file": "transcripts/v2.json", "...": "...", "parent": null,
      "stats": { "words": 1842, "segments": 14, "rtf": 0.19, "elapsedSec": 141.0 } },
    { "id": "v3", "kind": "edited", "file": "transcripts/v3.json", "createdAt": "2026-10-12T16:02:00+03:00",
      "parent": "v2",                       // версия, из которой сделана правка
      "model": null, "language": null, "settings": null,   // у правки нет своей модели: берутся из цепочки parent
      "edit": { "segmentsChanged": 2, "wordsChanged": 5, "source": "history" },  // history | live (правка во время записи)
      "app": { "name": "VoiceType.Uno", "version": "1.0", "platform": "android-arm64" },
      "stats": { "words": 1843, "segments": 14 } }
  ],
  "translations": [
    { "id": "t1", "file": "translations/t1.ru.v2.json", "sourceVersion": "v2",
      "targetLanguage": "ru", "engine": { "kind": "native", "model": "gemma-4-E2B-it.litertlm",
      "backend": "cpu", "greedy": true }, "systemPromptHash": "sha256:…",
      "createdAt": "…", "status": "complete" }          // complete | partial | failed
  ]
}
```

Что изменилось относительно ревизии 1:

- **`title` + `autoTitle`** вместо одного `title` (решение 4). `autoTitle` — объект, чтобы знать, из какой версии и каким промптом он сделан, и перегенерировать при смене текущей версии, только если пользователь нажал `Regenerate`.
- **`mode`** — режимы на старте сессии (решение 2). Решение «сохранять ли» принимается по `mode.injectText` на старте: если Inject включили посреди длинной встречи, встреча не теряется.
- **`versions[].parent` + `kind: "edited"`** — правки (решение 3). Поле `edited: bool` из ревизии 1 убрано: правка во время живой записи (`IsManualInputEnabled`) теперь тоже отдельная версия `edited` с `edit.source = "live"`, вместо `v1e`.
- **Удалены `chats` и `jobs`.** Чаты — в `<DataRoot>/chats` (§5.5), задания — в `queue.json` (§5.6). Манифест записи не меняется, когда меняется чат или очередь: меньше конфликтов писателей.

Правила версий:

- **Спикеры не меняют версию** (ревизия 3): диаризация — неизменяемый слой рядом с версией, `speakers/vN.rM.json`, по аналогии с переводом (§10.4).
- **Версия — неизменяемый снимок.** Новая версия не меняет старые. «Текущая» — только указатель `currentVersion`. Правка тоже создаёт новую версию, а не меняет файл.
- **Перевод и источник чата ссылаются на версию.** При смене текущей версии они не пересчитываются, а помечаются `outdated` в UI. Чат продолжает читать свою версию; в чипе источника есть `Use current version`.
- **Удалить версию нельзя**, если она текущая. Переводы этой версии удаляются вместе с ней (в диалоге это написано). Источники чатов, которые на неё указывают, переключаются на текущую версию, а в ленту чата добавляется событие «Weekly sync is now read from v3».
- **`settings`** — открытый словарь: туда пишем всё, что меняет результат распознавания. Это делает сравнение версий и WER-эксперименты воспроизводимыми.
- Мьют: аудио не пишется (так же, как его не видит распознаватель), поэтому шкала аудио и сегментов общая и непрерывная. `gaps` нужны только чтобы показать «пауза 41 с (mute)» и пересчитать время на часах.

### 5.3 `transcripts/vN.json`

```jsonc
{
  "schema": 1, "version": "v2", "text": "Okay, let's start. …",
  "segments": [
    { "i": 0, "start": 0.00, "end": 7.84, "text": "Okay, let's start. The meeting moves to ten next Monday, not nine.",
      "words": [ { "w": "Okay,", "s": 0.00, "e": 0.41 }, { "w": "let's", "s": 0.41, "e": 0.66 } ],
      "wordTiming": "estimated" }          // estimated | decoder (точные кадры TDT, §9.3) | interpolated (правка)
  ]
}
```

- `segments[].start/end` считаются по сэмплам (позиция батча в аудио). Начало сегмента — первый батч, давший непустой partial после предыдущего коммита. Конец — батч коммита минус `StopHistoryEouSeconds`.
- `words` — `SpeechLib.Models.WordTiming` в компактном виде, разложенные внутри сегмента той же логикой, что `Transcriber.AddWordTimings`.
- **Правленая версия** (`kind: "edited"`, решение 3). Правка идёт внутри сегментов: число сегментов, их `i`, `start`, `end` совпадают с родителем, поэтому перевод и ссылки чатов на время остаются осмысленными. Тайминги слов: слова родителя и правки выравниваются тем же `TranscriptDiff` (§9.4); у совпавших слов время переносится, вставленные и заменённые слова получают время линейной интерполяцией между соседними сохранёнными словами (по длине в символах), `wordTiming` сегмента становится `"interpolated"`, если в нём есть такие слова. Пустой сегмент после правки остаётся с пустым текстом (плеер пропускает его в подсветке). `TranscriptEdit.Apply(parent, editedTexts)` — чистая функция в Core с тестами.

### 5.4 Переводы — `translations/tN.<lang>.<ver>.json`

`{ "segments": [ { "i": 0, "text": "Итак, начнём. …" } ], "sentencesDone": 14, "sentencesTotal": 14 }`.
Индекс совпадает с сегментом исходной версии, время берётся оттуда. При отмене сохраняется `status: "partial"`, а повторный запуск продолжает с первого непереведённого сегмента.

### 5.5 Чаты — `<DataRoot>/chats/<chatId>/chat.json`

```jsonc
{
  "schema": 2,                              // было 1 (chats/c1.json в папке записи)
  "id": "c-20261012-1531-b4e1",
  "title": null, "autoTitle": { "text": "Q4 planning across meetings", "createdAt": "…" },
  "createdAt": "2026-10-12T15:20:03+03:00", "updatedAt": "2026-10-12T15:40:12+03:00",
  "sources": [                              // 1..N записей; порядок = порядок добавления
    { "recordingId": "7f3a9c", "versionId": "v2", "alias": "S1", "addedAt": "2026-10-12T15:20:03+03:00" },
    { "recordingId": "1c0d77", "versionId": "v1", "alias": "S2", "addedAt": "2026-10-12T15:31:40+03:00" },
    { "recordingId": "9a41e2", "versionId": "v1", "alias": "S3", "addedAt": "2026-10-12T15:31:40+03:00" }
  ],
  "nextAlias": 4,                           // псевдонимы не переиспользуются после удаления источника
  "agent": "TranscriptAnalyst", "promptVersion": 4, "model": "gemma-4-E2B-it.litertlm",
  "session": { /* JsonElement из AIAgent.SerializeSessionAsync — непрозрачен для нас */ },
  "display": [
    { "role": "user", "text": "Summary", "at": "…" },
    { "role": "assistant", "text": "…", "at": "…", "ms": 9100, "finish": "stop",      // stop | length | cancelled | error
      "citations": [ { "recordingId": "7f3a9c", "versionId": "v2", "segment": 6, "t": 200.0,
                       "label": "Weekly sync 03:20" } ] },                               // label — снимок для экспорта и удалённых записей
    { "role": "event", "kind": "sourcesAdded", "recordingIds": ["1c0d77", "9a41e2"], "at": "…" },  // sourcesAdded | sourceRemoved | sourceVersionChanged
    { "role": "user", "text": "Which risks could move the release date?", "at": "…" }
  ]
}
```

- **`sources` — единственный источник истины о связи чата и записей** (решение 5). Добавление и удаление источника меняет только `chat.json` (и строку-событие в `display`).
- **`alias`** (`S1`, `S2`…) — короткая устойчивая метка источника для модели (§7.4). После удаления источника его метка не переиспользуется, чтобы старые ответы со ссылкой `[S2 12:05]` не начали указывать на другую запись.
- **`citations`** — ссылка на запись, версию, сегмент и время плюс `label` на момент ответа. Если запись потом удалена, чип показывает `label` без перехода.
- **Удаление записи** (`SessionStore.Delete`) вызывает `ChatStore.RemoveRecording(recordingId)`: источник убирается из всех чатов, в ленту каждого добавляется событие. Чат с нулём источников остаётся (сообщения ценны), поле ввода в нём недоступно с подсказкой «Add a recording to continue».
- `display` хранится отдельно от `session` (см. §7.5): UI не зависит от внутреннего формата MAF.

### 5.6 Очередь — `<DataRoot>/jobs/queue.json`

```jsonc
{
  "schema": 1,
  "paused": false,                          // ⏸ всей очереди
  "batches": [
    { "id": "b-7d2c", "kind": "rerecognize", "title": "Re-recognize 3 recordings", "createdAt": "…",
      "params": { "model": { "folder": "parakeet-tdt-0.6b-v3-onnx-fp32", "catalogRepoId": "…" },
                  "language": "keep", "useVad": true, "makeCurrent": true },
      "conditions": { "idle": false, "charging": true,
                      "window": { "from": "01:00", "to": "06:00", "repeat": "daily" }, "notBefore": null },
      "jobIds": ["j-01", "j-02", "j-03"] }
  ],
  "jobs": [
    { "id": "j-01", "batchId": "b-7d2c", "kind": "rerecognize",   // rerecognize | translate | autoTitle | summarize
      "target": { "recordingId": "1c0d77" },                      // summarize/autoTitle чата: { "chatId": "…" } или { "recordingId", "versionId" }
      "params": null,                                             // null → берутся из batch.params; иначе свои
      "conditions": null,                                         // null → из batch; одиночное задание — свои
      "priority": 1,                                              // 0 interactive · 1 normal · 2 low
      "order": 3,                                                 // ручной порядок внутри приоритета (FIFO по умолчанию)
      "state": "scheduled",       // scheduled | waiting | running | paused | done | failed | cancelled
      "progress": { "fraction": 0.0,
                    "checkpoint": null },   // rerecognize: { "sample": 1843200, "partFile": "transcripts/v2.json.part" }
                                            // translate: { "segment": 6 } · summarize: { "chunk": 3 } · autoTitle: null
      "result": null,                       // { "versionId": "v2" } | { "translationId": "t2" } …
      "attempts": 0, "error": null,
      "createdAt": "…", "startedAt": null, "finishedAt": null }
  ]
}
```

- **Условия — конъюнкция** (`idle` И `charging` И окно И `notBefore`). Пресеты UI: `Now` (всё пусто), `WhenIdle` (`idle`), `WhenCharging` (`charging`), `AtTime` (окно или `notBefore`). Пример из макета — окно 01:00–06:00 + `charging`.
- `scheduled` — условия ещё не выполнены; `waiting` — выполнены, ждёт свободного слота. Переходы считает `JobScheduler.Next(queue, environment, now)` — чистая функция в Core (§9.5).
- Готовые задания хранятся 7 дней для строки «Done today», потом удаляются из файла.
- `queue.json` переписывается атомарно при каждом изменении состояния и на каждом checkpoint (не чаще раза в 2 с).
- Ревизия 3: виды `diarize` и `analyze`, цепочки (`chainId`, `dependsOn`), условия `requireCharging` / `requireIdle` и схема 2 — §11.4 (дельта к реализованному `JobModels.cs`).

### 5.7 Заголовки, режим, правка — сводка правил

| Что | Правило |
|---|---|
| Действующий заголовок записи | `title ?? autoTitle.text ?? первые ~8 слов текущей версии` |
| Генерация заголовка | После сохранения ставится задание `autoTitle` (priority 2, условие `idle`). Пропускается, если `title` уже задан или `History.AutoTitles = false`. Результат пишется только в `autoTitle` |
| Rename | Пишет `title`. Пустое значение — `title = null` (возврат к `autoTitle`) |
| Regenerate | Задание `autoTitle` с priority 1 по текущей версии |
| Inject-диктовки | `SaveInjectDictations = false` и `mode.injectText` на старте → сессия не сохраняется (аудио `.part` удаляется при Stop) |
| Правка | Всегда новая версия `edited` с `parent`, становится текущей; родитель не меняется |

### 5.8 Поиск

На 10³ записей хватает поиска без БД: `index.json` хранит для каждой записи нормализованный текст (casefold, без диакритики, `ё→е`) обоих заголовков, текущей версии и переводов, плюс метаданные для фильтров (`mode`, язык, модель, наличие аудио) и список `chatIds`. Тексты сообщений чатов индексируются по чату и показываются у каждой записи-источника как сниппет `(chat)`. Поиск — подстрочный по словам, в фоне, с отменой при наборе. Если записей станет больше или понадобится ранжирование, переходим на SQLite FTS5 (`Microsoft.Data.Sqlite`, работает на всех трёх головах). Формат папок при этом не меняется: индекс остаётся кэшем.

### 5.9 Настройки (`AppSettings`, пишет только `SettingsStore`)

| Ключ | Тип, по умолчанию | Решение |
|---|---|---|
| `History.SaveRecordings` | bool, `true` | 1 |
| `History.ConfirmDelete` | bool, `true`; `false` ставит флажок «Don't ask again» | 1 |
| `History.AudioRetentionDays` | int, `30`; варианты 7 / 30 / 90 / 180 / 365, `0` = Forever | 1 |
| `History.MaxAudioStorageMb` | int, `2048`; `0` = без лимита | 1 |
| `History.AudioQuality` | `Standard` (Opus) / `High` (WAV), `Standard` | — |
| `History.SaveInjectDictations` | bool, `true` | 2 |
| `History.AutoTitles` | bool, `true` | 4 |
| `Jobs.AllowDuringLiveRecording` | bool, `true` (на Android с предупреждением) | 6 |

Ревизия 3 добавляет ключи `Speakers.*` и `Reprocess.*` — §11.5.

Существующие `AppSettings.SessionsPath` и `SaveAudioMp3` остаются: первый задаёт `SessionsDir`; второй пока не используется, MP3 в экспорте зависит от наличия кодировщика (§6.6).

## 6. Аудио: формат, размер, хранение, приватность, экспорт

### 6.1 Формат

| Вариант | МБ/час (16 kHz mono) | Плюсы | Минусы |
|---|---|---|---|
| WAV PCM16 | **115** | без кодека, точная перемотка | 2.7 ГБ на сутки разговоров, на телефоне неприемлемо |
| FLAC | ~55–65 | без потерь | всё ещё много, нужна managed-библиотека |
| **Ogg Opus 48 kbps VBR** (см. замер ниже) | **~19.5** | речь звучит прозрачно, кодек managed ([Concentus](https://github.com/lostromb/concentus) + Concentus.Oggfile) **[не проверено: версии пакетов, скорость кодирования на Android]** | потери: влияние на WER повторного распознавания нужно измерить |
| Opus 16 kbps | ~7 | меньше | заметнее артефакты |
| MP3 (как в WinUI) | ~14–28 | — | кодировщик в репо только через NAudio/MediaFoundation, то есть только Windows |

**Замер этапа 0 (2026-10-12).** Parakeet TDT INT4, офлайн, CV17 250 en + 250 ru (47 мин), каждая фраза прошла через настоящий писатель и читатель (`tools/WerEval --codec`):

| Формат | WER | Δ к WAV | МБ/ч |
|---|---|---|---|
| WAV (round-trip) | 8.10 % | 0 | 115 |
| Opus 16 kbps | 9.14 % | +1.04 | 6.9 |
| Opus 24 kbps | 8.89 % | +0.79 | 10.1 |
| Opus 32 kbps | 8.63 % | +0.53 | 13.3 |
| **Opus 48 kbps** | **8.33 %** | **+0.23** | **19.5** |
| Opus 64 kbps | 8.31 % | +0.21 | 31.5 |

Режим `voip` хуже `audio`; разброс между прогонами ~±0.2 п.п.; CV17 уже прошёл MP3, поэтому это второе сжатие — для живого микрофона потери, вероятно, меньше (не проверено).

**Решение:** **Standard = Ogg Opus 48 kbps** (VBR, audio, 16 kHz mono, ~19.5 МБ/ч, 30 часов ≈ 585 МБ), **High = WAV PCM16** (115 МБ/ч, перераспознавание без потерь), **Compact = Opus 24 kbps** (~10 МБ/ч, +0.8 п.п.) для телефонов с малым запасом памяти. Кодек — Concentus 2.2.2 (чистый C#, trimmable), Ogg-мультиплексор свой (сброс страницы раз в секунду, восстановление `.part`) — `libraries/SpeechLib/src/SpeechLib.AudioStorage`.

### 6.2 Запись

- **Tee в `RecognitionService.ProcessLoop`:** каждый батч, который уходит распознавателю (после проверки mute), передаётся также в `ISessionAudioWriter.Append(float[])`. Это неблокирующий `Channel<float[]>`, кодирование идёт в отдельной задаче, как в `BufferedAudioRecorder`.
- Пишем сразу в `audio.ogg.part`, Ogg-страницы сбрасываются каждые ~1 с. При падении процесса теряется не больше секунды. Восстановление при старте: `.part` + `.lock` без живой сессии → финализируем Ogg и помечаем запись `recovered`.
- Счётчик сэмплов — общий для аудио и сегментов (§5.3).
- При `Stop` задача сохранения запускается из `OnFinalResult`/`Stopped` и **не блокирует UI**: дописать аудио, записать `v1.json` и `manifest.json`, обновить `index.json`, показать снэкбар, поставить задание `autoTitle` (§5.7). Если `SaveInjectDictations = false` и сессия началась с Inject, вместо этого `.part` удаляется и снэкбара нет. Чтобы не писать зря, решение известно на старте: при таком сочетании tee аудио не включается вовсе.
- На Android запись уже идёт под foreground-сервисом `microphone` (см. соседний дизайн), отдельных разрешений для файлов не нужно: всё лежит во внутреннем хранилище приложения.

### 6.3 Воспроизведение и синхронизация

`MediaPlayerElement` в Uno на голове Skia desktop поддерживается не во всех сборках **[не проверено для Uno 6.7.30]**, а точная позиция для подсветки слов нужна на всех головах. Поэтому дизайн опирается на свой минимальный плеер:

- `IAudioPlaybackSink` (платформенный, в `VoiceType.Uno`): Android — `AudioTrack`; Windows — NAudio `WaveOutEvent` (NAudio3 уже подключён); Linux — PulseAudio `pa_simple` playback (P/Invoke рядом с существующим захватом в `PulseAudioSourceFactory`).
- `SessionPlayer` (в Core, без UI): декодирует Opus → PCM, пишет в sink, держит позицию = записанные кадры − задержка sink. Seek — по таблице гранул Ogg (строится при открытии). Скорость 0.75–2× — простой time-stretch (WSOLA) **[не проверено: качество на 2×]** или только 1×/1.5×/2× через пересэмплирование с пометкой «pitch changes».
- Синхронизация: таймер UI 10 раз в секунду → бинарный поиск сегмента по `start` → `CurrentSegment`, `CurrentWord`. Чистая функция `PlaybackCursor.Locate(segments, t)` в Core, с тестами.

### 6.4 Хранение и очистка (настройки, раздел «History»)

| Настройка (ключ, §5.9) | Значения | По умолчанию |
|---|---|---|
| Save recordings to History (`History.SaveRecordings`) | on / off | **on** на телефоне и десктопе (решение 1) |
| Save Inject text dictations (`History.SaveInjectDictations`) | on / off | **on** (решение 2) |
| Keep audio (`History.AudioRetentionDays`) | 7 · 30 · 90 · 180 · 365 days · Forever (`0`) | **30 days** (решение 1: срок можно продлить) |
| Storage limit for audio (`History.MaxAudioStorageMb`) | 0.5 · 1 · 2 · 5 · 10 GB · No limit (`0`) | **2 GB** |
| Audio quality (`History.AudioQuality`) | Standard (Opus, ~11 MB/h) · High (WAV, 115 MB/h) | Standard |
| Ask before deleting (`History.ConfirmDelete`) | on / off | **on**; выключается флажком «Don't ask again» |
| Delete all history | кнопка, подтверждение всегда | — |

`RetentionPolicy.Plan(sessions, now, settings)` — чистая функция в Core: возвращает список «удалить аудио» (старше срока, если срок не Forever; затем самые старые сверх лимита, если лимит задан). Записи, открытые прямо сейчас, и записи с заданием в очереди (`scheduled`/`waiting`/`running`/`paused`) не трогает. **Тексты, версии, переводы и чаты не удаляются автоматически никогда** (решение 1). Запускается при старте приложения и после каждого сохранения. Полоса заполнения в настройках берёт данные из того же расчёта (`StorageUsage`: аудио, текст и чаты, свободно на устройстве). Аудио, удалённое политикой, не попадает в корзину: это не действие пользователя, а настроенное правило.

### 6.5 Приватность

- Всё хранится только локально, в `<DataRoot>/sessions`: на Android во внутреннем хранилище приложения, на десктопе в `%LOCALAPPDATA%` / `$XDG_DATA_HOME`. Сеть не используется: Gemma и ASR работают на устройстве.
- Android: исключить `sessions/`, `chats/`, `jobs/` и `trash/` из Auto Backup (`android:fullBackupContent` / `dataExtractionRules`), иначе записи разговоров и чаты по ним уходят в Google Drive. **Это обязательный пункт реализации.**
- Запись разговора других людей — ответственность пользователя. Первый запуск истории показывает однократное уведомление: «Recordings and transcripts are stored on this device. Others may need to consent to being recorded.»
- Export и Share — только явным действием, через системный диалог сохранения или Share sheet (§6.6).
- Шифрования на диске нет: полагаемся на шифрование устройства (Android FBE, BitLocker/LUKS). Это отмечено в справке.

### 6.6 Экспорт (решение 7)

Три формата в порядке, который выбрал пользователь. Все генераторы — чистые функции в Core (`MarkdownChatExporter`, `LrcWriter`, `SrtWriter`, `TimestampedTextWriter`) с тестами; платформенная часть только отдаёт файлы.

**(a) Чат → Markdown** (из чата `⋯ → Export`, или флажком в экспорте записи — все чаты, где она есть). Опционально HTML (тот же документ через минимальный конвертер markdown-подмножества, без внешних библиотек).

```markdown
# Q4 planning across meetings

Exported 2026-10-12 16:10 · Gemma 4 E2B (gemma-4-E2B-it.litertlm) on this device · VoiceType.Uno 1.0

## Sources
- **Weekly sync — budget and hiring** — 12 Oct 2026 14:02, 12:34 · transcript v2 (Parakeet TDT 0.6B v3 FP32, EN)
- **Звонок с поставщиком по срокам поставки** — 11 Oct 2026 17:40, 41:10 · transcript v1 (Parakeet TDT 0.6B v3 INT4, RU)

## Conversation
**You:** Which risks could move the release date?

**Gemma:** QA must get the build by Friday [Weekly sync 06:02]. The supplier delivery slips two weeks [Supplier call 12:05].

> *Supplier call and Interview Maria added (15:31).*
```

Ссылки — `[<короткий заголовок> mm:ss]` (для чата с одной записью — просто `[mm:ss]`), чтобы документ читался без приложения.

**(b) Аудио для обычных плееров:** три файла с одним базовым именем `2026-10-12 1402 Weekly sync — budget and hiring.*` (недопустимые для ФС символы заменяются, длина ≤ 120):

- аудио: по умолчанию хранимый Ogg Opus без перекодирования; выбор `WAV` (декодирование Opus → PCM16, есть всегда) для плееров без Opus; `MP3` — только там, где есть кодировщик (на Windows через MediaFoundation/NAudio, как в WinUI; на Android и Linux кодировщика MP3 в проекте нет, пункт скрыт) (открытый пункт: нужен ли MP3 вообще, если WAV играет везде);
- `.lrc` (синхронизированный текст для музыкальных плееров): строка `[mm:ss.xx]текст` на сегмент; сегменты длиннее 10 с делятся по словным таймкодам на строки ≤ 80 символов; заголовок `[ti:…]`, `[length:mm:ss]`. Расширенный LRC со словами (`<mm:ss.xx>`) — опция на потом **[не проверено: какие плееры его понимают]**;
- `.srt` (видеоплееры, VLC): нумерованные реплики `hh:mm:ss,mmm --> hh:mm:ss,mmm`, ≤ 2 строки по ≤ 42 символа, реплика ≤ 7 с — длинные сегменты делятся по словным таймкодам.

**(c) Текст с таймкодами (`.txt`):** строка `[hh:mm:ss] текст` на сегмент. Опция «Add the <язык> translation» добавляет перевод под каждой строкой (отступ 4 пробела и `→`). Шапка: заголовок, дата, длительность, версия и модель.

**Версия.** Экспортируется текущая версия; в листе экспорта из Versions (`⋯ → Export`) — выбранная.

**Отдача файлов.** Лист Export: флажки форматов, выбор аудиоформата, итог `4 files · 2.4 MB`, кнопки `Save to…` и `Share`. Android: `Share` → `Intent.ACTION_SEND_MULTIPLE` через `FileProvider` из временной папки `cache/export/`; `Save to…` → `ACTION_OPEN_DOCUMENT_TREE` (выбор папки, файлы через `DocumentFile`). Windows: `Save to…` → `FolderPicker` (несколько файлов) или `FileSavePicker` (один); `Share` → `DataTransferManager` **[не проверено в Uno 6.7.30 на WinUI-голове]**. Linux (Skia): только `Save to…` через диалог выбора папки Uno **[не проверено: есть ли `FolderPicker` на Skia desktop]**. Временные файлы экспорта удаляются при следующем запуске.

## 7. Чаты с Gemma: Microsoft Agent Framework + LiteRT-LM

### 7.1 Что подтверждено

**LiteRtLmSharp** ([репозиторий](https://github.com/OrihuelaConde/LiteRtLmSharp), ветка `master`, прочитано 2026-10-12):

| Факт | Источник |
|---|---|
| Пакеты 1.2.0 (последние на NuGet до 2026-10-12; тогда же вышла **1.3.0 с нативом v0.18.0** у апстрима, но **проект подключён к сборке форка [DimQ1/LiteRtLmSharp](https://github.com/DimQ1/LiteRtLmSharp) — 1.4.0**, потому что апстримный 1.3.0 не содержит улучшений форка — см. [research/litert/engine-improvements.md](../../research/litert/engine-improvements.md) §5a): `LiteRtLmSharp`, `LiteRtLmSharp.Extensions.AI`, `LiteRtLmSharp.SemanticKernel`, runtime `win-x64`, `linux-x64`, `android-arm64`, `osx-arm64`. Все `net10.0`. Версии managed- и runtime-пакетов должны совпадать | NuGet flatcontainer; [README.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/README.md) |
| `LiteRtChatClient : IChatClient` (namespace `LiteRtLmSharp.Extensions.AI`), конструктор `LiteRtChatClient(LiteRtEngine engine, string? modelId = null, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)`; `GetStreamingResponseAsync` поддерживается | [LiteRtChatClient.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp.Extensions.AI/LiteRtChatClient.cs), [docs/extensions-ai.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/extensions-ai.md) |
| **По умолчанию stateless**: на каждый вызов создаётся свежая conversation, вся история подаётся как `History` (prefill каждый ход). Opt-in stateful: `LiteRtStatefulConversationOptions` (`MaxLiveConversations`, по умолчанию 8, LRU), ответ несёт `ConversationId`, дальше отправляются только новые сообщения; системное сообщение в продолжении → `InvalidOperationException`, вытесненный id → `ArgumentException`; на мобильных советуют 1–2 живые беседы | [LiteRtStatefulConversationOptions.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp.Extensions.AI/LiteRtStatefulConversationOptions.cs), [docs/conversation-state.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/conversation-state.md) |
| `ChatOptions` → `Temperature/TopP/TopK/MaxOutputTokens/Seed`; `LiteRtChatOptions` добавляет `EnableThinking`, `NoRepeatNgramSize`, `SuppressTokens` | [LiteRtChatOptions.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp.Extensions.AI/LiteRtChatOptions.cs) |
| **Один движок на процесс** (по умолчанию; в сборке форка 1.4.0 `LiteRtEngine.AllowMultipleEngines` снимает ограничение, в апстримном 1.3.0 такого флага нет): второй `LiteRtEngine.Load` бросает исключение | [LiteRtEngine.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp/LiteRtEngine.cs) |
| Отмена: `CancellationToken` → нативный `CancelProcess()`. **Отменённую conversation нельзя использовать дальше**, её нужно выбросить; в 1.4.0 форка `Snapshot()` / `Resume(snapshot)` переносят завершённые ходы и частичный текст в новый разговор | [LiteRtConversation.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp/LiteRtConversation.cs) |
| `MaxNumTokens` — полный контекст (промпт + ответ, все ходы). Явное значение включает защиту от переполнения (`LiteRtContextOverflowException`). Не больше встроенного максимума модели, не меньше 1024. Неохраняемое переполнение портит нативную память. Сэмплы используют 4096 | [LiteRtEngineOptions.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp/LiteRtEngineOptions.cs), [samples/Maui/Services/EngineService.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/samples/Maui/Services/EngineService.cs) |
| Отмена: `CancellationToken` → нативный `CancelProcess()`. **Отменённую conversation нельзя использовать дальше**, её нужно выбросить | [LiteRtConversation.cs](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp/LiteRtConversation.cs) |
| История на стороне LiteRT из движка не читается. Её хранит приложение: `LiteRtMessage.Serialize/Deserialize`, `LiteRtConversationOptions.History/HistoryJson` | docs/conversation-state.md |
| Android: Moto G100, gemma-4-E2B — CPU 12.9 ток/с, RSS до 1.66 ГБ (сборка с нативом v0.18 — тогда ещё предрелизный 1.3.0) | [docs/android.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/android.md) |
| `LiteRtLmSharp.Extensions.AI` не AOT/trim-clean, зависит от `Microsoft.Extensions.AI.Abstractions` 10.7.0 | [LiteRtLmSharp.Extensions.AI.csproj](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LiteRtLmSharp.Extensions.AI/LiteRtLmSharp.Extensions.AI.csproj) |

**В репозитории LiteRtLmSharp нет сэмпла с Microsoft Agent Framework.** Сэмплы только `samples/Console`, `samples/Maui`, `samples/SemanticKernel`. MAF упоминается одной строкой в [docs/extensions-ai.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/extensions-ai.md):
`var agent = new ChatClientAgent(client, instructions: "…"); AgentRunResponse reply = await agent.RunAsync("…");`.
Имя `AgentRunResponse` для текущего MAF **устарело**, см. ниже.

**Microsoft Agent Framework 1.24.0** (`Microsoft.Agents.AI`, stable; зависит от `Microsoft.Extensions.AI` 10.10.0). API проверен по XML-документации из пакетов `microsoft.agents.ai` и `microsoft.agents.ai.abstractions` 1.24.0 с nuget.org. Кода с ним я не компилировал.

- `ChatClientAgent(IChatClient, ChatClientAgentOptions, ILoggerFactory?, IServiceProvider?)` и `ChatClientAgent(IChatClient, string instructions, string name, string description, IList<AITool>, …)`; расширение `IChatClient.AsAIAgent(…)`.
- `ChatClientAgentOptions`: `Name`, `Description`, `ChatOptions`, `ChatHistoryProvider`, `AIContextProviders`, `UseProvidedChatClientAsIs`, `AllowConcurrentInvocation`.
- **Сессия, а не thread:** `AgentSession`, `AIAgent.CreateSessionAsync(ct)`, `AIAgent.SerializeSessionAsync(session, JsonSerializerOptions?, ct)` → `JsonElement`, `AIAgent.DeserializeSessionAsync(JsonElement, …)`. Имена `AgentThread` / `GetNewThread()` / `thread.Serialize()` из ранних версий MAF в 1.24.0 **отсутствуют**.
- Запуск: `RunAsync(string, AgentSession, AgentRunOptions?, ct)` → `AgentResponse`; `RunStreamingAsync(string, AgentSession, …)` → поток `AgentResponseUpdate`.
- История: `InMemoryChatHistoryProvider(InMemoryChatHistoryProviderOptions)` с `ChatReducer` и `ReducerTriggerEvent` (`AfterMessageAdded` / `BeforeMessagesRetrieval`), `GetMessages(session)` / `SetMessages(session, …)`; база `ChatHistoryProvider`.
- Контекст: `AIContextProvider` (`InvokingContext` / `InvokedContext`) — добавляет инструкции и сообщения перед каждым запуском.

### 7.2 Пакеты и проекты

| Проект | Пакеты | Содержимое |
|---|---|---|
| `SpeechLib.LiteRT.Native` (есть) | `LiteRtLmSharp` 1.2.0 + runtimes (есть) | **Новое:** `GemmaEngineHost` — единственный владелец `LiteRtEngine`. `LiteRTLmNativeTranslator` получает конструктор с внешним хостом |
| **`SpeechLib.Agents`** (новый, `net10.0`) | `Microsoft.Agents.AI` 1.24.0, `LiteRtLmSharp.Extensions.AI` 1.2.0 | `TranscriptAgentFactory`, `ChatSourcesContextProvider`, `SamplerFallbackChatClient`, `MapReduceSummarizer`, `TitleGenerator` |
| `VoiceType.Uno.Core` | — (без LiteRT) | Чистая логика с тестами: `ChatStore`, `TranscriptChunker`, `ContextBudget` (в том числе деление бюджета между источниками), `Bm25Index` (по сегментам всех источников), `CitationParser` (`[S2 12:05]` / `[mm:ss]` → запись + сегмент), `ChatTranscriptPrompt` (инструкции и быстрые подсказки) |
| `VoiceType.Uno` | ссылка на `SpeechLib.Agents` | `ChatsViewModel`, `ChatViewModel`, связь с `GemmaEngineHost` и `JobScheduler` из DI |

Сначала проверить: `Microsoft.Agents.AI` тянет `Microsoft.Extensions.AI.Evaluation`, `Microsoft.Extensions.VectorData.Abstractions` и `Microsoft.ML.Tokenizers` (видно в nuspec 1.24.0). Нужно убедиться, что Android-сборка с trimming это переносит и размер APK растёт приемлемо. Если нет — запасной путь в §7.8.

### 7.3 Один движок на всё: `GemmaEngineHost`

```csharp
// SpeechLib.LiteRT.Native — эскиз, не компилировался
public sealed class GemmaEngineHost : IAsyncDisposable
{
    public Task<GemmaLease> AcquireAsync(GemmaWorkKind kind, CancellationToken ct); // LiveTranslation > ChatAnswer > Job (из очереди)
    public LiteRtEngine Engine { get; }        // загружается лениво, MaxNumTokens = ContextBudget.EngineTokens
    public int CountTokens(string text);      // engine.Tokenize(text).Length — для бюджета
}
```

- Одна conversation за раз (семафор), очередь с приоритетом. Три уровня: живой перевод на главном экране, ответ в открытом чате (интерактивный, в очередь заданий не ставится), задание очереди (перевод, сводки, заголовок — §9.5). Старший уровень вытесняет младший: задание отменяет свою conversation (отменённую использовать нельзя), сохраняет checkpoint и возвращается в `waiting`.
- Перевод и чат хотят разный `MaxNumTokens` (2048 и 4096+), а движок один. Значит, движок грузится с наибольшим нужным значением. Цена — память и prefill **[не проверено: насколько растёт RSS на 4096 против 2048]**.
- `LiteRtChatClient(host.Engine, "gemma-4-E2B-it", optionsTemplate)` создаётся поверх того же движка. Все вызовы клиента идут под lease.

### 7.4 Контекст: длинные транскрипты и несколько записей

Бюджет на 4096 токенов (на десктопе 8192, если модель позволяет **[не проверено: встроенный максимум KV в `gemma-4-E2B-it.litertlm`; LiteRtLmSharp предупреждает, что значение выше встроенного молча откатывается]**):

| Часть | Токены (4096) |
|---|---|
| Инструкции агента + список источников | ~300 |
| Контекст записей | ≤ 2250 |
| История беседы (после редукции) | ≤ 900 |
| Ответ (`MaxOutputTokens`) | 600 |

Оценка: речь ~150 слов/мин, примерно 200 токенов/мин для EN, для RU больше **[оценка, измерить `CountTokens`]**. Целиком в окно 4096 помещается ~10 минут одной записи.

**Одна запись.** Стратегия выбирается в `ContextBudget.Plan(sources)`:

1. **Direct** (транскрипт ≤ бюджета): весь текст с метками `[mm:ss]` на каждый сегмент.
2. **Digest + retrieval** (длиннее):
   - **Map:** транскрипт режется по границам сегментов на части ~1500 токенов (`TranscriptChunker`). Каждая часть сжимается Gemma в 120–150 токенов, факты с `[mm:ss]`. Сводки кэшируются **в папке записи** `analysis/<ver>.chunks.json` с ключом `(версия, hash текста, версия промпта)`, поэтому считаются один раз на версию и переиспользуются всеми чатами. Map — это задания `summarize` в очереди (§9.5) с приоритетом `interactive`, пока чат открыт. UI показывает `Preparing long recording · 3 / 6 parts`; можно уйти с экрана — задание продолжится.
   - **Answer:** в контекст идут все сводки (digest), а оставшийся бюджет занимают сегменты, ближайшие к вопросу по BM25 (`Bm25Index` в Core; модели эмбеддингов на устройстве нет).
   - Для подсказок «Summary», «Key points» используется только digest (reduce), для «Action items» — digest + retrieval по словам «will, by, need to, должен, до…».

**Несколько записей (решение 5).** Транскрипты нескольких записей почти никогда не помещаются целиком, поэтому стратегия всегда «сводка на каждую запись + общий поиск»:

1. **Сводка записи.** Для каждой записи-источника нужна сводка 150–250 токенов: `analysis/<ver>.summary.json` (reduce поверх chunk-сводок; для короткой записи — одна генерация по всему тексту). Кэш общий для всех чатов. Если сводки ещё нет, при добавлении источника ставится задание `summarize`; лист Add recordings честно пишет «first answer prepares summaries (≈ 2 min)».
2. **Деление бюджета.** Сводки получают до 40 % контекста: на источник `max(60, 900 / N)` токенов (при большом N сводка сокращается до первых предложений). Остаток — фрагменты из **общего BM25-индекса по сегментам всех источников**; в выдаче не больше половины фрагментов из одного источника, чтобы одна длинная запись не вытесняла остальные. Свыше 8 источников UI предупреждает «Gemma sees less of each recording» (порог **[подобрать замером]**).
3. **Формат контекста.** Каждый источник получает короткий устойчивый псевдоним из `chat.json` (`S1`, `S2`, …):
   ```
   Sources:
   [S1] "Weekly sync — budget and hiring", 12 Oct 2026, 12:34, transcript v2
   [S2] "Звонок с поставщиком по срокам поставки", 11 Oct 2026, 41:10, transcript v1
   Summaries:
   [S1] Next meeting moves to 10:00 Monday [S1 00:00]. Cloud costs 5% over budget [S1 03:20]. …
   [S2] Поставка сдвигается на две недели [S2 12:05]. …
   Passages:
   [S1 06:02] I think so, if QA gets the build by Friday.
   [S2 12:05] …
   ```
4. **Ссылки.** Инструкция просит ссылаться как `[S2 12:05]`. Короткая метка надёжнее для маленькой модели, чем длинный заголовок, и однозначна при похожих названиях. `CitationParser` превращает её в `{recordingId, versionId, segment, t}` и проверяет, что такой сегмент есть у этого источника; невалидные метки отбрасываются. UI и экспорт показывают `Weekly sync 12:05` (короткий заголовок + время). Для чата с одной записью допускается и голое `[mm:ss]`.
5. Всё это делает **`ChatSourcesContextProvider : AIContextProvider`**: перед каждым запуском он читает **текущий** `sources` из `ChatStore` и кладёт контекст в инструкции. Поэтому транскрипты **не попадают в историю беседы**, не дублируются в каждом ходе, а сериализованная сессия остаётся маленькой.

**Добавление и удаление источника посреди беседы.**

- `ChatStore.AddSources/RemoveSource` меняет `sources` и пишет событие в `display`. Сессия агента (`session`) не трогается: история сообщений та же, а контекст пересобирается на следующем ходе из нового списка — так работает `AIContextProvider`. В stateless-режиме этого достаточно.
- Удалённый источник перечисляется в инструкциях одной строкой «S2 was removed by the user — do not cite it». Старые ответы с `[S2 …]` остаются в истории; их чипы работают, пока запись существует.
- Событие в истории модели не дублируется: модели достаточно актуального списка источников в инструкциях.
- **Stateful-режим (этап 5)** держит контекст в первом сообщении живой LiteRT-беседы. Смена источников делает его неверным, поэтому `ConversationId` сбрасывается и следующий ход идёт stateless из `display` (один лишний prefill).
- Смена версии источника (`Use current version`, удаление версии) — то же, что удаление + добавление с тем же псевдонимом и событием `sourceVersionChanged`.

История беседы ограничивается `InMemoryChatHistoryProvider` с `ChatReducer`: последние N сообщений, остальные сжимаются в одну сводку. Редьюсеры `MessageCountingChatReducer` и `SummarizingChatReducer` есть в `Microsoft.Extensions.AI` 10.10.0 (по XML-документации пакета). `SummarizingChatReducer` сам вызывает модель, поэтому на телефоне начинаем с `MessageCountingChatReducer`.

### 7.5 Агент, сессия, хранение беседы

```csharp
// SpeechLib.Agents — эскиз по API MAF 1.24.0 (сигнатуры из XML-документации, код не компилировался)
var chatClient = new SamplerFallbackChatClient(               // DelegatingChatClient, §7.6
    new LiteRtChatClient(host.Engine, modelId: "gemma-4-E2B-it",
        optionsTemplate: new LiteRtConversationOptions { EnableThinking = false }));

AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "TranscriptAnalyst",
    ChatOptions = new ChatOptions { Instructions = ChatTranscriptPrompt.Instructions, MaxOutputTokens = 600 },
    ChatHistoryProvider = new InMemoryChatHistoryProvider(new() { ChatReducer = reducer }),
    AIContextProviders = [ new ChatSourcesContextProvider(chatStore, sessionStore, chatId, budget, summaries) ],
});

ChatDocument chat = chatStore.Load(chatId);                         // §5.5
AgentSession session = chat.Session is null
    ? await agent.CreateSessionAsync(ct)
    : await agent.DeserializeSessionAsync(chat.Session.Value, jsonOptions, ct);

await foreach (var update in agent.RunStreamingAsync(question, session, cancellationToken: ct))
    viewModel.Append(update.Text);                                  // UI-поток через IUiScheduler

JsonElement state = await agent.SerializeSessionAsync(session, jsonOptions, ct);
chatStore.SaveTurn(chatId, state, userMessage, assistantMessage);  // атомарно, после каждого ответа
```

Формат файла — §5.5 (`<DataRoot>/chats/<chatId>/chat.json`).

- **`display` хранится отдельно от `session`.** UI не зависит от внутреннего формата MAF, а при обновлении MAF, если старая сессия не десериализуется, беседа восстанавливается из `display` через `SetMessages`.
- `citations` — ссылки `{recordingId, versionId, segment, t, label}`, которые `CitationParser` нашёл в ответе. Метки, которых нет у источников, отбрасываются, поэтому выдуманные ссылки не показываются.
- **Инструкции** (EN, коротко — маленькая модель): отвечать только по источникам; если ответа нет — так и сказать; ссылаться `[S<n> mm:ss]`; при сравнении записей называть, из какой записи факт; язык ответа = язык вопроса; не выдумывать имена. Быстрые подсказки — фиксированные пользовательские сообщения из `ChatTranscriptPrompt`; для нескольких источников добавляется «Compare the recordings».
- **Заголовок чата** — `autoTitle` по первому вопросу и ответу (задание `autoTitle` с `target.chatId`, низкий приоритет), переименовывается так же, как запись.
- **Отмена:** Stop отменяет `CancellationToken`. LiteRT-беседа выбрасывается, частичный ответ сохраняется с `finish: "cancelled"`. Stateless-клиент на следующем ходе строит новую беседу из истории, так что отмена ничего не ломает.
- **Stateless vs stateful.** Этап 1 — stateless (проще, переживает перезапуск и смену источников). Цена — prefill всего контекста на каждом ходе: при ~3000 токенов на телефоне это **[не измерено]** порядка 10–30 с до первого токена. Оптимизация на этапе 5: stateful `LiteRtChatClient` (`MaxLiveConversations = 1`) и `ConversationId` в `AgentSession`. Тогда контекст нужно положить в первое сообщение, а не в инструкции, потому что системное сообщение в продолжении запрещено. При `ArgumentException` (беседа вытеснена, приложение перезапущено) или смене источников — откат на stateless из `display`.

### 7.6 Память и известные сбои на телефонах

- Gemma 4 E2B: пик RSS ~1.66 ГБ (по LiteRtLmSharp docs/android.md). Parakeet INT4 ~0.7 ГБ на диске, в памяти сопоставимо **[не измерено]**. Поэтому **одновременно работает одно тяжёлое задание** (§9.5): перераспознавание, перевод, сводки и заголовки идут через одну очередь. Ответ в чате при запущенном перераспознавании на устройстве с ≤ 6 ГБ RAM показывает «Waiting for re-recognition · Pause it» (кнопка ставит задание на паузу с checkpoint).
- Перед загрузкой Gemma проверяем свободную память (Android `ActivityManager.MemoryInfo.availMem`, на десктопе `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`). Если меньше ~2.5 ГБ — предупреждение «Close other apps» и возможность выгрузить модель ASR (`RecognitionService.UnloadModel`) на время чата, если запись не идёт.
- **Сбой сэмплера** (`Sampler type: 3 not implemented yet`) — это **не** нехватка RAM, а отсутствие
  стратегии: CPU-бэкенд LiteRT-LM v0.16/v0.18 реализует только `TopP`, отказ приходит на **первой
  отправке** разговора с явным `Greedy`/`TopK`, и такой разговор дальше непригоден (измерено 2026-10-12,
  см. [research/litert/engine-improvements.md](../../research/litert/engine-improvements.md) §3.4 и §5a).
  Поэтому в чате **явный сэмплер не передаём** (`Temperature`/`TopK`/`TopP` = null) и используется сэмплер
  файла модели (top-k 40 / top-p 0.95 / T 1.0). Дополнительно `SamplerFallbackChatClient` при
  `LiteRtException` до первого токена повторяет запрос без `ChatOptions`-сэмплера — та же логика, что в
  `LiteRTLmNativeTranslator.DisableSamplerForRetry`. В сборке форка 1.4.0 такой отказ приходит как
  `LiteRtSamplerNotSupportedException` с названием стратегии, а `LiteRtSamplerParams.NearGreedy`
  (`TopP=0.5`, `T=0.1`) — поддерживаемый профиль, близкий к жадному (качество и задержка ещё не замерены).
- Thinking выключен (`EnableThinking = false`): он делит бюджет `MaxOutputTokens` с ответом и может дать пустой ответ с `finish: length`.
- Фоновая работа на Android — §9.5 (foreground-сервис очереди).

### 7.7 Чем это лучше прямых вызовов `LiteRtConversation`

MAF даёт готовые сессию с сериализацией, провайдеры истории и контекста и стриминг поверх `IChatClient`. Ровно это требуется: «чат + своя история + контекст из меняющегося набора записей». Особенно полезен `AIContextProvider`: источники меняются между ходами, а история и сессия — нет. Кроме того, тот же агент без изменений работает с другим `IChatClient`, например с HTTP-бэкендом LiteRT-LM (`SpeechLib.LiteRT`), когда нативная модель не скачана — так же, как переводчик сейчас откатывается на HTTP.

### 7.8 Запасной путь

Если MAF не проходит по размеру или trimming на Android, интерфейсы остаются: `ITranscriptChat` (в `SpeechLib.Agents`) с реализацией на чистом `IChatClient` (`Microsoft.Extensions.AI`): своя история (`display` → `ChatMessage`), тот же `ChatSourcesContextProvider`-эквивалент как функция сборки системного сообщения. Формат `chat.json` совместим, поле `session` просто отсутствует.

## 8. Перевод сохранённого транскрипта

- Отдельный `ITextTranslator` поверх `GemmaEngineHost` (тот же `LiteRTLmNativeTranslator`, тот же промпт `TranslationPrompt`, greedy + no-repeat n-gram 8, повтор без сэмплера). При отсутствии нативной модели — HTTP `LiteRTLmTranslator`, как в `TranslationService.CreateTranslatorAsync`.
- **Не через `LiveTranslationSession`:** он рассчитан на ревизуемый поток (черновики, стабильный префикс), а здесь текст неизменен и уже разбит на сегменты. `SavedTranscriptTranslator` в `SpeechLib` (без UI, с тестами на фейковом переводчике):
  - сегменты режутся на предложения тем же `SentenceSplitter`;
  - каждое предложение — `TranslationRequest` с контекстом предыдущей пары (`PreviousSource/PreviousTranslation`), как в живом переводе;
  - вывод чистится `TranslationOutputCleaner` / `TranslationGuards`;
  - результат собирается обратно по сегментам, поэтому индексы и время совпадают с версией;
  - сохранение после каждого сегмента (`status: partial`) — отмена и падение не теряют работу, повторный запуск продолжает.
- Перевод — задание `translate` очереди (§9.5) с checkpoint по сегменту. Живой перевод старше: если пользователь начал запись с живым переводом, задание отдаёт движок Gemma и возвращается в `waiting` (§7.3).
- «Translate again» на тот же язык и версию заменяет перевод (с подтверждением). На другой язык или версию — добавляет новый.

## 9. Повторное распознавание и очередь заданий

### 9.1 Конвейер

```
audio.ogg ──decode Opus──► float PCM 16 kHz ──resample to recognizer.SampleRate (StreamingResampler)──►
   ChunkSamples-батчи ──► тот же IRecognitionPipeline, что в RecognitionService ──► сегменты + слова ──► vN.json
```

- **`OfflineRecognitionJob`** (в `VoiceType.Uno`, логика нарезки и сборки сегментов — в Core): создаёт **свой** распознаватель через `RecognizerFactory.Create(new RecognizerFactoryOptions { ModelPath, Language, UseVad, … })` и не трогает загруженную модель главного экрана. Если выбрана та же папка и запись не идёт, можно переиспользовать её, чтобы не держать две копии в памяти.
- Конвейер (Utterance/Plain) выносится из `RecognitionService` в общий `RecognitionPipeline` (сейчас это private-классы). Тогда живое и офлайн-распознавание дают одинаковые сегменты, а `SessionTimeline` (сэмплы → сегменты) тестируется в Core на фейковом распознавателе.
- Подача батчей без ожидания реального времени. Прогресс = обработанные сэмплы / все сэмплы. RTF пишется в `stats.rtf`.
- Отмена через `CancellationToken` между батчами. При отмене версия не создаётся, временный `vN.json.part` удаляется.
- **Продолжение после остановки (решение 6: «прогресс сохраняется, убитое приложение продолжает»).** Checkpoint — граница последней закоммиченной фразы: `vN.json.part` хранит закоммиченные сегменты, `queue.json` — сэмпл, на котором закончился последний `Final`. После паузы или перезапуска создаётся свежий распознаватель и подача начинается с этого сэмпла минус 2 с контекста (тот же приём, что onset-контекст и «свежее состояние декодера после паузы» в `ParakeetTdtRecognizer.Step`). Для `PlainPipeline` граница — конец предложения или пауза ≥ 1 с. Результат может немного отличаться от прогона без остановки на стыке **[не проверено: замерить diff «с остановкой» против «без» на `Test-Audio/cv17` и длинной записи]**. Checkpoint пишется не чаще раза в 2 с.
- Запуск, приоритет, фон, параллельность с живой записью — общая очередь, §9.5.

### 9.2 Что записывается в версию

`model` (repoId каталога, папка, заголовок, точность, вариант, семейство), `language.requested/detected`, `settings` (VAD, repetition penalty, EP, chunk, EOU), `app` (имя, `ApplicationDisplayVersion`, платформа/RID), `createdAt`, `stats` (слова, сегменты, RTF, время). Этого хватает, чтобы воспроизвести результат или объяснить разницу в WER. У `edited`-версии модели нет: `parent` указывает, откуда она, а `edit` — сколько изменено.

### 9.3 Точные таймкоды слов (этап 5)

Parakeet TDT предсказывает длительность каждого токена (кадр энкодера 80 мс, см. `docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md`). Новый необязательный интерфейс `ITimedUtteranceRecognizer` (default-метод, как `LastTokenCount => 0`) отдаёт токены с кадрами, и `wordTiming` становится `"decoder"`. Для остальных моделей остаётся оценка.

### 9.4 Diff версий

`TranscriptDiff.Compute(a, b)` в Core: выравнивание по словам (Myers по нормализованным токенам: casefold, пунктуация отдельно) → вставки, удаления, замены в привязке к сегментам версии B. Статистика «words changed / % of words» — это (S+D+I)/N относительно версии A, по сути WER одной версии против другой. Подпись в UI «diff», а не «WER», потому что эталона нет. Тот же алгоритм переносит тайминги слов в правленую версию (§5.3). Тесты: одинаковые версии, сдвиг сегментации, пунктуация, кириллица.

### 9.5 Очередь заданий и планировщик (решение 6)

**Что идёт через очередь.** Всё долгое и тяжёлое: `rerecognize` (ASR), `translate`, `summarize` (сводки для чатов), `autoTitle` (Gemma); с ревизии 3 ещё `diarize` и `analyze` и цепочки Reprocess из них (§11.4). Не идут: живое распознавание и живой перевод (это не задания, у них высший приоритет), ответ в открытом чате (интерактивный; ждёт освобождения движка, см. §7.3).

**Компоненты.**

| Компонент | Где | Что делает |
|---|---|---|
| `JobStore` | Core | Единственный писатель `queue.json` (§5.6), атомарная запись |
| `JobScheduler.Next(queue, env, now)` | Core, чистая функция | Выбирает следующее задание и считает переходы `scheduled ↔ waiting`; `env` = `{ liveRecording, liveTranslation, charging, onBattery, memoryClass }` |
| `JobRunner` | `VoiceType.Uno` | Один рабочий цикл: берёт задание у планировщика, запускает исполнитель (`OfflineRecognitionJob`, `SavedTranscriptTranslator`, `MapReduceSummarizer`, `TitleGenerator`), пишет прогресс и checkpoint, реагирует на pause/cancel/вытеснение |
| `IDeviceConditions` | `VoiceType.Uno`, по платформам | Зарядка, простой, события их смены (Android `BatteryManager`/`ACTION_POWER_CONNECTED`; Windows `GetSystemPowerStatus`; Linux `/sys/class/power_supply/*/online`). Устройство без батареи считается «на зарядке» **[не проверено на всех трёх головах]** |
| `JobsForegroundService` | Android | Держит процесс, пока есть `running`-задание (см. ниже) |

**Правила планировщика.**

1. **Одно тяжёлое задание за раз.** И ASR, и Gemma — «тяжёлые»: на телефоне они не помещаются в память вместе (§7.6). Слот один на всё приложение.
2. **Порядок:** приоритет (`0 interactive` — сводки для открытого чата; `1 normal` — перераспознавание, перевод, Regenerate; `2 low` — автозаголовки), внутри приоритета — ручной `order`, по умолчанию FIFO. Перетаскивание в Queue меняет `order`.
3. **Условия** (`conditions`, конъюнкция): `idle` — нет живой записи; `charging` — устройство на зарядке; `window` — локальное время внутри `[from, to)` (окно через полночь поддерживается; `repeat: daily` — задание, не успевшее в окно, ждёт следующего); `notBefore` — не раньше момента. Пресеты в UI: Now, When idle, When charging, In a time window (+ флажок Only while charging).
4. **Выход из условия во время работы** (сняли с зарядки, закончилось окно, началась запись при `idle`): задание сохраняет checkpoint и возвращается в `scheduled`. Это то же действие, что ручная пауза, только автоматическое.
5. **Параллельно с живой записью** (решение 6): разрешено. `Jobs.AllowDuringLiveRecording` по умолчанию включено; на десктопе без оговорок, на Android с меткой в Queue «Live recording in progress · slower, uses battery» и подписью в настройках. Gemma-задания при этом всё равно уступают движок живому переводу (§7.3). Если памяти не хватает (`memoryClass` ниже порога **[подобрать]**), ASR-задание на время записи ставится на паузу с той же меткой — запись важнее.
6. **Пакет** (лист «Re-recognize N recordings» из выбора в History): одна запись `batches[]` с общими параметрами и условиями + N заданий с `batchId`. Задания пакета идут по одному в порядке выбора. Карточка пакета в Queue показывает записи и общий прогресс; `Cancel batch` отменяет невыполненные, готовые версии остаются. «Keep each recording's language» = язык берётся из текущей версии каждой записи.
7. **Пауза/продолжение/отмена.** Пауза задания или всей очереди (`paused: true`) — checkpoint и `paused`; продолжение — с checkpoint. Отмена удаляет частичные файлы (`vN.json.part`, недоделанный перевод остаётся `partial` — его можно продолжить из Translate).
8. **Сбой.** Исключение исполнителя → `failed` с текстом ошибки, `attempts++`; автоматический повтор один раз через 1 мин для ошибок памяти/движка, иначе — только кнопкой Retry.

**Персистентность и продолжение.** `queue.json` пишется на каждом переходе и checkpoint. При старте приложения все `running` без живого процесса становятся `waiting` и продолжают с checkpoint: rerecognize — с сэмпла последней фразы (§9.1), translate — с сегмента, summarize — с части, autoTitle — заново (одна короткая генерация).

**Android: работа в фоне.**

- Пока есть `running`-задание, `JobsForegroundService` держит уведомление `Re-recognizing · Weekly sync · 42%` с кнопками Pause и Cancel. Тип сервиса для локальных вычислений: кандидаты `dataSync` и `mediaProcessing` (Android 15+), у обоих есть дневной лимит времени работы в фоне **[не проверено: точные лимиты и какой тип пройдёт ревью Play для ASR/LLM-вычислений; `shortService` не подходит — до 3 мин]**. Если запись уже идёт под `microphone`-сервисом, задание работает в том же процессе и отдельный сервис не нужен.
- **Условия, когда приложение закрыто** (окно 01:00–06:00, «на зарядке»): регистрируется `WorkManager` (`Xamarin.AndroidX.Work.Runtime`) с ограничением `RequiresCharging` и `InitialDelay` до начала окна; воркер поднимает процесс, запускает `JobRunner` и переходит в foreground через `setForeground` **[не проверено: биндинг WorkManager в net10.0-android и запуск foreground из воркера в Android 14+]**. Без этого задания выполняются, только пока приложение открыто — UI должен это честно говорить («Runs when VoiceType is open» в подписи условия).
- Десктоп (Windows, Linux): задания выполняются, пока приложение открыто. Окно 01:00–06:00 срабатывает, если приложение работает в это время; иначе задание ждёт следующего окна. Подпись условия на десктопе: «while VoiceType is running».

## 10. Спикеры: Nemotron 3 Diarization на устройстве

Ревизия 3 (решения 9–11, §16): офлайн-диаризация моделью [nvidia/Nemotron-3-Diarization](https://huggingface.co/nvidia/Nemotron-3-Diarization), сконвертированной в ONNX для нативного запуска на всех трёх головах; фоновая переобработка (новая версия → спикеры → анализ, §11); живые метки спикеров — только если тесты производительности это позволят, и с выключателем.

### 10.1 Что известно о модели

Источники: карточка модели — [README.md](https://huggingface.co/nvidia/Nemotron-3-Diarization/blob/main/README.md), [ASR_INTEGRATION_GUIDE.md](https://huggingface.co/nvidia/Nemotron-3-Diarization/blob/main/ASR_INTEGRATION_GUIDE.md), [diarization_evaluation.md](https://huggingface.co/nvidia/Nemotron-3-Diarization/blob/main/diarization_evaluation.md) и список файлов репозитория (прочитано 2026-10-12).

| Факт | Где в карточке | Что из этого следует |
|---|---|---|
| Streaming Sortformer: «кто когда говорил», до **8** спикеров; один чекпойнт для потокового и офлайн-режима | Description | Одна модель на оба режима: фон после записи (офлайн-буфер) и живые метки (потоковый) |
| 100M параметров; 31-слойный Transformer-энкодер с RoPE; вход — mel 10 мс, стэкинг ×8 → кадр энкодера 80 мс; Conv1D поднимает выход до 10 мс | Model Architecture | Выход `[T, 8]` вероятностей активности, шаг 10 мс (настраивается кратно 10 мс) |
| Вход 16 kHz mono; длительность при чанковом выводе не ограничена | Input | Тот же PCM 16 kHz, что лежит в `audio.ogg` (§6.1), ресемплинг не нужен |
| Каналы упорядочены по времени первого появления спикера; между чанками — Arrival-Order Speaker Cache (AOSC) и FIFO | Description, Output | Номера спикеров устойчивы внутри одного прогона, между прогонами — нет (§10.6) |
| Пресеты, в кадрах 80 мс (`spkcache / fifo / chunk / right / update`): offline 30.4 с — `264 / 40 / 340 / 40 / 300`; 1.04 с — `264 / 264 / 9 / 4 / 222`; 0.64 с — `264 / 264 / 6 / 2 / 222`; 0.32 с — `264 / 264 / 3 / 1 / 222`. Задержка = (chunk + right) × 80 мс, без времени счёта | Setting up Streaming Configuration | Пресеты переносятся в C# как есть (§10.3) |
| DER с перекрытиями: DIHARD III full — 12.73 (30.4 с) / 13.18 (1.04 с) / 13.28 (0.64 с) / 13.55 (0.32 с); CALLHOME part 2 — 9.10 / 10.29 / 10.66 / 11.32; есть AMI, AliMeeting, NOTSOFAR1 | Performance Evaluation Results | Живой режим хуже офлайна на 0.5–2 п.п. DER, поэтому после живых меток имеет смысл офлайн-проход (§10.7) |
| RTFx 1340 (офлайн) / 38 (1.04 с) / 25 (0.64 с) / 12.5 (0.32 с): batch 1, eager, BF16, **NVIDIA RTX PRO 5000** | Inference Speed Evaluation | Цифр для CPU и телефона нет. Соотношение подтверждает: потоковые пресеты в десятки раз дороже офлайна на секунду аудио (оценка в §10.7) |
| Файлы: `Nemotron-3-Diarization.nemo` 199 MB, `model.safetensors` 397 MB, `Nemotron-3-Diarization.q8_0.gguf` 107 MB | дерево файлов (HF API) | Порядок размеров ONNX: FP32 ≈ 400 MB, INT8 ≈ 100–110 MB **[оценка до спайка]** |
| Рантаймы: NeMo, 🤗 Transformers (`AutoModelForAudioFrameClassification`, `speaker_cache` между чанками), NeMo-Speech.cpp на GGUF (`nemo-speech transcribe --diarize`) | How to use | ONNX-экспорта у модели нет: его делает наш спайк (§10.2) |
| Лицензия OpenMDW-1.1, «ready for commercial or non-commercial use» | Description, License | Можно отдавать через каталог моделей со скачиванием по запросу |
| Обучение: английский, китайский, мультиязычные наборы (VoxConverse, DIHARD, CALLHOME, YODAS-v2, David AI — 21 язык), хинди и другие. Русский отдельно не указан | Training Datasets | Качество на русском **[не проверено]**, замер в спайке |
| Метки спикеров локальны для сессии и не являются биометрией; имена приложение сопоставляет само. Обычный ASR после диаризации ошибается на перекрытиях; для перекрытий гайд предлагает multitalker Parakeet (только EN) или Nemotron 3.5 ASR с `masked_asr=true` | ASR_INTEGRATION_GUIDE.md | Наш путь — один поток ASR и разметка слов по времени (§10.5). Перекрытия помечаем, но не разделяем. Speaker-masked ASR в эту итерацию не входит |
| В отчёте DER нужно указывать протокол: collar, учёт перекрытий, пресет, точность | diarization_evaluation.md | По этому протоколу идут замеры спайка (§10.2) |

### 10.2 Конвертация в ONNX (спайк, этап 6)

**Почему ONNX, а не GGUF/NeMo-Speech.cpp.** ONNX Runtime уже есть в решении: `Microsoft.ML.OnnxRuntime` приходит транзитивно с `Microsoft.ML.OnnxRuntimeGenAI` в `SpeechLib.Providers` **[проверить, что `InferenceSession` доступен новому проекту без лишних пакетов]**. ORT работает на android-arm64, win-x64 и linux-x64, а `ExecutionProviderSelector` уже есть. NeMo-Speech.cpp потребовал бы своей нативной сборки под три RID и P/Invoke; это запасной путь.

Конвертацию делает отдельная работа в `tools/converters/NemotronDiarization`; этот дизайн её не трогает. От неё ожидаются:

| Артефакт | Содержимое |
|---|---|
| `preencode.onnx` | mel-чанк → эмбеддинги кадров 80 мс (subsampling ×8). Считается один раз на кадр |
| `encoder.onnx` | `[spkcache ⧺ fifo ⧺ chunk ⧺ right]` эмбеддингов → вероятности `[T, 8]` (31 слой, голова, Conv1D-апсемплинг). Ось T динамическая |
| Обновление кэша | AOSC/FIFO между чанками — не в графе, а в C#. Чистый порт уже есть: `tools/converters/SortformerStreaming/Services/SortformerStreamingUpdater.cs`, но он сделан для Sortformer v2.1; совпадение с Nemotron 3 **[проверить]** |
| mel-признаки | Параметры из `processor_config.json`. Либо фичеризатор на C#, либо отдельный `features.onnx` — решает спайк |
| `config.json` | Пресеты, размер эмбеддинга, кадр, порог по умолчанию, ревизия апстрима (commit HF) |
| Варианты | **FP32** (эталон, ≈ 400 MB) и **INT8** (динамическая квантизация, ≈ 100–110 MB, по умолчанию на телефонах); FP16 для десктопа — по желанию |

Папка модели устроена так же, как у ASR-моделей каталога: `nemotron-3-diarization-onnx-int8/{preencode.onnx, encoder.onnx, config.json}`.

**Критерии выхода спайка.** DER считается по протоколу diarization_evaluation.md: collar 0, перекрытия учитываются.

1. **Паритет:** ONNX FP32 против PyTorch на 10 записях — max |Δp| ≤ 1e-3, то же число спикеров, DER между гипотезами ≤ 0.5 %.
2. **Качество:** DER FP32 и INT8 на AMI test SDM (16 записей, forced-alignment RTTM, как в карточке) для offline и 1.04 с. INT8 хуже FP32 не больше чем на 1 п.п.
3. **Русский:** 3–5 своих записей встреч на русском с ручной разметкой (RTTM) — DER и точность подсчёта спикеров (SCA). Порога нет: по результату решаем, нужна ли в UI пометка «quality on Russian not verified».
4. **Скорость и память:** RTF и пиковый RSS на десктопном CPU и на телефоне класса Moto G100 для offline, 1.04 с, 0.64 с и двух своих пресетов — **lean-live** (`fifo 40`, chunk 9, right 4) и **delayed-live** (chunk 125 ≈ 10 с, right 13, fifo 40). Отдельный замер — вместе с потоковым Parakeet INT4 на том же устройстве (бюджет из §10.7).
5. **Размеры** вариантов попадают в каталог и в Models › Speakers вместо «≈ … *».

### 10.3 Рантайм: `SpeechLib.Diarization`

Новый проект `libraries/SpeechLib/src/SpeechLib.Diarization` (net10.0, без UI), на него ссылается `VoiceType.Uno`.

```csharp
// эскиз, не компилировался
public enum DiarizationPreset { Offline, Low1040, VeryLow640, Ultra320, LeanLive, DelayedLive }
public sealed record DiarizationOptions(string ModelPath, DiarizationPreset Preset, string? ExecutionProvider = "cpu",
    float Threshold = 0.5f, double MinSpeechSec = 0.25, double MinSilenceSec = 0.2, int MaxSpeakers = 8);
public sealed record SpeakerTurn(double Start, double End, int Speaker);       // Speaker = канал в порядке появления
public sealed record DiarizationResult(IReadOnlyList<SpeakerTurn> Turns, SpeakerProbabilities Probs, int SpeakersFound);

public interface IDiarizer : IDisposable                                     // офлайн: задание очереди
{
    Task<DiarizationResult> DiarizeAsync(IAudioReader audio, IProgress<double>? progress, CancellationToken ct);
}
public interface IStreamingDiarizer : IDisposable                            // живые метки
{
    DiarizationUpdate Accept(ReadOnlySpan<float> samples16k);                // новые финальные кадры, без look-ahead
    DiarizationUpdate Finish();
}
```

- `SortformerOnnxDiarizer : IDiarizer, IStreamingDiarizer` — две `InferenceSession`, созданные через `ExecutionProviderSelector.Apply`, как у трёх распознавателей. Офлайн — тот же потоковый цикл с пресетом Offline: память ограничена, длина записи не важна.
- `SortformerStreamingState` / `SortformerStreamingUpdater` — порт из `tools/converters/SortformerStreaming` с тестами против дампа состояний NeMo.
- `DiarizationPostprocessor` — чистая функция: порог с гистерезисом, минимальная речь и тишина, склейка коротких пауз → `SpeakerTurn`. Параметры записываются в `diarizationRuns[].post`.
- `SpeakerProbabilities` — вероятности на кадр 80 мс, квантованные в байт: 8 байт на кадр, час ≈ 360 KB. Хранятся в файле прогона, чтобы переразметить слова или сменить порог без нового прогона модели.

В Core (`VoiceType.Uno.Core/Services/History`, с тестами): `SpeakerAssigner` (§10.5), `SpeakerMatcher` (§10.6), `SpeakerStats` (время речи, слова, реплики, перекрытия), `SpeakerDirectory` (люди, имена, цвета, merge/split; единственный писатель `manifest.speakers` через `RecordingStore`).

### 10.4 Модель данных: прогоны, слои, люди

**Решение: диаризация — неизменяемый слой рядом с версией, а не новая версия транскрипта.**

Почему не новая версия:

- **Текст не меняется.** Версия `v4 = v2 + спикеры` сделала бы перевод v2 и источники чатов `outdated` (§5.2) при том же тексте, а diff показал бы «0 изменений».
- **Диаризация — свойство аудио, а не транскрипта.** Один прогон модели размечает любую версию: live, перераспознанную, правленую. С версией-копией пришлось бы гонять модель или копировать файлы для каждой версии.
- **Шаблон уже есть.** Переводы так и лежат: параллельный файл с тем же индексом сегмента `i` (§5.4).
- **Совместимость.** Новые поля манифеста старое приложение сохраняет через `[JsonExtensionData]` (он есть у `RecordingManifest`). Новый `kind` версии оно показало бы как непонятную версию.

Вопрос «какая ASR и какая диаризация дали этот вид» решается связкой: версия `vN` → её `model`; слой `sK` → прогон `rM` → его `model` и `mode`. Обе части неизменяемы. Меняются только указатели: `currentVersion` (есть) и новый `currentSpeakers[vN]`.

Три уровня:

1. **Прогон** (`diarizationRuns[]`) — один запуск модели по аудио или живой поток. Файл `diarization/rM.json`: реплики спикеров и вероятности. Номера спикеров — каналы модели в порядке появления.
2. **Слой** (`speakerLayers[]`) — разметка конкретной версии конкретным прогоном: `speakers/vN.rM.json` рядом с `transcripts/vN.json`. Его строит `SpeakerAssigner` за миллисекунды, без модели. Новая версия (перераспознанная или правленая) автоматически получает слой от последнего прогона.
3. **Люди** (`speakers` в манифесте) — имена и цвета от пользователя и сопоставление «прогон + канал → человек». Merge — два канала на одного человека, Rename — поле `name`. Файлы прогонов и слоёв при этом не переписываются.

**Дельта `manifest.json`** относительно `RecordingManifest` (schema 1, `VoiceType.Uno.Core/Services/History/RecordingManifest.cs`). Все поля новые и необязательные, схема не поднимается:

```jsonc
{
  // … существующие поля без изменений …
  "diarizationRuns": [
    { "id": "r1", "file": "diarization/r1.json", "createdAt": "2026-10-12T14:15:02+03:00",
      "source": "live",                                   // live | job
      "model": { "catalogRepoId": null,                   // repo каталога с ONNX-вариантами — после спайка
                 "upstream": "nvidia/Nemotron-3-Diarization", "upstreamRevision": "<HF commit>",
                 "folder": "nemotron-3-diarization-onnx-int8", "title": "Nemotron 3 Diarization — 8-bit (INT8)",
                 "precision": "Int8", "family": "Sortformer" },              // поля ModelInfo + upstream
      "mode": { "preset": "low1040", "latencySec": 1.04, "spkcacheLen": 264, "fifoLen": 264,
                "chunkLen": 9, "rightContext": 4, "updatePeriod": 222 },     // кадры по 80 мс, как в карточке
      "post": { "threshold": 0.5, "minSpeechSec": 0.25, "minSilenceSec": 0.2, "maxSpeakers": 8 },
      "settings": { "executionProvider": "cpu" },
      "app": { "name": "VoiceType.Uno", "version": "1.5", "platform": "win-x64" },
      "stats": { "speakers": 3, "overlapSec": 9.8, "rtf": null, "elapsedSec": null, "fellBehindAtSec": null } },
    { "id": "r2", "file": "diarization/r2.json", "source": "job", "jobId": "j-32", "createdAt": "…",
      "model": { "…": "…" },
      "mode": { "preset": "offline", "latencySec": 30.4, "spkcacheLen": 264, "fifoLen": 40,
                "chunkLen": 340, "rightContext": 40, "updatePeriod": 300 },
      "post": { "…": "…" },
      "stats": { "speakers": 4, "overlapSec": 21.4, "rtf": 0.04, "elapsedSec": 30.2 } }
  ],
  "speakerLayers": [
    { "id": "s1", "version": "v1", "run": "r1", "file": "speakers/v1.r1.json", "assigner": 1, "createdAt": "…" },
    { "id": "s2", "version": "v2", "run": "r2", "file": "speakers/v2.r2.json", "assigner": 1, "createdAt": "…" }
  ],
  "currentSpeakers": { "v1": "s1", "v2": "s2" },         // слой по версии; нет записи → последний слой версии
  "speakers": {                                           // люди этой записи; имена хранятся только на устройстве
    "people": [ { "id": "p1", "name": "Mark", "color": 0 }, { "id": "p2", "name": "Anna", "color": 1 },
                { "id": "p3", "name": null, "color": 2 } ],                 // null → «Speaker 3» по порядку появления
    "map": { "r1": { "0": "p1", "1": "p2", "2": "p3" },
             "r2": { "0": "p1", "1": "p2", "2": "p3", "3": "p1" } },       // r2 #3 слит с Mark (Merge)
    "overrides": [ { "from": 312.40, "to": 318.00, "person": "p2", "at": "…" } ],  // Change speaker у сегмента; по времени, переживает смену версии
    "suggested": [ { "run": "r2", "speaker": 2, "name": "Oleg", "analysis": "a1" } ]  // подсказка анализа, пока не принята
  },
  "analyses": [                                           // шаг Analyze (§11.3)
    { "id": "a1", "file": "analysis/v2.s2.insights.json", "version": "v2", "speakerLayer": "s2",
      "engine": { "kind": "native", "model": "gemma-4-E2B-it.litertlm", "backend": "cpu" }, "promptVersion": 1,
      "namesSnapshot": { "p1": "Mark", "p2": "Anna", "p3": null }, "createdAt": "…", "status": "complete" }
  ]
}
```

**Дельта версии** (`TranscriptVersion`). Поле пишется один раз при создании, поэтому версия остаётся неизменяемой:

```jsonc
{ "id": "v4", "kind": "rerecognized", "…": "…",
  "origin": { "chainId": "ch-3f1a", "trigger": "auto",             // user | auto (§11.5)
              "steps": ["rerecognize", "diarize", "analyze"] } }   // план цепочки, которая создала версию
```

**`transcripts/vN.json` не меняется** (schema 1, `TranscriptDocument`). Слой спикеров лежит рядом и индексируется так же, как перевод:

```jsonc
// speakers/v2.r2.json — разметка версии v2 прогоном r2; номера — каналы прогона, не люди
{ "schema": 1, "version": "v2", "run": "r2", "assigner": 1,
  "segments": [
    { "i": 0,  "spk": 0,  "words": [0,0,0,0,0,0,0,0,0,0,0,0] },
    { "i": 10, "spk": 1,  "words": [1,1,1,1,1,1,1,1,1,1], "overlap": [[0,2],[1,2],[2,2]] },  // [индекс слова, второй канал]
    { "i": 12, "spk": 1,  "words": [1,1,1,1,1,1,3,3,3,3,3,3] },                               // смена спикера внутри сегмента
    { "i": 13, "spk": -1, "words": [-1,-1,-1,-1,-1] }                                          // -1 — не определён
  ] }
```

```jsonc
// diarization/r2.json — выход модели после постобработки; от версии не зависит
{ "schema": 1, "id": "r2", "durationSec": 754.2,
  "turns": [ { "s": 0.00, "e": 40.10, "spk": 0 }, { "s": 89.6, "e": 97.0, "spk": 1 }, { "s": 361.2, "e": 362.5, "spk": 2 } ],
  "probs": { "frameSec": 0.08, "speakers": 8, "encoding": "u8-base64", "data": "…" } }   // ≈ 360 KB на час
```

Правила:

- В файлах хранятся **каналы прогона** (0..7), а не люди. Rename и Merge меняют только `manifest.speakers`.
- `assigner` — версия алгоритма `SpeakerAssigner`. Если алгоритм меняется, слой пересобирается из `probs` без модели: появляется новый слой, старый остаётся.
- Удаление версии удаляет её слои, удаление прогона — его слои. Прогон, на котором стоит текущий слой, удалить нельзя (как текущую версию).
- Офлайн-прогон пишется в `diarization/rM.json.part` и переименовывается в конце. Checkpoint не делаем: прогон быстрый, после обрыва начинается заново (как `autoTitle`).
- Поиск (§5.8): в `index.json` добавляются имена людей и `speakerCount`; фильтр With speakers.
- Приватность (§6.5): имена — персональные данные, лежат только в папке записи (она уже исключена из Auto Backup). Голосовых отпечатков нет: каналы — номера внутри одного прогона, а не идентичность голоса, как и сказано в гайде модели.

### 10.5 От кадров к словам: `SpeakerAssigner`

Чистая функция в Core: `Assign(probs, turns, TranscriptDocument version, options) → SpeakerLayerDocument`.

- Слово `[s, e]` получает канал с наибольшей суммой вероятности по кадрам 80 мс внутри слова. Если второй канал выше порога больше чем на половине длительности слова, слово помечается `overlap`.
- Если активного канала нет (тайминг слова — оценка, §5.3), берётся ближайшая реплика в пределах 0.5 с, иначе `-1`.
- Сегмент получает канал большинства по длительности слов. Смена спикера внутри сегмента показывается чипом в тексте; сегмент не разрезается, потому что индексы `i` общие с переводом и ссылками чатов.
- Тайминги слов у Parakeet — оценка (§5.3), поэтому на границе реплик возможна ошибка в 1–2 слова. Точные тайминги TDT (§9.3, этап 5) это снимают. Тест: разметка `sample-0.mp3` по эталонному RTTM, доля слов с верным спикером.
- Поверх применяются `overrides` пользователя (по времени).

### 10.6 Номера спикеров: внутри прогона, между прогонами, больше 8

- **Внутри прогона** номера устойчивы: AOSC помнит прежних спикеров между чанками. У офлайн-пресета чанки по 27 с, правила те же.
- **Между прогонами** (живой → офлайн, другая модель или пресет) номера могут не совпасть. `SpeakerMatcher.Match(prevRun, newRun)` строит матрицу совместного времени 8×8 и решает задачу назначения (венгерский алгоритм). Канал нового прогона наследует человека, если пересечение ≥ 60 % его речи; иначе появляется новый человек `Speaker N`. В листе Speakers подпись «Names carried over from the previous detection», пользователь может поправить.
- **Между записями** люди не переносятся: метки локальны для сессии, а сравнение голосов — биометрия. Вне этой итерации.
- **Больше 8 голосов.** Модель различает до 8 каналов; девятый голос попадёт в один из них. Если в прогоне заняты все 8 каналов, лист Speakers предупреждает «Up to 8 voices are told apart — some may be mixed».
- **Короткий лишний спикер** (< 3 % времени, как `Speaker 4` в макете) — частая ошибка, когда один голос разбит на два. Лист предлагает `Merge…`.

### 10.7 Живые метки спикеров (только если тесты позволят)

**Статус: за флагом; решение — по итогам спайка (этап 6) и теста устройства.** Выключатель `Live speaker labels` есть в секции MODE бокового меню и в Settings. На телефонах по умолчанию **выключен**. На десктопе включается по умолчанию, только если спайк уложится в бюджет ниже.

Оценка стоимости — порядок величины: FLOPs ≈ 2 × 10⁸ параметров × число кадров, без квадратичной части внимания **[замерить в спайке]**. Энкодер на каждом шаге пересчитывает весь вход `spkcache + fifo + chunk + right`:

| Пресет | Кадров на шаг | Шаг, с | Кадров энкодера на 1 с аудио | ≈ GFLOP на 1 с аудио |
|---|---|---|---|---|
| offline 30.4 с | 264+40+340+40 = 684 | 27.2 | 25 | 5 |
| delayed-live (наш, метки через ~10 с) | 264+40+125+13 = 442 | 10.0 | 44 | 9 |
| lean-live (наш, ~1 с) | 264+40+9+4 = 317 | 0.72 | 440 | 88 |
| 1.04 с (карточка) | 264+264+9+4 = 541 | 0.72 | 751 | 150 |
| 0.32 с (карточка) | 264+264+3+1 = 532 | 0.24 | 2217 | 443 |

Вывод: офлайн дёшев на любом устройстве, а пресеты карточки для живого режима на телефонном CPU вместе с потоковым ASR почти наверняка не уложатся. Поэтому живых режимов два: **Fast** (метки через ~1 с: lean-live или 1.04 с; кандидат для десктопа) и **Delayed** (метки через ~10 с, дёшево; кандидат для телефона). DER своих пресетов неизвестен — это пункт спайка.

**Гейт.**

- **Спайк.** Для Fast на десктопе RTF(потоковый Parakeet INT4 + диаризация) ≤ 0.5, для Delayed на телефоне класса Moto G100 — ≤ 0.6. Диаризация добавляет к ASR не больше 25 % CPU и не больше 300 MB RSS. Если не проходит — на этом классе устройств живого режима нет, остаётся фоновая диаризация после Stop.
- **Тест устройства** (Models › Speakers › `Test this device`): 30 с встроенного образца через обе модели; результат сохраняется в `Speakers.LiveBenchmark` (RTF, режим, версия приложения). На телефоне выключатель включается только после пройденного теста, иначе подпись объясняет почему.

**Как работает.**

- `LiveDiarizationStage` получает те же батчи 16 kHz, что `ISessionAudioWriter` (tee в `RecognitionService.ProcessLoop`, §6.2), через ограниченный `Channel<float[]>`. Свой поток, свой `IStreamingDiarizer`.
- Закоммиченная фраза Parakeet получает спикера, когда кадры диаризации покрыли её конец (задержка = пресет + время счёта). Текст-превью идёт без спикера (пунктирная точка «Speaker in ~1 s»).
- **Отставание.** Если очередь кадров длиннее 3 с (Fast) или 20 с (Delayed), стадия останавливается, текст идёт дальше без меток, в карточке появляется «Speaker labels paused… Speakers will be added after Stop», а после Stop ставится задание `diarize` с условием When idle. Это и есть запасной путь «фоновая диаризация после остановки».
- В режиме Inject text живые метки не показываются: в чужое приложение вставляется только текст.
- После Stop живой прогон сохраняется как `r1` (`source: "live"`) вместе со слоем для `v1`. При `Speakers.RediarizeAfterLive = true` (по умолчанию) ставится офлайн-прогон `r2` — он точнее на 0.5–2 п.п. DER, — и `SpeakerMatcher` переносит имена из `r1`.
- Память: модель загружается, только когда метки включены и идёт запись, и выгружается после Stop.

### 10.8 UI спикеров

- **Transcript** (`*-transcript-speakers`). Строка имени с цветной точкой появляется, когда меняется спикер. Смена внутри сегмента — чип `● Speaker 4`, перекрытие — пунктирное подчёркивание. Управление зависит от ширины: < 600 px — строка `👥 4 speakers  Mark, Anna, … ▾` и `✎` (как строка источников в чате); 600–839 px или низкое окно — пик `👥 4 speakers ▾` в панели инструментов; от 840 × 480 — чипы-фильтры `All speakers · ● Mark 46% · ● Anna 34% · … · Rename, merge`.
- **Фильтр по спикеру** (`*-speakers-filter`): строка `● Anna only · 4 of 14 segments ×`. Чужие сегменты свёрнуты в `··· 3 segments by Mark`. В плеере кнопки `−10/+10` становятся `Previous/Next turn by Anna`, воспроизведение перескакивает чужие реплики.
- **Лист Speakers** (`*-speakers-sheet`): кнопка цвета 44×44 · имя и `46% · 5:47 · 7 turns` (тап — Rename, тот же лист, что для заголовка) · `⋯` (Show only, Merge into…, Split, Change colour). Подсказки: «Speaker 4 talks only 38 s… Merge…» и `Gemma suggests “Oleg”` (из анализа; применяется только по тапу). Внизу `Detect again…` (лист Reprocess с выключенным ASR) и `Done`.
- **Merge** (`*-speakers-merge`): флажки голосов, «Name to keep», `Merge 2`; подпись, что сама детекция не меняется, а отменить можно через Split.
- **Долгий тап по сегменту** → `Change speaker…` (пишет `speakers.overrides`).
- **Versions.** В карточке версии `Speakers 4 · Nemotron Diarization INT8` и `Detection offline, 30 s buffer` (у живой версии — `3 · live labels`, `live, 1 s delay`), чип `Speakers`; `⋯` → `Detect speakers again…`. Над списком две кнопки: `Reprocess…` (основная) и `Re-recognize…`.
- **History:** чип `4 speakers`, фильтр With speakers, поиск по именам.
- **Главный экран при записи** (`*-live-speakers`): над фразами строки `● Mark · 03:20`; в `*-live-fallback` — плашка о паузе меток.
- **Chats.** В контексте строки вида `[S1 03:20] Anna: …`; инструкция «use the speaker names as given; never invent names for “Speaker N”». Ссылки остаются `[S1 03:20]`, поэтому `CitationParser` не меняется; чип показывает `Weekly sync · Anna 03:20`, в чате с одной записью — `Anna 03:20`. В `citations[]` добавляется снимок `"speaker": "Anna"`, в `chat.json sources[]` — `"speakerLayer": "s2"` (какой слой читала модель). Быстрые подсказки: «Action items — by speaker», «Who said what». В экспорте Markdown — `[Anna 03:20]` / `[Weekly sync · Anna 03:20]`.
- **Экспорт:** флажок `Speaker names` (включён, если слой есть): `.txt` — `[00:03:20] Anna: …`, `.srt` и `.lrc` — префикс `Anna: `.
- **Models › Speakers** (`*-models-speakers`): новая вкладка страницы «Модели» (Speech · Speakers · Gemma). Карточка Nemotron 3 Diarization: источник, ONNX, 100M параметров, лицензия, «до 8 спикеров». Варианты INT8 (по умолчанию на телефоне) и FP32 с размерами «≈ … *» до замера, `Download` / `⋯ Delete`. Группа Speaker detection: качество для записей, живые метки, `Test this device`. Скачивание — тот же `ModelDownloadManager`; в `ModelCatalog` нужен отдельный вид модели для диаризации **[дельта каталога решается на этапе 7]**.

## 11. Фоновая переобработка (Reprocess)

### 11.1 Что это

`Reprocess…` — одна цепочка шагов по записи: **Re-recognize** выбранной ASR-моделью → **Detect speakers** → **Analyze** (Gemma: сводка, задачи с ответственными, время речи). Любой шаг можно выключить. Результат — новая версия `vN` (если включён шаг ASR), слой спикеров для неё и файл анализа. Без шага ASR цепочка добавляет слой и анализ к выбранной версии; `Detect again…` в листе Speakers — это Reprocess только со вторым шагом.

Точки входа: Versions → `Reprocess…` (рядом прежний `Re-recognize…`); `⋯` записи → Reprocess…; History → выбор → `↻` → лист «Reprocess N recordings» (бывший пакетный Re-recognize с переключателями `Then detect speakers` и `Then summarize`); автоматически — §11.5.

Лист Reprocess (`*-reprocess-sheet`): `1 · Speech` (переключатель и модель), `2 · Speakers` (переключатель и режим: Best quality offline 30 s / Faster), `3 · Analysis` (переключатель), `Make v4 current when done`, `When` (Now / When idle / When charging / окно времени — тот же набор, что в §9.5), оценка `≈ 4 min · 3 steps in the Queue`.

### 11.2 Порядок и результат

- Шаги идут строго по порядку: разметке нужна готовая версия, анализу — версия со спикерами.
- Каждый шаг сразу сохраняет свой неизменяемый результат: `v4` появляется в Versions после первого шага с подписью «speakers pending». **`currentVersion` переключается в конце цепочки** (если отмечено Make current), чтобы пользователь не видел текст без спикеров, который через минуту поменяется. Если упал шаг 2 или 3, указатель всё равно переключается на `v4` — результат ASR ценен сам по себе, а у упавшего шага в Queue есть Retry.
- **Повторное использование.** Если прогон диаризации с той же моделью, вариантом и пресетом по этому аудио уже есть, шаг 2 не запускает модель, а только строит слой для новой версии (`SpeakerAssigner`, миллисекунды).
- Шаг 3 заодно пишет `analysis/vN.summary.json` (§7.4), поэтому первый ответ в чате после ночной переобработки не ждёт map-reduce.

### 11.3 Анализ

Файл `analysis/v4.s2.insights.json` (Gemma через `GemmaEngineHost`, задание `analyze`, map-reduce как у `summarize`, checkpoint по части):

```jsonc
{ "schema": 1, "version": "v4", "speakerLayer": "s2", "model": "gemma-4-E2B-it.litertlm", "promptVersion": 1,
  "summary": [ { "text": "The meeting moves to 10:00 on Monday.", "cite": { "i": 0, "t": 0.0 } } ],
  "actionItems": [ { "owner": { "run": "r2", "spk": 1 }, "text": "Share the hiring plan in the team channel",
                     "due": "Wed evening", "cite": { "i": 3, "t": 90.0 } } ],
  "decisions": [ "…" ], "openQuestions": [ "…" ],
  "nameHints": [ { "run": "r2", "spk": 2, "name": "Oleg", "cite": { "i": 7, "t": 245.0 } } ],  // «Oleg, can you…» — только подсказка
  "stats": { "0": { "talkSec": 347.0, "words": 840, "turns": 7, "overlapSec": 6.1 }, "1": { "…": "…" } } }  // SpeakerStats, без Gemma
```

- Ответственный хранится как канал прогона и показывается через `manifest.speakers`, поэтому Rename и Merge сразу видны в задачах. Текст сводки содержит имена на момент генерации; по `namesSnapshot` из манифеста UI показывает «Names changed after this summary · Regenerate».
- `SpeakerStats` считается в Core без Gemma: время речи, слова, реплики, перекрытия, самая длинная реплика.
- UI (`*-recording-insights`): карточка **Insights** вверху вкладки Chats записи (это и есть вкладка «Gemma по записи») — сводка, задачи с владельцем и временем (строки-кнопки ≥ 44 px, тап перематывает), время речи полосами цвета спикера, `Regenerate`.

### 11.4 Очередь: новые виды заданий и цепочки

Дельта к реализованной модели (`VoiceType.Uno.Core/Services/History/JobModels.cs`, `JobScheduler.cs`):

```csharp
public enum HistoryJobKind { Unknown = 0, Rerecognize, Translate, AutoTitle, Summarize,
                             Diarize,   // новый: прогон диаризации + слой для версии
                             Analyze }  // новый: Gemma-анализ версии со спикерами
```

```jsonc
// HistoryJob — новые необязательные поля (рядом с существующими id, kind, recordingId, batchId, …)
{ "id": "j-32", "kind": "diarize", "recordingId": "7f3a9c",
  "chainId": "ch-3f1a",                 // шаги одной цепочки Reprocess (по аналогии с batchId)
  "dependsOn": "j-31",                  // стартует только после Completed этого задания
  "batchId": "b-77",                    // цепочки, поставленные из выбора в History
  "parameters": {
    "sourceVersion": null,              // null + dependsOn → версия из resultId предыдущего шага
    "diarization": { "modelFolder": "nemotron-3-diarization-onnx-int8", "catalogRepoId": null,
                     "preset": "offline", "threshold": 0.5, "maxSpeakers": 8, "reuseExistingRun": true },
    "analysis": { "summary": true, "actionItems": true, "speakerStats": true, "promptVersion": 1 },
    "makeCurrentAtChainEnd": true,
    "origin": "user"                    // user | auto | liveFallback
  },
  "condition": { "kind": "atTime", "windowStart": "01:00", "windowEnd": "06:00",
                 "requireCharging": true, "requireIdle": false },  // новое: условия поверх kind
  "resultId": "s2" }                    // diarize → id слоя; analyze → id анализа
```

**Решение: цепочка — это `chainId` и `dependsOn` у обычных заданий, а не отдельный вид «reprocess».** Планировщик остаётся плоским списком; правило «одно тяжёлое задание за раз» действует на каждый шаг; пауза и отмена цепочки — `ForChain(chainId)`, как `ForBatch`. Шаг `Diarize` один и тот же в цепочке и сам по себе (после отставания живых меток, `Detect again…`). Отдельный контейнер дублировал бы прогресс и состояние шагов.

Правила планировщика (дельта `JobScheduler.GetWaitReason`):

- новая причина `JobWaitReason.WaitingForPreviousStep`: `dependsOn` ещё не `Completed`;
- если `dependsOn` в `Failed` или `Cancelled`, `JobQueue` переводит зависимые шаги в `Cancelled` с `error: "previous step failed"`; Retry упавшего шага возвращает их в `Queued`;
- **условия складываются.** У реализованного `JobCondition` один `Kind`, а для «ночью и на зарядке» нужно два. Добавляются `requireCharging` и `requireIdle`, которые `GetConditionReason` проверяет после `Kind`;
- пакет цепочек идёт цепочка за цепочкой: `EnqueueChain` вставляет шаги подряд, и FIFO внутри приоритета само возьмёт `A2` раньше `B1`, потому что `B1` стоит в списке позже;
- `IsHeavy(Diarize) = IsHeavy(Analyze) = true` — одно тяжёлое задание за раз. Разрешить диаризацию параллельно с Gemma-заданием на десктопе можно после замеров памяти;
- **совместимость.** `LenientEnumConverter` читает незнакомый вид как `Unknown` и записывает его обратно как `"unknown"`. Старое приложение, переписав `queue.json`, потеряет вид `diarize`. Поэтому `JobQueueDocument.CurrentSchema` поднимается до 2, и приложение, которое видит схему новее своей, открывает очередь только на чтение.

Queue (`*-queue-chain`): карточка цепочки `Reprocess · Weekly sync…`, подпись `Parakeet FP32 → speakers → summary · creates v4`, шаги ✓ / ● / ○ с прогрессом текущего, общий прогресс `Step 2 of 3 · 55%`, `Pause`, `Cancel chain`; в `⋯` — Skip step. Запланированная автопереобработка — отдельная карточка с условиями `☾ Tonight 01:00–06:00`, `⚡ When charging` и кнопками `Settings`, `Run now`.

### 11.5 Автопереобработка новых записей

Settings › **Speakers and reprocessing** (`*-settings`); ключи пишет только `SettingsStore`:

| Ключ | Тип, по умолчанию | Что делает |
|---|---|---|
| `Speakers.LiveLabels` | bool, `false` на телефоне; на десктопе — по итогам спайка | Живые метки (§10.7); на телефоне включается только после теста устройства |
| `Speakers.LiveMode` | `Fast` / `Delayed`, по классу устройства | Пресет живых меток |
| `Speakers.AfterStop` | `Off` / `Best` (offline) / `Fast`, `Best` | Фоновая диаризация после Stop (When idle); `Off` — только вручную или через Reprocess |
| `Speakers.RediarizeAfterLive` | bool, `true` | Офлайн-прогон после живых меток |
| `Speakers.ModelFolder` | string | Установленный вариант (INT8 или FP32) |
| `Speakers.LiveBenchmark` | `{ rtf, mode, passedAt, appVersion }` | Результат Test this device |
| `Reprocess.AutoNew` | bool, `false` | Ставить цепочку для каждой новой записи |
| `Reprocess.Profile` | `{ asrModelFolder, rerecognize, diarize, analyze, makeCurrent }`, всё `true` | Что делает автопереобработка |
| `Reprocess.AutoCondition` | `JobCondition`: окно 01:00–06:00 + `requireCharging` | Когда |
| `Reprocess.MinDurationSec` | int, `20` | Запись короче — без диаризации и анализа |

- При `AutoNew = true` после сохранения записи (§6.2) ставится цепочка с `origin: "auto"`. Шаг ASR пропускается, если модель профиля совпадает с моделью живой версии; шаг диаризации — если уже есть офлайн-прогон той же моделью. Inject-диктовки не переобрабатываются.
- Работа не дублируется: при `AutoNew` фоновая диаризация после Stop не ставится, её сделает цепочка.
- Android: «ночью на зарядке» при закрытом приложении — `WorkManager` с `RequiresCharging`; механизм и пометки **[не проверено]** те же, что в §9.5.

## 12. Проверка раскладки

### 12.1 Метод

Тот же стенд, что у соседнего дизайна (frame.html + headless Edge), правила расширены:

1. каждый интерактивный элемент (`[data-hit]`) верхнего слоя (лист/диалог > меню > страница History/Chats/Queue/Settings/Models > главный экран) целиком внутри безопасной области (окно минус системные панели и клавиатура); у элементов в прокрутке — сама прокрутка внутри;
2. цель касания ≥ 44×44;
3. нет пересечений интерактивных элементов (по видимой части);
4. подпись не обрезана (`scrollWidth ≤ clientWidth`);
5. Record по центру главного экрана; Play по центру панели плеера (|dx| ≤ 1 px, кроме низких окон, где плеер в одну строку);
6. области чтения ≥ 96 px (≥ 72 px на окнах ниже 480 px): список истории и чатов, транскрипт (и в режиме правки), перевод, сообщения чата, diff, версии, вкладка Chats записи (в том числе с Insights), Queue, Settings, Models;
7. снэкбар не заходит на панель команд.

```powershell
pwsh docs/design/voicetype-uno-history/render.ps1            # 125 сценариев, код выхода 1 при проблеме
pwsh docs/design/voicetype-uno-history/render.ps1 -Only chat  # часть сценариев
pwsh docs/design/voicetype-uno-history/render.ps1 -Only speakers  # сценарии спикеров (ревизия 3)
```

### 12.2 Результат: 125/125 OK

| Группа | Viewport, вставки | Сценарии |
|---|---|---|
| Телефон (29 + 11) | 360×800, статус 32, жесты 24 | main-saved, drawer-history (History · Chats · Queue), history, history-search (клавиатура 320), history-select, history-filter, **batch-sheet**, **delete-confirm**, transcript-playing, transcript-noaudio, **transcript-edit** (клавиатура 320), **rename** (клавиатура 320), **export-sheet**, **recording-chats**, **addto-sheet**, **chats**, chat-empty, chat-streaming, chat-keyboard (320), **chat-add-source**, **chat-sources**, **chat-export**, translation, translation-progress, versions-running, versions-diff, rerecognize-sheet, **queue**, **settings** |
| Маленький телефон (19 + 10) | 320×568, 24 / 3-кнопочная 48 | main-saved, history, **history-select**, **batch-sheet**, **delete-confirm**, transcript, **transcript-edit** (260), **rename** (260), **export-sheet**, **recording-chats**, **chats**, chat, chat-keyboard (260), **chat-add-source**, **chat-sources**, versions-diff, rerecognize-sheet, **queue**, **settings** |
| Горизонтально (17 + 8) | 800×360, 24 / справа 48 | main-saved, history, **batch-sheet**, **delete-confirm**, transcript (плеер в строку), **transcript-edit**, **rename**, **export-sheet**, **recording-chats**, **chats**, chat, **chat-add-source**, **chat-sources**, versions, rerecognize-sheet, **queue**, **settings** |
| Десктоп (19 + 11) | 1280×800 | main-saved, history-empty, **batch-dialog**, **delete-confirm**, transcript, **transcript-edit**, **rename**, **export-dialog**, **recording-chats**, **chats-empty** (заглушка справа), chat (master-detail), **chat-add-source**, **chat-export**, versions-diff, rerecognize-dialog, dark-translation, **queue**, **settings**, **dark-chat** |
| Минимальное окно | 480×560 | transcript |
| Ревизия 3: телефон (11) | 360×800 | live-speakers, live-fallback, transcript-speakers, speakers-filter, speakers-sheet, speakers-merge, versions-speakers, reprocess-sheet, recording-insights, queue-chain, models-speakers |
| Ревизия 3: маленький (10) | 320×568 | live-speakers, transcript-speakers, speakers-filter, speakers-sheet, speakers-merge, versions-speakers, reprocess-sheet, recording-insights, queue-chain, models-speakers |
| Ревизия 3: горизонтально (8) | 800×360 | live-speakers, transcript-speakers (пик спикеров в панели инструментов), speakers-sheet, versions-speakers, reprocess-sheet, recording-insights, queue-chain, models-speakers |
| Ревизия 3: десктоп (11) | 1280×800 | live-speakers, transcript-speakers (чипы-фильтры), speakers-filter, speakers-sheet, speakers-merge, reprocess-dialog, versions-speakers, recording-insights, queue-chain, models-speakers, dark-speakers |

Жирным — новые в ревизии 2. Прежние `*-chat*` сценарии перенесены с вкладки записи на страницу чата. В ревизии 3 изменились и прежние экраны: меню (выключатель Live speaker labels, пункт Models), строка истории (чип `4 speakers`), Filter и Export (спикеры), пакетный лист (теперь «Reprocess 3 recordings» с шагами), Versions (кнопки Reprocess… и Re-recognize…), ссылки в чате с именами спикеров, Settings (группа Speakers and reprocessing). Все прежние 85 сценариев прогнаны заново и проходят.

### 12.3 Что нашла проверка и как это исправлено

Ревизия 3: новые экраны прошли проверку с первого прогона, потому что заранее повторяют уже проверенные шаблоны. Решения, которые для этого понадобились:

| Риск раскладки | Решение в дизайне |
|---|---|
| Чипы-фильтры четырёх спикеров + `Rename, merge` на 320 px заняли бы две-три строки (как источники чата в ревизии 2) | Три состояния по ширине: < 600 px — одна строка-сводка `👥 4 speakers · имена… ▾` + `✎` (тот же шаблон, что строка источников); 600–839 px и низкие окна — пик `4 speakers ▾` в существующей панели инструментов (своей строки нет, область чтения в 800×360 не сжимается); ≥ 840 × 480 — чипы с долей речи |
| В режиме фильтра в плеере нужны «предыдущая/следующая реплика», а лишние кнопки сломали бы центровку Play | Те же две кнопки `−10/+10` меняют назначение и подпись (`Previous/Next turn by Anna`); сетка плеера не меняется |
| Имя спикера отдельной кнопкой в каждой строке транскрипта дало бы пересечение с целью касания сегмента | Имя — часть кнопки-сегмента (не отдельная цель); переименование — только в листе Speakers |
| Строки листа Speakers: цвет, имя и `⋯` — три цели в одной строке | Сетка `44px · 1fr · 44px`, у каждой цели свои 44×44 |
| Кнопка `Download` рядом с вариантом модели | Вариант и действие — соседние ячейки `.vrow` (`1fr · auto`), а не вложенные кнопки |

Ревизия 2:

| Находка | Исправление в дизайне |
|---|---|
| `c-input: hit target 30x44 < 44x44` во **всех 9** сценариях чата: поле ввода сжалось до 30 px. Причина — новый стиль флажка (`.box` 20×20) для диалогов совпал по имени со стилем поля ввода | Флажок переименован (`.cbox`). Для XAML вывод тот же: у флажков в листах и у поля чата — разные именованные стили, без неявных стилей по типу на общем `Border` |
| `rn-field overlaps rn-clear (44x44)` во всех 4 сценариях Rename: целью касания было всё поле, а внутри него — кнопка очистки | Цель касания — только текстовая часть, кнопка очистки рядом. В XAML это обычный `TextBox` со встроенной `DeleteButton`, своей кнопки не нужно |

Найдено при просмотре скриншотов (правила этого не ловят):

| Находка | Исправление |
|---|---|
| На 320×568 чипы источников чата (3 записи + `Add recording`) занимали две строки, плюс строка контекста в две строки: под сообщения оставалось ~180 px. При 8 источниках было бы 4 строки чипов | Ниже 600 px ширины или 480 px высоты чипы заменены одной строкой `▤ 3 recordings · имена… ▾` + `+`; строка открывает лист Sources (там же удаление). Чипы остаются на широких окнах |
| Подпись над списком в режиме правки обрезалась многоточием на 320 px | Подпись переносится на вторую строку и сокращена |

Ревизия 1 (для истории): Play не по центру из-за колонок `1fr` (→ `minmax(0,1fr)`); карточка записи вылезала вправо на 320 px (→ `*`-колонки без `MinWidth`, вкладка «Translate» вместо «Translation»); обрезанные подписи в diff на 320 px (→ перенос, короткие имена версий); сообщения чата 34 px на 320×568 с клавиатурой (→ состояние `Keyboard`: шапка 48 px, лишние строки скрыты); ошибка порядка рендера в самом макете для десктопа без выбора.

Проверка относится к **дизайну** (HTML повторяет предлагаемую сетку), а не к приложению. После реализации те же правила нужно прогнать на desktop-голове через `docs/design/voicetype-uno-mobile/uia-layout-check.ps1`, добавив туда пункты 4–7.

![Сохранено](screenshots/phone-main-saved.png) ![Меню](screenshots/phone-drawer-history.png) ![История](screenshots/phone-history.png) ![Транскрипт](screenshots/phone-transcript-playing.png)

![Правка](screenshots/phone-transcript-edit.png) ![Чаты записи](screenshots/phone-recording-chats.png) ![Чат](screenshots/phone-chat-streaming.png) ![Источники](screenshots/phone-chat-sources.png)

![Очередь](screenshots/phone-queue.png) ![Пакет](screenshots/phone-batch-sheet.png) ![Удаление](screenshots/phone-delete-confirm.png) ![Настройки](screenshots/phone-settings.png)

![Перевод](screenshots/phone-translation.png) ![Версии](screenshots/phone-versions-running.png) ![Diff](screenshots/phone-versions-diff.png) ![Экспорт](screenshots/phone-export-sheet.png)

![Десктоп, чат с тремя записями](screenshots/desktop-chat.png)

![Десктоп, очередь](screenshots/desktop-queue.png)

![Десктоп, diff](screenshots/desktop-versions-diff.png)

Ревизия 3 — спикеры и переобработка:

![Живые метки](screenshots/phone-live-speakers.png) ![Спикеры в транскрипте](screenshots/phone-transcript-speakers.png) ![Фильтр по спикеру](screenshots/phone-speakers-filter.png) ![Лист Speakers](screenshots/phone-speakers-sheet.png)

![Merge](screenshots/phone-speakers-merge.png) ![Reprocess](screenshots/phone-reprocess-sheet.png) ![Цепочка в Versions](screenshots/phone-versions-speakers.png) ![Insights](screenshots/phone-recording-insights.png)

![Цепочка в Queue](screenshots/phone-queue-chain.png) ![Models › Speakers](screenshots/phone-models-speakers.png) ![Отставание живых меток](screenshots/phone-live-fallback.png) ![Настройки](screenshots/phone-settings.png)

![Десктоп, транскрипт со спикерами](screenshots/desktop-transcript-speakers.png)

![Десктоп, Reprocess](screenshots/desktop-reprocess-dialog.png)

## 13. Эскиз XAML (карточка записи)

Не компилировался; показывает сетку, на которую опирается проверка.

```xml
<Page x:Class="VoiceType.Uno.Presentation.RecordingPage" xmlns:utu="using:Uno.Toolkit.UI">
  <Grid utu:SafeArea.Insets="Left,Right" RowDefinitions="Auto,Auto,*,Auto">
    <!-- ← title ✦ ⋯ ; в режиме правки: × "Edit transcript" Undo -->
    <Grid Grid.Row="0" utu:SafeArea.Insets="Top" ColumnDefinitions="48,*,Auto" MinHeight="56"> … </Grid>
    <!-- Вкладки: тот же сегментированный RadioButton-стиль, что на ModelsPage; скрыты в режиме правки.
         Явные Grid.Row у всех детей: скрытие вкладок не сдвигает область чтения в Auto-строку -->
    <Grid Grid.Row="1" ColumnDefinitions="*,*,*,*" Margin="16,0,16,8"
          Visibility="{x:Bind ViewModel.AreTabsVisible, Mode=OneWay}"> … Transcript | Chats | Translate | Versions … </Grid>
    <ContentControl Grid.Row="2" Content="{x:Bind ViewModel.CurrentTab, Mode=OneWay}" />   <!-- DataTemplateSelector по вкладке -->
    <!-- Низ: плеер (Transcript/Translate/diff), панель правки (Cancel | Save as new version) или ничего (Chats, список версий) -->
    <Grid Grid.Row="3" utu:SafeArea.Insets="Bottom,SoftInput" Visibility="{x:Bind ViewModel.IsPlayerVisible, Mode=OneWay}"
          RowDefinitions="44,Auto" Padding="16,4,16,10">
      <Slider Grid.Row="0" Minimum="0" Maximum="{x:Bind ViewModel.Player.Duration, Mode=OneWay}"
              Value="{x:Bind ViewModel.Player.Position, Mode=TwoWay}" AutomationProperties.Name="Seek" />
      <Grid Grid.Row="1" ColumnDefinitions="*,Auto,Auto,Auto,*" ColumnSpacing="16">
        <TextBlock Text="{x:Bind ViewModel.Player.TimeText, Mode=OneWay}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
        <Button Grid.Column="1" Width="48" Height="48" AutomationProperties.Name="Back 10 seconds" />
        <Button Grid.Column="2" Width="56" Height="56" CornerRadius="28" Style="{StaticResource AccentButtonStyle}"
                Command="{x:Bind ViewModel.Player.TogglePlayCommand}" AutomationProperties.Name="{x:Bind ViewModel.Player.PlayButtonName, Mode=OneWay}" />
        <Button Grid.Column="3" Width="48" Height="48" AutomationProperties.Name="Forward 10 seconds" />
        <Button Grid.Column="4" HorizontalAlignment="Right" MinWidth="52" Height="44" Content="{x:Bind ViewModel.Player.SpeedText, Mode=OneWay}" />
      </Grid>
    </Grid>
    <Grid Grid.Row="3" utu:SafeArea.Insets="Bottom,SoftInput" ColumnDefinitions="*,Auto,Auto" ColumnSpacing="8" Padding="16,6,16,10"
          Visibility="{x:Bind ViewModel.IsEditing, Mode=OneWay}">
      <TextBlock Text="{x:Bind ViewModel.Edit.ChangedText, Mode=OneWay}" VerticalAlignment="Center" />
      <Button Grid.Column="1" Content="Cancel" MinHeight="44" Command="{x:Bind ViewModel.Edit.CancelCommand}" />
      <Button Grid.Column="2" Content="Save as new version" MinHeight="44" Style="{StaticResource AccentButtonStyle}"
              Command="{x:Bind ViewModel.Edit.SaveCommand}" />
    </Grid>
  </Grid>
</Page>
```

Транскрипт — `ItemsRepeater` (виртуализация: часовая запись — ~600 сегментов); в режиме правки тот же репитер с шаблоном `TextBox` (AcceptsReturn=False, TextWrapping=Wrap). `ChatPage` — та же схема `Auto,Auto,*,Auto`: шапка, источники (чипы или строка-сводка по `MainLayout`), сообщения, поле ввода. `MainLayout.Compute` расширяется состояниями `HistoryWide` (≥ 840 → master-detail у History и Chats), `Short` (< 480 → плеер в строку) и `CompactSources` (< 600 ширины или < 480 высоты → строка-сводка источников) и тестируется так же, как сейчас.

## 14. План реализации

UI-free логика — в `VoiceType.Uno.Core` с тестами в `VoiceType.Uno.Tests` (по AGENTS.md); общая с WinUI — в `SpeechLib`. WinUI в этой итерации не трогаем (решение 8).

| Этап | Что | Проекты | Тесты |
|---|---|---|---|
| **0. Замеры и пробы** (2–3 дня) | WER Parakeet INT4 на CV17 ru/en: WAV vs Opus 24/16 kbps (`tools/WerEval`); RSS и prefill Gemma 4096 vs 2048 на телефоне; встроенный максимум контекста `.litertlm`; trimming и размер APK с `Microsoft.Agents.AI`; **проба Android**: foreground-сервис `dataSync`/`mediaProcessing` для вычислений + `WorkManager` с `RequiresCharging` в net10.0-android; **проба продолжения** перераспознавания с checkpoint (diff против непрерывного прогона) | `tools/`, черновой Android-проект | — |
| **1. Сохранение + список + плеер + хранение** | `AppPaths.ChatsDir/JobsDir/TrashDir`; `SessionStore` (папки, атомарная запись, восстановление `.part`, мягкое удаление в `trash/`), `SessionManifest` schema 2 + JSON source-gen, `SessionTimeline`, `RetentionPolicy` (Forever, лимит), `StorageUsage`, `HistoryIndex` + поиск по `title`/`autoTitle`, `PlaybackCursor`; настройки §5.9 через `SettingsStore` | **Core** | unit: манифест round-trip (в т. ч. чтение schema 1), повреждённые папки, retention (Forever, лимит, задания в очереди не трогаются), поиск (RU/ё), курсор, правило Inject-сохранения |
| | Tee аудио в `RecognitionService` + `OggOpusWriter` (не включается при Inject + `SaveInjectDictations=false`); общий `RecognitionPipeline` из private-классов | `SpeechLib` (pipeline, Opus writer), `VoiceType.Uno` | `SpeechLib.Tests`: pipeline на фейковом распознавателе |
| | `HistoryPage`, `RecordingPage` (Transcript), снэкбар, пункты меню History/Chats/Queue, `IAudioPlaybackSink` ×3, Settings › History, диалог удаления с «Don't ask again» + Undo, Rename (свой заголовок), экспорт `.txt`, Android `dataExtractionRules` | `VoiceType.Uno` | UIA-проверка раскладки |
| **2. Очередь + версии + правка** | `JobStore`, `JobScheduler` (условия, приоритеты, пакеты), `JobRunner`, `IDeviceConditions` ×3, `JobsForegroundService` + `WorkManager`; `OfflineRecognitionJob` с checkpoint; `TranscriptDiff`, `TranscriptEdit.Apply`; листы Re-recognize и пакетный, страница Queue, вкладка Versions + diff, режим правки; экспорт аудио + `.lrc` + `.srt` | Core (scheduler, diff, edit, writers), `VoiceType.Uno` | unit: `JobScheduler.Next` (окно через полночь, зарядка, idle, приоритет, пакет, вытеснение), checkpoint round-trip, diff, перенос таймингов правки, `LrcWriter`/`SrtWriter`; E2E: перераспознавание `sample-0.mp3` даёт ту же версию, что CLI; продолжение после убийства процесса |
| **3. Перевод сохранённого + заголовки** | `GemmaEngineHost` (рефакторинг `LiteRTLmNativeTranslator` на внешний движок), `SavedTranscriptTranslator` и `TitleGenerator` как задания очереди, вкладка Translate, Regenerate в Rename | `SpeechLib.LiteRT.Native`, `SpeechLib`, `SpeechLib.Agents`, `VoiceType.Uno` | unit на фейковом `ITextTranslator`: сегменты ↔ предложения, продолжение partial; заголовок не перезаписывает `title` |
| **4. Чаты** | Core: `ChatStore` (sources, aliases, события, удаление записи), `ContextBudget` для N источников, `Bm25Index` по всем источникам, `CitationParser` (`[S2 12:05]`), `ChatTranscriptPrompt`, `MarkdownChatExporter`; `SpeechLib.Agents`: агент, `ChatSourcesContextProvider`, `SamplerFallbackChatClient`, map-reduce и сводка записи как задания `summarize`; UI: страница Chats, вкладка Chats записи, листы Add recordings / Sources / Add to chat, экспорт Markdown | `SpeechLib.Agents` (новый), Core, `VoiceType.Uno` | unit: бюджет (1 и N источников, квота на источник), нарезка, BM25, цитаты с псевдонимами (в т. ч. удалённый источник), события источников, экспорт Markdown; интеграционный (ручной, нужна модель): сериализация сессии → перезапуск → продолжение; добавление источника посреди беседы |
| **5. Улучшения** | Stateful-чат (`ConversationId`, сброс при смене источников), точные таймкоды TDT (`ITimedUtteranceRecognizer`), расширенный LRC со словами, SQLite FTS5 при росте истории, side-by-side diff, экспорт чата в HTML | `SpeechLib.Providers`, Core | регрессия `E2E_WordTimingsRegressionTests` |
| **6. Спайк конвертации Nemotron 3 Diarization** (3–5 дней, параллельно с этапами 1–2) | Экспорт `preencode.onnx` + `encoder.onnx`, FP32 и INT8 (работа в `tools/converters/NemotronDiarization`); паритет с PyTorch; DER на AMI SDM и своих RU-записях; RTF и RSS на десктопе и телефоне для offline, 1.04 с, 0.64 с, lean-live, delayed-live — отдельно и вместе с потоковым Parakeet INT4; размеры файлов; совпадение `SortformerStreamingUpdater` (v2.1) с Nemotron 3. Выход — критерии §10.2 и решение по живым меткам (§10.7) | `tools/converters/NemotronDiarization`, скрипт замера DER | паритет и DER — скрипты спайка |
| **7. Рантайм `SpeechLib.Diarization` + Core** | `SortformerOnnxDiarizer` (offline + streaming), порт updater-а, `DiarizationPostprocessor`, пресеты; Core: `SpeakerAssigner`, `SpeakerMatcher`, `SpeakerStats`, `SpeakerDirectory`, дельты манифеста (§10.4); каталог: вид модели «диаризация», INT8/FP32 | `SpeechLib.Diarization` (новый), Core, `SpeechLib.ModelDownload` | `SpeechLib.Tests`: состояние кэша против дампа NeMo, постобработка. Core: разметка слов (перекрытие, `-1`, смена спикера внутри сегмента, overrides), сопоставление прогонов (перестановка, новый спикер, merge), round-trip манифеста через старую модель (ExtensionData). E2E: DER на записи с RTTM |
| **8. Фоновая диаризация + Reprocess** | Виды `Diarize`, `Analyze`, цепочки (`chainId`, `dependsOn`), `requireCharging`/`requireIdle`, схема очереди 2; `Speakers.AfterStop`; лист Reprocess (одиночный и пакетный), автопереобработка; UI: спикеры в Transcript, фильтр и плеер по репликам, лист Speakers + Merge, Versions, Insights, Models › Speakers, Settings; имена в чатах и экспорте | Core (scheduler, insights), `SpeechLib.Agents` (`analyze`), `VoiceType.Uno` | unit: `JobScheduler` (зависимости, отмена зависимых, сложение условий, порядок пакета цепочек), переключение текущей версии в конце цепочки, повторное использование прогона, экспорт и цитаты с именами; UIA-раскладка сценариев ревизии 3 |
| **9. Живые метки за флагом** (только если спайк прошёл гейт §10.7) | `LiveDiarizationStage` (tee в `ProcessLoop`), разметка закоммиченных фраз, отставание → пауза и `diarize` после Stop, `Test this device` и `Speakers.LiveBenchmark`, выключатель в MODE, офлайн-прогон после живого с переносом имён | `VoiceType.Uno`, `SpeechLib.Diarization` | интеграционный: часовая запись с живыми метками на десктопе без отставания; на телефоне — пауза меток вместо задержки текста; RTF и CPU в логе |

Этапы 7–9 начинаются после этапа 2 (очередь); спайк 6 не зависит ни от чего и идёт первым.

### Критерии приёмки

- [ ] После Stop запись видна в History меньше чем через 2 с (на 10-минутной записи), снэкбар открывает её.
- [ ] При `SaveInjectDictations = false` сессия, начатая с Inject text, не появляется в History и не оставляет файлов.
- [ ] 1 час речи занимает ≤ 20 МБ аудио в режиме Standard (Opus 48 kbps), ≤ 11 МБ в Compact.
- [ ] Тап по сегменту перематывает с точностью ±0.3 с, подсветка следует за звуком.
- [ ] Удаление спрашивает подтверждение; после «Don't ask again» не спрашивает, но показывает Undo на 8 с; переключатель в Settings возвращает вопрос.
- [ ] `Keep audio = Forever` и `No limit` никогда не удаляют аудио; при лимите удаляется самое старое аудио; текст, версии, переводы и чаты не удаляются автоматически ни при каких настройках.
- [ ] Заголовок от Gemma появляется после сохранения; Rename его заменяет; повторная генерация и смена версии не трогают свой заголовок.
- [ ] Правка транскрипта создаёт версию `edited` с `parent`, родитель побайтно не меняется; тайминги неизменённых слов совпадают с родителем.
- [ ] Чат с тремя записями отвечает со ссылками «запись + время», каждая ведёт в нужную запись и место. Добавление и удаление записи посреди беседы отражается в ленте и в следующем ответе. Беседа после перезапуска продолжается.
- [ ] Удаление записи убирает её из всех чатов, чаты и их сообщения остаются.
- [ ] Перевод на второй язык не удаляет первый; перевод старой версии помечен `outdated`.
- [ ] Перераспознавание другой моделью создаёт новую версию со всеми полями §9.2, diff открывается, «Make current» переключает текст списка и поиска.
- [ ] Пакет из 3 записей с условием «01:00–06:00 + на зарядке» не стартует вне окна или без зарядки, стартует при выполнении условий, идёт по одному заданию; Pause/Resume/Cancel/перестановка работают.
- [ ] Убитое посреди перераспознавания приложение после запуска продолжает с checkpoint, а не с начала.
- [ ] Во время живой записи задание очереди продолжается (десктоп) или продолжается с предупреждением (Android); живой перевод забирает движок Gemma у задания перевода.
- [ ] Экспорт: `.srt` открывается в VLC вместе с аудио, `.lrc` — в музыкальном плеере с синхронным текстом **[какой плеер — выбрать на этапе 2]**, `.txt` содержит `[hh:mm:ss]` на каждую строку, Markdown чата читается без приложения.
- [ ] Ревизия 3. ONNX-диаризация совпадает с PyTorch (критерии §10.2); INT8 хуже FP32 не больше чем на 1 п.п. DER.
- [ ] После Stop запись с тремя собеседниками получает спикеров в фоне. Rename и Merge меняют имена везде (Transcript, Insights, чаты, экспорт), файлы прогона и слоя при этом не переписываются.
- [ ] Новая версия (перераспознанная или правленая) получает спикеров без повторного прогона модели.
- [ ] Versions показывает для каждой версии ASR-модель, а также модель, вариант и режим диаризации.
- [ ] Reprocess «ASR → спикеры → анализ» идёт тремя шагами в Queue, `currentVersion` переключается в конце; упавший анализ не откатывает v4; пакет из трёх записей идёт цепочка за цепочкой.
- [ ] Автопереобработка с условием «01:00–06:00 + на зарядке» не стартует днём и без зарядки.
- [ ] Ссылки в чате показывают имя спикера и ведут в нужное место.
- [ ] Живые метки (если включены): отставание не задерживает текст; при паузе меток после Stop ставится фоновая диаризация; на телефоне выключатель недоступен без пройденного теста устройства.
- [ ] Все сценарии §12.2 проходят и на приложении (UIA).

## 15. Риски

| Риск | Смягчение |
|---|---|
| Opus ухудшает WER перераспознавания | Замерено: 48 kbps даёт +0.23 п.п.; режим High (WAV) — без потерь |
| Один движок LiteRT: живой перевод, чат и задания конкурируют | `GemmaEngineHost` с тремя уровнями приоритета; задания вытесняются с checkpoint; чат ждёт с понятным статусом |
| Нехватка памяти на телефоне (Gemma ~1.7 ГБ + ASR), особенно задание + живая запись | Одно тяжёлое задание, проверка свободной RAM, пауза ASR-задания на время записи при малой памяти |
| Отказ сэмплера на CPU-бэкенде (`Greedy`/`TopK`, `UNIMPLEMENTED: Sampler type: N`) | В чате без явного сэмплера + повтор на новом разговоре; в 1.3.0 `NearGreedy` как поддерживаемый профиль (§7.6) |
| Медленный prefill в stateless-чате, особенно с N источниками | Квоты бюджета, кэш сводок записи (общий для всех чатов); stateful на этапе 5 |
| Gemma E2B путает записи в чате с несколькими источниками | Короткие псевдонимы `[S2 mm:ss]`, проверка цитат по источнику, квота фрагментов на источник, предупреждение после 8 источников |
| Gemma E2B галлюцинирует по длинным записям | Ответ только из контекста, обязательные ссылки, проверка цитат, видимая пометка «summarised in N parts» |
| Android ограничивает фоновые вычисления (типы FGS, дневные лимиты, запуск из фона) | Проба на этапе 0; checkpoint позволяет продолжать по частям; честные подписи условий «Runs when VoiceType is open», если `WorkManager` не взлетит |
| Продолжение с checkpoint даёт другой текст на стыке | Граница — только закоммиченная фраза + 2 с контекста; замер на этапе 0; при плохом результате — перезапуск задания с начала (как в ревизии 1) |
| API MAF меняется (уже было: thread → session) | `display` хранится отдельно от `session`; адаптер `ITranscriptChat`; версия MAF зафиксирована |
| `LiteRtLmSharp.Extensions.AI` не trim-clean | Проверка на этапе 0; при проблемах — запасной путь §7.8 или `TrimmerRootAssembly` |
| `MediaPlayerElement` / аудиовыход на Skia | Свой `IAudioPlaybackSink` на трёх платформах |
| Пользователь отключил подтверждение и удалил не то | Мягкое удаление с Undo 8 с; «Delete all history» спрашивает всегда |
| Записи и чаты уходят в облачный бэкап Android | Исключение `sessions/`, `chats/`, `jobs/`, `trash/` в `dataExtractionRules` |
| Запись чужой речи без согласия | Однократное уведомление, всё локально, экспорт только явно |
| ONNX-экспорт Sortformer не сходится (динамические оси, RoPE, состояние кэша) или INT8 заметно хуже | Спайк до любых UI-работ; кэш вне графа, на C# (порт уже есть); FP32 как запасной вариант; крайний случай — NeMo-Speech.cpp (GGUF q8_0, 107 MB) через P/Invoke |
| CPU телефона: живые метки вместе с потоковым ASR не укладываются в реальное время | По оценке §10.7 потоковые пресеты карточки в 30–90 раз дороже офлайна. Гейт по спайку и тесту устройства, режим Delayed (~10 с), на телефонах по умолчанию выключено, при отставании — пауза меток и фоновая диаризация после Stop |
| Перекрывающаяся речь: один поток ASR не разделяет голоса | Слово отдаётся доминирующему спикеру, перекрытие помечается; разделение (multitalker / speaker-masked ASR) — вне итерации |
| Больше 8 голосов | Девятый голос попадает в чужой канал; подсказка в листе Speakers, когда заняты все 8 каналов; ручной Change speaker |
| Номера спикеров по порядку появления не совпадают между прогонами (живой → офлайн, другая модель) | `SpeakerMatcher` по совместному времени (≥ 60 %); люди в манифесте отделены от каналов; подпись «Names carried over…» |
| Разметка слов по оценочным таймингам ошибается на границах реплик | Канал большинства на сегмент, ближайшая реплика в пределах 0.5 с, точные тайминги TDT (этап 5), ручной Change speaker |
| Качество на русском в карточке не заявлено | Замер DER на своих RU-записях в спайке; при плохом результате — пометка в Models › Speakers |
| Gemma путает или придумывает имена | В контексте только имена от пользователя или «Speaker N»; подсказки имён (`nameHints`) — предложение с цитатой, применяется вручную |
| Старое приложение перезапишет очередь и потеряет виды `diarize`/`analyze` (`LenientEnumConverter` пишет `unknown`) | `JobQueueDocument` schema 2: более старое приложение открывает очередь только на чтение |
| Размер модели (≈ 110–400 MB) поверх ASR и Gemma | Скачивание по запросу; INT8 по умолчанию на телефоне; диаризация необязательна |

## 16. Решения

Ответы пользователя на открытые вопросы ревизии 1 (2026-10-12). Они перекрывают прежние предложения дизайна там, где расходятся.

| # | Вопрос | Решение | Где в дизайне |
|---|---|---|---|
| 1 | Сохранять историю по умолчанию? | **Да.** Перед удалением — предупреждение с флажком **«Don't ask again»**. Срок хранения можно **продлить** в настройках | Диалог удаления и мягкое удаление с Undo — §4.7; `History.ConfirmDelete`, `History.AudioRetentionDays` (7/30/90/180/365/Forever), `History.MaxAudioStorageMb` — §5.9, §6.4; текст, версии, переводы, чаты не удаляются автоматически |
| 2 | Сохранять короткие диктовки Inject? | **Да, но с настройкой, чтобы исключить** | `History.SaveInjectDictations`; `manifest.mode` — §5.2, §5.7; решение по режиму на старте сессии — §6.2; фильтр и чип `Inject` — §4.2 |
| 3 | Редактировать транскрипт в истории? | **Да, с версиями** | Режим правки — §4.3; версия `edited` с `parent`, перенос таймингов — §5.2, §5.3, §9.4 |
| 4 | Заголовок от Gemma? | **Да, но можно переименовать вручную** | `title` + `autoTitle` — §5.2, §5.7; лист Rename с Regenerate — §4.7; задание `autoTitle` — §9.5 |
| 5 | Несколько чатов на запись? | **Несколько; записи свободно добавляются в чат** | Чаты — сущность верхнего уровня `<DataRoot>/chats/<id>/chat.json` с `sources` — §5.5; страница Chats и вкладка Chats записи — §4.3, §4.4; контекст из нескольких записей, ссылки «запись + время», смена источников посреди беседы — §7.4 |
| 6 | Перераспознавание во время живой записи? | **Да, плюс пакеты и планировщик** | `queue.json` — §5.6; планировщик, условия Now/WhenIdle/WhenCharging/AtTime, одно тяжёлое задание, параллельно с записью, продолжение после убийства, Android-сервис — §9.5; страница Queue и пакетный лист — §4.5, §4.2 |
| 7 | Какие форматы экспорта первыми? | **(a) чат как документ, (b) аудио с текстом для обычного плеера, (c) текст с таймкодами** | Markdown (+HTML), аудио + `.lrc` + `.srt`, `.txt` `[hh:mm:ss]`, лист Export и системные Share/Save — §6.6 |
| 8 | WinUI тоже? | **Не сейчас** | Только Uno; формат папок остаётся переносимым — §1, §14 |
| 9 | Диаризация? (ревизия 3) | **Офлайн, на устройстве, моделью nvidia/Nemotron-3-Diarization, сконвертированной в ONNX** | Модель и пресеты — §10.1; спайк конвертации — §10.2; `SpeechLib.Diarization` — §10.3; прогоны, слои и люди (слой, а не новая версия) — §10.4; UI и Models › Speakers — §10.8 |
| 10 | Фоновая переобработка? (ревизия 3) | **Да, отдельной функцией: новая версия, затем диаризация и анализ** | Цепочка Reprocess, виды `diarize`/`analyze`, `chainId`/`dependsOn`, расписание и пакеты из той же очереди, автопереобработка новых записей — §11 |
| 11 | Диаризация в реальном времени? (ревизия 3) | **Если тесты позволят — в живой конвейер, с возможностью выключить** | Гейт по спайку и тесту устройства, режимы Fast/Delayed, выключатель в MODE (на телефоне по умолчанию выключен), отставание → фоновая диаризация после Stop — §10.7 |

## 17. Что не проверено

- Компиляция и поведение MAF 1.24.0 с `LiteRtChatClient`: API взят из XML-документации пакетов, сэмпла MAF в LiteRtLmSharp нет.
- Передаёт ли `LiteRtChatClient` `ChatOptions.Instructions` (свойство есть в M.E.AI 10.10.1) в системное сообщение LiteRT-LM или его нужно класть отдельным `ChatRole.System`-сообщением.
- Встроенный максимум контекста `gemma-4-E2B-it.litertlm`, prefill-скорость и RSS на 4096/8192 на телефоне. Цифры Moto G100 из docs/android.md сняты на нативе v0.18 (не на 1.2.0).
- Качество ответов Gemma 4 E2B по нескольким записям: держится ли она псевдонимов `[S2 mm:ss]`, путает ли записи; порог «8 источников» взят с потолка.
- `MediaPlayerElement` на Skia desktop в Uno 6.7.30; качество time-stretch на 2×.
- Скорость кодирования Opus на телефоне (на ПК ~75× real-time; ожидается 15–25×), воспроизведение `.ogg` встроенными плеерами Windows/Android.
- Совпадение результата перераспознавания с остановкой и продолжением с checkpoint и без остановки.
- Android 14/15: подходящий тип foreground-сервиса для локальных ASR/LLM-вычислений (`dataSync` / `mediaProcessing`), их дневные лимиты и требования Play; биндинг `WorkManager` в net10.0-android и запуск foreground-работы из воркера.
- Определение зарядки и «простоя» на Windows и Linux из Uno (P/Invoke `GetSystemPowerStatus`, `/sys/class/power_supply`); поведение на устройстве без батареи.
- Экспорт: `FolderPicker` на Skia desktop, `DataTransferManager` (Share) на WinUI-голове Uno 6.7.30, `ACTION_OPEN_DOCUMENT_TREE` из Uno на Android; какие плееры воспроизводят Ogg Opus без кодеков и понимают `.lrc` (включая расширенный со словами); MP3-кодировщик вне Windows отсутствует.
- Перенос таймингов в правленую версию: качество интерполяции на заметно переписанных сегментах.
- Все оценки времени (map-фаза, сводки для N записей, перераспознавание, prefill).
- Ревизия 3, спикеры:
  - экспорт Nemotron 3 Diarization в ONNX (две части графа, динамические оси), паритет FP32 и качество INT8; итоговые размеры файлов (сейчас это оценки ≈ 110 / 400 MB по GGUF q8_0 и safetensors);
  - совпадение `SortformerStreamingUpdater` из `tools/converters/SortformerStreaming` (Sortformer v2.1) с логикой кэша Nemotron 3; параметры mel-признаков и пригодность существующего C#-фичеризатора;
  - доступность `Microsoft.ML.OnnxRuntime` (`InferenceSession`) транзитивно через `Microsoft.ML.OnnxRuntimeGenAI` на всех трёх головах;
  - RTF и RSS диаризации на CPU (десктоп, телефон класса Moto G100) для всех пресетов и вместе с потоковым ASR; оценка FLOPs в §10.7 — порядок величины, не замер;
  - DER своих пресетов lean-live и delayed-live (в карточке их нет);
  - качество на русском (в данных обучения и оценки русского отдельно нет);
  - точность разметки слов при оценочных таймингах Parakeet; порог 60 % в `SpeakerMatcher` и 3 % для подсказки Merge взяты без замера;
  - держит ли Gemma 4 E2B имена спикеров в ответах, качество `nameHints`;
  - вид модели «диаризация» в `ModelCatalog` / `ModelDownloadManager` и репозиторий с ONNX-вариантами для скачивания.
