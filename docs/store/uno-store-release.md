# VoiceType 2.0 в Microsoft Store: Uno (WinAppSDK) вместо WinUI

**Статус (2026-10-28):** пакет собран, миграция проверена на установленном preview-пакете, листинг и скриншоты готовы.
В Partner Center (отправка 9, черновик): пакет 2.0.0.0 загружен и прошёл проверку, пакеты 1.2.12.0 и 1.3.1.0 удалены, листинг
на 17 языков импортирован. Не сделано: WACK, «Параметры отправки» (обоснование `runFullTrust`) и «Отправить для сертификации».

**Статус (2026-11-04):** отправка 9 (2.0.0.0, сборка 28.10 без локализации) опубликована. Отправка 10 ушла на сертификацию:
пакет **2.0.1.0** (интерфейс на 16 языках, пакет объявляет все 16 языков, поэтому Partner Center добавил 13 листингов `ar`, `de`, …
к старым `*-xx`), листинги из свежего экспорта (`make-listing-v2.0.0.py <export.csv>` заполняет новые колонки текстом своего
языка). Свои скриншоты сняты для en, ru, de, fr, nl, pl (6 штук, без «Модели»); остальные языки пока получают английские, их
досъёмка (`tools/store-screenshots/shoot-all.ps1`) и импорт — со следующей отправкой.

Приложение в Store (`9NRQX69XCLPT`, identity `DimQ1.VoiceType`, последняя отправка WinUI: 1.3.4.0) получает
**обновление** 2.0.0.0. Это WinAppSDK-голова Uno-приложения (`net11.0-windows10.0.26100`), а не новое приложение рядом
со старым. Identity та же, поэтому у пользователей сохраняются установка, отзывы и рейтинг, а главное, та же папка
данных: новая версия находит скачанные модели там, где их оставила WinUI, и ничего не копирует.

## 1. Что в репозитории

| Файл | Что |
|---|---|
| `apps/VoiceType.Uno/src/VoiceType.Uno/Packaging/Windows/Package.appxmanifest` | Манифест пакета: identity WinUI, `runFullTrust` + `internetClient` (`systemAIModels` убран, Uno не использует Windows AI API). Логотипы и заставку генерирует Uno.Resizetizer из `Assets/Icons/icon.svg` |
| `VoiceType.Uno.csproj`, свойство `VoiceTypeMsix=true` | Включает MSIX только для этой сборки. Без него WinAppSDK-голова остаётся распакованной: F5, проверки паритета и CI не меняются |
| `apps/VoiceType.Uno/src/VoiceType.Uno/build-store-msix.ps1` | Сборка пакета для Store, preview-пакет, локальная установка |
| `VoiceType.Uno.Core/Services/Settings/WinUiDataImport.cs` | Перенос данных WinUI при первом запуске (раздел 3) |
| `VoiceType.Uno.Core/Services/PackageIdentity.cs`, `AppPaths.DefaultDataRoot` | В пакете корень данных — настоящая папка `Packages\<family>\LocalCache\Local\VoiceType` (как у WinUI), поэтому пути в «Настройках» открываются в Проводнике |
| `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/Settings/WinUiDataImportTests.cs` | 14 тестов переноса |
| `docs/store/imports/v2.0.0/` + `make-listing-v2.0.0.py` | Папка для импорта (CSV и `screenshots/`, больше ничего) и генератор CSV с текстами на 16 языках |
| `tools/store-screenshots/` | Как сделаны скриншоты (раздел 6) |

## 2. Сборка

```powershell
cd apps\VoiceType.Uno\src\VoiceType.Uno
.\build-store-msix.ps1 -Version 2.0.0            # для Store: bin\StorePackages\DimQ1.VoiceType_2.0.0.0_x64_unsigned.msix
.\build-store-msix.ps1 -Version 2.0.0 -Preview -Install
    # локальная проверка: identity DimQ1.VoiceType.UnoPreview («VoiceType Preview»), подписан dev-сертификатом,
    # ставится РЯДОМ с WinUI, своя папка данных
```

- Версия: `MAJOR.MINOR.PATCH`, скрипт дописывает `.0` (Store требует 0 в четвёртой части) и отказывается собирать версию
  не выше 1.3.4.0. Она же уходит в `ApplicationDisplayVersion` (окно «О программе», метаданные History).
- Пакет self-contained (.NET 11 + WinAppSDK внутри), x64, 98 МБ с Uno 7 / WinAppSDK 2.5 (было 116 МБ), без `.msixupload`. С WinAppSDK 2.x пакет появляется в `AppPackages\` рядом с проектом, а не в `bin\Release\...\AppPackages\`; скрипт ищет в обоих местах. Partner Center принимает `.msix`
  и сам его подписывает.
- `-Sign -Install -Force` ставит **настоящую** identity поверх локально установленной WinUI. Это и есть обновление
  из Store на этой машине, и оно конвертирует данные WinUI. Без `-Force` скрипт откажется, если `DimQ1.VoiceType` установлен.

## 3. Путь миграции моделей и настроек

Обе версии записывают `%LOCALAPPDATA%\VoiceType`, а MSIX перенаправляет эту папку в
`%LOCALAPPDATA%\Packages\DimQ1.VoiceType_310ax279fjzmt\LocalCache\Local\VoiceType`. Папка принадлежит семейству пакета и
переживает обновление. Uno-версия берёт её как корень данных (`AppPaths.DefaultDataRoot` при `PackageIdentity.IsPackaged`).
При старте `App.OnLaunched` выполняет по порядку:

1. `DataLocation.RunAtStartup`: незаконченный перенос данных приложения (у пользователей WinUI его нет).
2. **`WinUiDataImport.RunAtStartup`** (только Windows):
   - `settings.json` от WinUI распознаётся по ключам, которые пишет только WinUI (`PostProcessingRules`,
     `DownloaderModelsRootPath`, `SaveSessions`, ...), или по числовому `TextInjectionMethod`, и при отсутствии `History`.
     Оригинал копируется в **`settings.winui.json`**; без резервной копии файл не трогается. Uno-читатель сбросил бы
     все настройки из-за числового enum, поэтому файл конвертируется поштучно:
     - совпадающие по имени и типу значения переносятся: язык, источник звука, провайдер, VAD, хоткеи, «поверх окон»,
       автопрокрутка, ввод текста вкл/выкл, перевод (вкл, язык, CPU/GPU), `FirstRunCompleted`, MP3 и т. д.;
     - `TextInjectionMethod`: `Clipboard` (2) → `"paste"`, `InputSimulator`/`SendInput` → `"type"`;
     - папка моделей по умолчанию → `""` (папка Uno по умолчанию, это та же папка: `Models` и `models` на Windows
       совпадают); **своя папка моделей остаётся папкой моделей**;
     - `SessionsPath` → `""`;
     - пути под виртуальным `%LOCALAPPDATA%\VoiceType` переписываются на настоящую папку;
     - значение, которое не читается, получает значение по умолчанию и пишется в лог.
   - `Models\Translation\*.litertlm*` (Gemma и кэши движка) переименованием переезжает в `llm\` папки моделей.
     Если папка моделей на **другом диске**, многогигабайтный файл при старте не копируется: он ложится в `models\llm`
     на своём диске, а в «Настройки > Модели» появляется заметка, куда его перенести (`ModelStorageText.PreviousVersionModelNote`).
     Заметка исчезает, когда в той папке не остаётся `.litertlm`.
3. `ModelStorageMigrator.RunAtStartup`, существующая логика Uno: плоские папки моделей распознавания переезжают в
   `asr\`, папки дикторов в `diarization\`, `ModelPath` следует за ними.

Правила те же, что у `ModelStorageMigrator`: только переименования на одном диске, ничего не перезаписывается и не
удаляется (кроме пустой папки `Translation`), занятое остаётся до следующего запуска, повторный запуск ничего не делает.
Лог всех шагов: `models-migration.log` в папке данных.

**Не переносится:**
- сессии WinUI (`Sessions\*.json`, отдельный формат). Файлы остаются на месте, «История» показывает только записи Uno;
- пользовательские правила замены текста (`PostProcessingRules`), `StopOnAnyInput`, громкости микрофона и loopback:
  этих функций в Uno нет. Это сказано в «Что нового»;
- данные распакованной Uno-сборки с GitHub (`%LOCALAPPDATA%\VoiceType` вне пакета) пакет автоматически не видит.
  Указать эту папку в «Настройки > Хранилище» в пакете не проверялось.

**Отката нет:** Store не даёт вернуться на 1.3.4. `settings.winui.json` остаётся, папки моделей WinUI после переезда в
`asr\`/`llm\` не годятся для 1.x без ручного переноса назад.

## 4. Что проверено (2026-10-28)

- `dotnet test` VoiceType.Uno.Tests, фильтр Storage/ModelStorage/ModelPathResolver/WinUiDataImport: 119 из 119
  (14 новых). Весь проект тестов не прогонялся.
- Preview-пакет установлен рядом с WinUI 1.3.4.0 (данные WinUI не тронуты). В его `LocalCache` положены копия настоящего
  `settings.json` WinUI (числовой `TextInjectionMethod`, `PostProcessingRules`, папка моделей по умолчанию), Parakeet INT4
  в `Models\`, Gemma в `Models\Translation\` и одна сессия WinUI. Первый запуск через `shell:AppsFolder\...!App`:
  корень данных — настоящая папка `LocalCache`, настройки сконвертированы с копией, Gemma переехала в `models\llm`,
  Parakeet в `models\asr`, `ModelPath` указывает туда, модель загрузилась («Ready»), сессия WinUI на месте, `errors.log` нет.
- В том же пакете работают распознавание из History (Parakeet INT4), определение дикторов, перевод Gemma, «Спросить Gemma».
  Ввод текста в чужие окна, глобальные хоткеи, запись с микрофона и loopback в пакете **не проверялись**: проверки
  делаются только по файлам, без микрофона и динамиков.
- Перенос модели перевода на другой диск покрыт только кодом (`MoveInto` с `OtherDrive`): в тестах нет второго диска.
- «Restart now» (`AppRestart` перезапускает exe по пути) в пакете не проверялся; дочерний процесс пакета должен получить ту же identity.

## 5. Отправка в Partner Center

1. **WACK** на машине с Windows SDK, с правами администратора:
   `appcert.exe test -appxpackagepath <msix> -reportoutputpath report.xml`.
2. **Apps and games → VoiceType → Start update.**
3. **Packages:** удалить пакет 1.3.4.0, загрузить `DimQ1.VoiceType_2.0.0.0_x64_unsigned.msix`.
4. **Store listings → Import:** импорт принимается только целиком, поэтому CSV строится из **свежего экспорта этой
   отправки**: `python docs/store/imports/make-listing-v2.0.0.py <export.csv>`. Набор языковых колонок должен совпадать с
   отправкой: пакет Uno объявляет язык `en`, а не `en-us`, и Partner Center добавил колонку `en`, которую скрипт заполняет
   английским текстом. Затем «Импортировать описания → Выложить папку» и выбрать `docs/store/imports/v2.0.0`. Пути к
   скриншотам должны **начинаться с имени загружаемой папки** (`v2.0.0/screenshots/01-….png`); `screenshots/…` и голое имя
   файла отклоняются как «недопустимое значение». В папке должен лежать только один `.csv` и картинки.
   Импорт 2026-10-28 09:25 UTC прошёл: 17 языков «Завершено».
5. **Submission options → Restricted capabilities (`runFullTrust`):**
   > VoiceType is a desktop dictation app built with the Windows App SDK. It needs runFullTrust for its core features:
   > global hotkeys (RegisterHotKey) that start and stop dictation while another app has the focus; typing the recognized
   > text into the focused third-party app (SendInput); capturing the microphone and the system audio (WASAPI loopback) for
   > meeting transcription; and folder pickers for the model, recording and app-data folders the user chooses.
   > All speech recognition, speaker detection and translation run on the device (ONNX Runtime, LiteRT-LM).
   > The internet is used only to download models from Hugging Face.
6. **Notes for certification:**
   > On first start the app asks to download a speech model (Parakeet TDT INT4, 697 MB, from huggingface.co). After that it
   > works offline. To test: press Ctrl+Shift+V (or the microphone button), speak, and the text is typed into the focused
   > window when "Inject text" is on. Optional AI features (translation, Ask Gemma) need the Gemma 4 E2B model (2.6 GB,
   > Models > Gemma). This version updates the earlier VoiceType: settings and downloaded models are carried over at the
   > first start.
7. Для крупной смены приложения стоит включить **gradual rollout** (например, 10 %) и смотреть Health > Crashes.

## 6. Скриншоты

`docs/store/imports/v2.0.0/screenshots/`, 1920×1080, окно на дисплее 150 % (вёрстка 1280×720). Сняты с установленного
preview-пакета, то есть с того же Release-бинарника, что уходит в Store. Весь контент настоящий:

- аудио четырёх записей синтезировано голосами SAPI сразу в WAV, через динамики ничего не играет
  (`tools/store-screenshots/make-audio.ps1`);
- текст распознан настоящим Parakeet INT4 через `OfflineTranscriber`, тот же путь, что у «Распознать заново» в History
  (`seed.cs`, RTF 0,2);
- дикторы определены в приложении моделью Nemotron diarization INT8 (кнопка «Detect speakers»), имена Daniel/Sarah даны вручную;
- ответы в чате и оба перевода (RU→EN записи, EN→DE в ручном вводе) сделал Gemma 4 E2B в приложении.

Главный экран снят в режиме «Ручной ввод + живой перевод»: живая диктовка требует микрофон или loopback, а для проверок
они не используются. Подпись это учитывает («Type or dictate…»).

Повторить: `tools/store-screenshots/README.md`.
