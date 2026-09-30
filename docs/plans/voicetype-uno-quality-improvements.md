# VoiceType.Uno — план улучшений качества кода

Источник: ревью `apps/VoiceType.Uno` от 2026-09-30 (режим .NET Clean Code).
Область: только UNO-приложение; WinUI/консольные приложения не затрагиваются.

## Диагноз

Приложение функционально работает (проверено: build `net10.0-windows10.0.26100`
и `net10.0-desktop`, запуск в WSLg, 3 сессии распознавания без ошибок), но
накопились структурные и конкурентные дефекты: настройки пишутся из четырёх
потоков, часть UI-состояния обновляется вне UI-потока, `MainViewModel`
превратился в Large Class, у приложения нет ни одного теста.

## Находки и решения

| # | Важность | Запах | Где | Решение |
|---|---|---|---|---|
| 1 | HIGH | Data race / потеря обновлений | `MainViewModel._settings` мутируется из UI-потока, `EnsureModelReadyAsync`, continuation загрузки и `ApplySettingsAsync`; параллельно `SettingsService.Update` (read-modify-write) и полный `Save(newSettings)` | Новый `SettingsStore`: единственный владелец in-memory `AppSettings`, все записи — через один `SemaphoreSlim` |
| 2 | HIGH | Обновление UI вне UI-потока | `MainViewModel.ApplySettingsSnapshot` вызывается после `ConfigureAwait(false)` | Все обновления состояния — через `IUiScheduler.Post` |
| 3 | MEDIUM-HIGH | Large Class + Divergent Change | `MainViewModel` (~900 строк: транскрипт, модель, загрузки, хоткеи, трей, перевод, настройки) | Extract Class: `TranscriptCoordinator`, `SettingsStore`, `DownloadProgressFormatter` |
| 4 | MEDIUM | Temporary Field, три механизма одной задачи | `_settingsApplyGate`, `_settingsApplyVersion`, `_isApplyingSettingsSnapshot` | Версионирование и подавление эхо-записи переехали в `SettingsStore`/координатор |
| 5 | MEDIUM | Duplicate Code + проглоченные исключения | 4× `_settings.X = value; _ = Task.Run(() => _settingsService.Update(...))`; fire-and-forget `_tray.InitializeAsync()`, `InitializeHotkeysAsync()` | `SettingsStore.Post(mutate, onError)` с единым обработчиком ошибок; `AsyncGuard` для фоновых задач |
| 6 | MEDIUM | Sync-over-async | `RecognitionService.Dispose()` → `DisposeAsync().AsTask().GetAwaiter().GetResult()` | `Dispose()` только сигнализирует остановку, очистка — в фоне; `IAsyncDisposable` для детерминированного пути |
| 7 | MEDIUM | Switch on type, дублирование ветвей | `RecognitionService.ProcessLoop` — три проверки `is IUtteranceStreamingRecognizer` + `_recognizer!` | `IRecognitionPipeline` (`UtterancePipeline` / `PlainPipeline`), выбор один раз при загрузке модели |
| 8 | MEDIUM | Нет тестов | у `VoiceType.Uno` нет тестового проекта | `VoiceType.Uno.Core` (net10.0, чистая логика) + `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests` |
| 9 | LOW | Аллокации в горячем пути | `PulseMixAudioSource.Drain` (`List<float>` + `AddRange` каждые ~20 мс), `Mix` (новый массив) | Посэмпловый микс без промежуточного списка; чистая математика вынесена в `AudioMixdown` и покрыта тестами |
| 10 | LOW | Пустой `Dispose()` против контракта интерфейса | `PulseAudioSource` / `PulseMixAudioSource` | Явный контракт владения в XML-документации: нативный поток принадлежит потоку захвата |

## Структура после изменений

```
apps/VoiceType.Uno/
  src/
    VoiceType.Uno.Core/            ← net10.0, без UI: тестируемая логика
      Services/AppSettings.cs
      Services/AppPaths.cs
      Services/SettingsService.cs
      Services/SettingsStore.cs          (новый)
      Services/ModelPathResolver.cs
      Services/DownloadProgressFormatter.cs (новый)
      Services/IUiScheduler.cs           (новый)
      Services/TranscriptCoordinator.cs  (новый)
      Services/Audio/AudioMixdown.cs     (новый)
    VoiceType.Uno/                 ← приложение (Uno.Sdk), ссылается на Core
      Services/DispatcherQueueUiScheduler.cs (новый)
  tests/
    VoiceType.Uno.Tests/           ← net10.0, xUnit
```

`IUiScheduler` разрывает зависимость Core от `Microsoft.UI.Dispatching`
(DIP); в приложении его реализует `DispatcherQueueUiScheduler`.

## Порядок работ

1. `VoiceType.Uno.Core` + перенос `AppSettings`, `AppPaths`, `SettingsService`, `ModelPathResolver`.
2. `SettingsStore` — единственный писатель настроек.
3. `TranscriptCoordinator` — троттлинг частичных результатов, ручной ввод,
   продолжение после существующего текста, без UI-типов.
4. `MainViewModel` — перевод на `IUiScheduler` + `SettingsStore` + координатор;
   удаление трёх механизмов сериализации.
5. `RecognitionService` — `IRecognitionPipeline`, неблокирующий `Dispose`.
6. PulseAudio — микс без промежуточных аллокаций, явный контракт владения.
7. Тесты на `ModelPathResolver`, `SettingsStore`, `TranscriptCoordinator`,
   `DownloadProgressFormatter`, `AudioMixdown`.

## Проверка

```powershell
# Core + тесты
dotnet test apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/VoiceType.Uno.Tests.csproj

# Приложение (Windows-голова)
dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -f net10.0-windows10.0.26100 -c Debug

# Приложение (Skia-голова / Linux)
dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -f net10.0-desktop -c Debug
```

Функциональная регрессия — запуск в WSLg по `launch-linux.sh` (модель, три
сессии распознавания, переключение языка без перезагрузки модели).

## Вне области этого плана (продуктовый backlog)

- Паритет диалога настроек с WinUI: сессии, постобработка, параметры декодирования, микшер.
- Linux: реальный always-on-top; документирование ограничений инъекции текста (X11 / Wayland / `ydotool`).
- Android: Mic-only — показать ограничение в UI, а не только в комментарии композиционного корня.

## Статус выполнения

Сделано (находки 1–10):

| # | Что сделано |
|---|---|
| 1 | `SettingsStore` — единственный владелец `AppSettings` и единственный писатель `settings.json`; все мутации (UI, инициализация модели, continuations загрузок, диалог) идут через него |
| 2 | `IUiScheduler` + `DispatcherQueueUiScheduler`; проекция снимка настроек и все обновления состояния выполняются на UI-потоке |
| 3 | Из `MainViewModel` извлечены `TranscriptCoordinator`, `SettingsStore`, `DownloadProgressFormatter`; UI-независимая логика перенесена в проект `VoiceType.Uno.Core` |
| 4 | Версионирование применений настроек и подавление эхо-записи (`ProjectSettingsSnapshot`) — два явно документированных механизма вместо трёх смешанных |
| 5 | Единый `Persist(...)` + `SettingsStore.Post(mutate, onError)` и `RunInBackgroundAsync` — fire-and-forget больше не теряет исключения |
| 6 | `RecognitionService.Dispose()` только сигнализирует остановку; `IAsyncDisposable` остаётся детерминированным путём |
| 7 | `IRecognitionPipeline` (`UtterancePipeline` / `PlainPipeline`) — одна ветка вместо трёх проверок типа распознавателя |
| 8 | `VoiceType.Uno.Core` (net10.0) + `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests` (xUnit, 45 тестов) |
| 9 | `AudioMixdown.Average` — посэмпловый микс без промежуточного `List<float>`; покрыт тестами |
| 10 | Контракт владения нативного потока задокументирован в `PulseAudioSource` / `PulseMixAudioSource` |

Осталось (осознанно вне этого изменения):

- `MainViewModel` (≈870 строк) по-прежнему держит жизненный цикл модели, горячие
  клавиши, трей и перевод. Следующий кандидат — `ModelLifecycleCoordinator`
  (методы `EnsureModelReadyAsync` / `ReloadModelAsync` / `RestartCaptureAsync` /
  баннеры и очередь загрузок), но вынос требует отдельной проверки в WSLg,
  потому что там сосредоточена версионная логика применения настроек.
- `_isProjectingSettingsSnapshot` остаётся полем-флагом: полноценный вариант —
  `IDisposable`-скоуп, передаваемый в сеттеры явно.

