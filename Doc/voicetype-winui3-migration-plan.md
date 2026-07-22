# План миграции VoiceType: WPF → WinUI 3

**Ветка:** `feature/voicetype-winui3-migration`
**Дата:** 2026-07-22
**Статус:** план (код не меняем)

## Цель

Перевести десктоп-приложение диктовки `VoiceType` с WPF (`UseWPF=true`) на WinUI 3 (Windows App SDK), сохранив функциональность: запись аудио, распознавание через NemotronSpeech, глобальные горячие клавиши, инъекцию текста, загрузку моделей, настройки.

## Объём работ

### Что мигрирует
| Компонент | Файлы |
|---|---|
| Окна | `Views/MainWindow`, `Views/SettingsWindow`, `Views/ModelDownloaderWindow` |
| ViewModels | `MainViewModel`, `SettingsViewModel`, `ModelDownloaderViewModel`, `Commands.cs` |
| Сервисы | `Services/*` (10 файлов) |
| Модели | `Models/*` (3 файла) |
| Ресурсы | `Resources/microphone.ico`, стили |

### Что НЕ мигрирует (переиспользуется как есть)
- `SpeechLib` — аудио-пайплайн, транскрайбер
- `NemotronSpeech` — ONNX Runtime GenAI
- `VoiceType.Tests` — частично (см. этап 6)

## Этапы

### Этап 0 — Подготовка
- [ ] Установить Windows App SDK (WASDK 1.6+) и шаблоны WinUI 3
- [ ] Зафиксировать целевую платформу: `net10.0-windows10.0.19041.0`, `WindowsPackageType` (решить: MSIX-пакет или unpackaged self-contained)
- [ ] Решить: **новый проект `VoiceType.WinUI`** рядом со старым (рекомендуется — старый WPF остаётся рабочим для сравнения) vs. миграция на месте

### Этап 1 — Скелет проекта
- [ ] Создать `VoiceType.WinUI.csproj` (Blank App, Packaged / или single-project MSIX)
- [ ] Добавить `ProjectReference` на `SpeechLib` и `NemotronSpeech`
- [ ] Перенести пакеты: `NAudio`, `NAudio.Lame` (совместимы, ничего WPF-специфичного нет)
- [ ] Заменить `Microsoft.Xaml.Behaviors.Wpf` → `Microsoft.Xaml.Behaviors.WinUI.Managed`
- [ ] Добавить проект в `NemotronSpeech.slnx`

### Этап 2 — Сервисы (без UI-зависимостей)
Переносятся почти без изменений (не зависят от WPF):
- [ ] `AudioRecorderService` (NAudio)
- [ ] `RecognitionService`, `PostProcessingPipeline`, `SessionManager`
- [ ] `ModelDownloaderService`, `SettingsService`, `AppPaths`

Требуют адаптации (Win32 interop — должны работать, но проверить HWND):
- [ ] `GlobalHotkeyService`, `GlobalInputHook` — RegisterHotKey/hook'и: в WinUI 3 получать HWND через `WinRT.Interop.WindowNative.GetWindowHandle(window)`
- [ ] `TextInjector` — **ОБЯЗАТЕЛЬНАЯ функциональность** (см. раздел «Требование: печать в других приложениях»). `SendInput` переносится без изменений (runFullTrust); `System.Windows.Clipboard` → заменить на WinRT `Windows.ApplicationModel.DataTransfer.Clipboard` или Win32 clipboard API
- [ ] `FolderBrowser` — заменить на `Windows.Storage.Pickers.FolderPicker` + `WinRT.Interop.InitializeWithWindow`

### Этап 3 — ViewModels
- [ ] Перенести `MainViewModel`, `SettingsViewModel`, `ModelDownloaderViewModel` без изменений логики (ручной `INotifyPropertyChanged` — совместим)
- [ ] **Критично:** заменить `Application.Current.Dispatcher.Invoke()` → `Microsoft.UI.Dispatching.DispatcherQueue.TryEnqueue()` (у WinUI `Application.Current.Dispatcher` отсутствует)
- [ ] `RelayCommand`/`AsyncRelayCommand` — перенести как есть; сохранить известный pitfall: `AsyncRelayCommand` глотает исключения → try/catch в VM с установкой статуса

### Этап 4 — XAML / Views
- [ ] `MainWindow`: WPF `Window` → `Microsoft.UI.Xaml.Window`; обновить namespace'ы (`http://schemas.microsoft.com/winfx/2006/xaml/presentation` остаётся, но контролы из `Microsoft.UI.Xaml.Controls`)
- [ ] Перевести стили/темы на Fluent (WinUI даёт из коробки)
- [ ] **Сохранить правило из AGENTS.md:** computed-свойства без setter → `Mode=OneWay` (в WinUI биндинги по умолчанию OneWay, но для надёжности — проверить `x:Bind` vs `Binding`; рекомендуется `x:Bind` с `Mode=OneWay`)
- [ ] Tray icon: WPF-вариант (если есть NotifyIcon) → Win32 `Shell_NotifyIcon` или CommunityToolkit `TaskbarIcon` (проверить наличие в текущем коде)
- [ ] `SettingsWindow`, `ModelDownloaderWindow` → либо отдельные `Window`, либо `ContentDialog` (WinUI-идиома)
- [ ] Иконка приложения: `Resources/microphone.ico` + ассеты MSIX-манифеста (если packaged)

### Этап 5 — Интеграция и ручная проверка
- [ ] Горячие клавиши работают глобально (в т.ч. когда окно не в фокусе)
- [ ] Запись → распознавание → инъекция текста в стороннее приложение (Notepad, браузер)
- [ ] Загрузка моделей с прогрессом
- [ ] Настройки сохраняются/загружаются
- [ ] Проверить MSIX vs unpackaged: инъекция текста и global hooks могут требовать unpackaged или `runFullTrust` capability

### Этап 6 — Тесты
- [ ] `VoiceType.Tests` остаётся на WPF-версии до конца миграции
- [ ] Создать `VoiceType.WinUI.Tests` (xUnit, `net10.0-windows10.0.19041.0`, **без** `UseWinUI` — как и сейчас с WPF, тесты ссылаются на проект транзитивно)
- [ ] Перенести unit-тесты VM (`Unit_ModelDownloaderViewModelTests`, `Unit_WordTimingsTests`)
- [ ] E2E-тесты без изменений (не зависят от UI)

### Этап 7 — Финализация
- [ ] Обновить `VoiceType/README.md` и корневой `README.md`
- [ ] Обновить `AGENTS.md` (File Map, pitfalls: DispatcherQueue, x:Bind)
- [ ] Решить судьбу WPF-проекта: удалить / оставить legacy / архивировать
- [ ] PR + ревью

## Риски

| Риск | Митигация |
|---|---|
| Global hotkey + text injection под MSIX (appContainer) | Собирать unpackaged (`WindowsPackageType=None`) или `runFullTrust` |
| `Dispatcher` → `DispatcherQueue` миграция во всех VM | Централизованный хелпер `UIThread.Run(Action)` |
| Tray icon API отличается | Win32 P/Invoke или CommunityToolkit |
| Регрессии в bindable-поведении (`Run.Text`, DataContext в DataTemplate) | `x:Bind` с явным `x:DataType` |
| ORT GenAI native DLL в MSIX-упаковке | Проверить копирование native assets; при проблемах — unpackaged |

## Решения, подтверждённые пользователем (2026-07-22)

1. ✅ **Новый проект `VoiceType.WinUI` рядом** со старым WPF-проектом
2. ✅ **MSIX packaged с `runFullTrust` capability** — требуются: инсталлятор с автообновлением + публикация в Microsoft Store. MSIX даёт оба из коробки. `runFullTrust` разрешает RegisterHotKey/SendInput/SetWindowsHookEx.
3. ✅ **Минимальная версия Windows:** `10.0.19041.0` (Windows 10 2004)
4. ✅ **Глобальные горячие клавиши — обязательны** (не опциональны)
5. ✅ **Инъекция текста в другие приложения — обязательна** (`TextInjector` печатает в любом сфокусированном окне: Notepad, браузеры, мессенджеры, Office)

## Требование: печать в других приложениях (ОБЯЗАТЕЛЬНО)

`TextInjector` — ядро функциональности диктовки. Реализация (`VoiceType/Services/TextInjector.cs`):

| Метод | Механизм | Когда |
|---|---|---|
| `SendInput` (основной) | `SendInput` + `KEYEVENTF_UNICODE` — посимвольная Unicode-печать | Кириллица, любые спецсимволы, большинство приложений |
| Clipboard (fallback) | `SetForegroundWindow` + вставка через буфер обмена с сохранением/восстановлением | Приложения, блокирующие SendInput (некоторые терминалы, RDP) |

**Требования для WinUI 3 миграции:**
- `runFullTrust` в манифесте (уже есть) — `SendInput`, `SetForegroundWindow`, clipboard API работают из full-trust MSIX
- ⚠️ **`System.Windows.Clipboard` (WPF) недоступен** в WinUI 3 → заменить на `Windows.ApplicationModel.DataTransfer.Clipboard` (WinRT) или Win32 `OpenClipboard`/`SetClipboardData`
- Clipboard fallback требует STA/UI-thread контекста — адаптировать под `DispatcherQueue`
- Матрица ручного тестирования (этап 5): Notepad, VS Code, Chrome/Edge (адресная строка + текстовое поле), Telegram/WhatsApp Desktop, Word, терминал
- Известные ограничения (не блокеры, задокументировать): приложения от администратора (UIPI — нужен запуск VoiceType от админа), RDP-сессии, некоторые Electron-приложения с clipboard fallback

## Требования к установке и обновлению

| Требование | Реализация |
|---|---|
| Инсталлятор | MSIX single-project (`WindowsPackageType=None` НЕ используем; пакетный режим) |
| Автообновление | App Installer file (`.appinstaller`) с `UpdateSettings` + `PackageManager.CheckUpdateAvailabilityAsync` в приложении |
| Microsoft Store | MSIX-пакет готов к публикации; Partner Center приёмка с `runFullTrust` стандартна для desktop-приложений |
| Сайдлоад | `.msixbundle` + сертификат для установки вне Store |

## Следующий шаг

Этап 1 — создание проекта `VoiceType.WinUI` (шаблон WinUI Blank App, single-project MSIX, `runFullTrust` в манифесте).

## Этап 1 — выполнено (2026-07-22)

Создан проект `VoiceType.WinUI`:
- Шаблон `winui` (WinUI Blank App, single-project MSIX), Windows App SDK **2.3.1**
- `TargetFramework`: `net10.0-windows10.0.26100.0`, `TargetPlatformMinVersion`: **10.0.19041.0**
- `Package.appxmanifest`: `runFullTrust` уже присутствует + `systemAIModels`; MinVersion обновлён до 19041
- Добавлены `ProjectReference` на `SpeechLib` и `NemotronSpeech`
- Пакеты: `NAudio 2.3.0`, `NAudio.Lame 2.1.0`, `Microsoft.Xaml.Behaviors.WinUI.Managed 3.0.0`
- Добавлен в `NemotronSpeech.slnx`
- ✅ Сборка `dotnet build` успешна

### Решённый конфликт: дублирование `onnxruntime.dll`

WASDK 2.3.1 транзитивно тянет `Microsoft.Windows.AI.MachineLearning 2.1.74` (Windows AI / Phi Silica), который несёт собственный нативный `onnxruntime.dll` → конфликт с ORT из `Microsoft.ML.OnnxRuntimeGenAI.Cuda` (NemotronSpeech) при MSIX-упаковке (`APPX1101`).

**Фикс** в `VoiceType.WinUI.csproj`:
```xml
<PackageReference Include="Microsoft.Windows.AI.MachineLearning" Version="2.1.74" ExcludeAssets="native" />
```

## Открытые задачи этапа 1

- [ ] MSIX-пакет Release-сборки (`dotnet publish` / `msbuild -t:Publish`) + `.appinstaller` для автообновлений
- [ ] Замена placeholder-ассетов (`Assets/*.png`, `AppIcon.ico` → `microphone.ico` из VoiceType)
- [ ] Publisher/Identity в манифесте (`CN=AppPublisher` → реальный для Store/сайдлоада)
- [ ] Перенос сервисов/VM/Views (этапы 2–4)
