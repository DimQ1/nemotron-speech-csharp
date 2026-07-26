# VoiceType.WinUI — Microsoft Store Submission Guide

**Дата подготовки:** 2026-07-26
**Целевая платформа:** Windows 10 2004+ (10.0.19041.0), x64
**Тип упаковки:** MSIX packaged, self-contained, `runFullTrust`

---

## 0. Предварительные требования

Перед началом Submission убедитесь, что у вас есть:

- [ ] Аккаунт разработчика Microsoft Partner Center (≈$19 разовый взнос для individual, ≈$99 для company)
- [ ] Подписанный сертификат для подписи кода (EV Code Signing) — **обязательно** для Store
- [ ] Privacy Policy URL (см. раздел 2)

---

## 1. Partner Center: резервирование имени приложения

1. Зайдите в [Partner Center](https://partner.microsoft.com/dashboard) → **Apps and Games**
2. Нажмите **+ New product** → выберите **MSIX or PWA app** → укажите имя: `VoiceType`
3. После резервирования перейдите в **Product Management → Product Identity**
4. Скопируйте три значения и вставьте их в `Package.appxmanifest`:

```xml
<Identity
    Name="ЗНАЧЕНИЕ_ИЗ_PackageIdentityName"         <!-- Пример: 12345YourCompany.VoiceType -->
    Publisher="ЗНАЧЕНИЕ_ИЗ_Publisher"               <!-- Пример: CN=ABCD1234-... -->
    Version="1.0.1.0" />
```

5. Замените `<PublisherDisplayName>` на имя, зарегистрированное в Partner Center

---

## 2. Privacy Policy (GDPR Compliance)

Приложение отправляет данные:
- **В интернет:** скачивание моделей с HuggingFace; телеметрия в Aspire Dashboard (только dev-режим)
- **Микрофон:** аудиозапись для распознавания речи (обрабатывается **локально**, не передаётся в облако)
- **Клавиатура:** инъекция текста через `SendInput`

### Варианты размещения политики:
- **Быстрый путь:** GitHub Pages в этом же репо (`docs/privacy.md`)
- **Внешний:** отдельный сайт или сервис вроде TermsFeed

### Шаблон privacy policy — создайте файл `docs/privacy.md` в корне репо:

```markdown
# VoiceType Privacy Policy

**Last updated:** 2026-07-26

## Data Collection

**VoiceType does NOT collect, transmit, or sell your personal data.** All processing happens locally on your device:

- **Audio recordings:** Processed entirely on-device by the Nemotron ASR engine. Audio is never uploaded to any server.
- **Recognized text:** Injected directly into your active application via Windows text input APIs. Text is not stored or transmitted.
- **Model downloads:** The app downloads ASR model files from HuggingFace (huggingface.co). These are anonymous HTTP requests containing no personal data.

## Diagnostics (Development Mode Only)

In development builds, the app may export anonymous performance logs to a local Aspire Dashboard. This is disabled in production (Store) builds.

## Third-Party Services

- **HuggingFace:** Model files are downloaded from huggingface.co. See [HuggingFace Privacy Policy](https://huggingface.co/privacy).

## Your Rights

Since no personal data is collected, there is no data to access, delete, or port. The app works fully offline after initial model download.

## Contact

For privacy questions: [your-email@example.com]

## Changes

We will update this policy if data practices change. Continued use of the app constitutes acceptance.
```

### После создания политики:
1. Загрузите `privacy.md` на GitHub Pages или другой хостинг
2. В Partner Center → **Properties** → укажите URL политики

---

## 3. Store Listing (Partner Center)

### 3.1. Properties
| Поле | Значение |
|---|---|
| **Category** | Utilities & Tools |
| **Subcategory** | Voice & Speech |
| **Privacy Policy URL** | `https://your-domain/privacy` |
| **Website** | `https://github.com/DimQ1/nemotron-speech-csharp` |

### 3.2. Pricing & Availability
| Поле | Значение |
|---|---|
| **Price** | Free |
| **Free trial** | Not applicable |
| **Markets** | All markets (или выберите нужные) |
| **Release date** | As soon as possible |

### 3.2b. Donations (донаты)
Добавьте в **Description** приложения блок:
```
💝 Поддержать разработку:
  • IBAN (BYN): BY97PJCB30140010095081080933 (Priorbank)
  • Карта: 4916 9896 9022 8035
```
Или укажите ссылку на `DONATE.md` в GitHub-репо.

### 3.3. Age ratings
- Пройдите опросник IARC (International Age Rating Coalition)
- Приложение **не содержит:** насилия, азартных игр, alcohol/tobacco reference
- Рекомендуемый рейтинг: **3+ (E for Everyone)**

### 3.4. Packages
- Загрузите `.msixupload` файл (см. раздел 6)
- Выберите архитектуру: **x64** (основная), опционально ARM64

### 3.5. Store Listing Content (скопировать в Partner Center)

#### Description (English)
```
VoiceType is a lightning-fast AI dictation app that types what you say — in any application.
Powered by Nemotron ASR running entirely on your device.

🔹 WHY VOICETYPE:
• Real-time transcription — words appear as you speak
• Works in ANY app — Notepad, Word, browsers, messengers, code editors
• 100% offline — all processing on your device, no cloud, no privacy concerns
• Global hotkeys — start/stop dictation even when VoiceType is minimized
• 17 languages — auto-detect or choose: EN, RU, DE, FR, ES, ZH, JA, KO, PT, IT, AR, HI, TR, UK, PL, NL
• Free & open-source — no ads, no subscriptions, no data collection

🔹 HOW IT WORKS:
1. Install VoiceType from Microsoft Store
2. On first launch, download a speech recognition model (~600 MB, one-time)
3. Press the hotkey (default: Ctrl+Shift+V) and start speaking
4. Recognized text appears in your active window — just like magic

🔹 SYSTEM REQUIREMENTS:
• Windows 10 version 2004+ or Windows 11
• 8 GB RAM recommended (4 GB minimum)
• x64 processor
• ~1 GB free disk space for AI model

🔹 PRIVACY:
VoiceType does NOT collect, transmit, or sell ANY of your data.
Microphone audio is processed locally and discarded immediately.
No internet connection is required after model download.

💝 Support development:
  IBAN (BYN): BY97PJCB30140010095081080933 (Priorbank)
```

#### Description (Русский)
```
VoiceType — быстрый AI-диктовщик, который печатает то, что вы говорите — в любом приложении.
На базе Nemotron ASR, работает полностью на вашем устройстве.

🔹 ПОЧЕМУ VOICETYPE:
• Распознавание в реальном времени — слова появляются по мере речи
• Работает в ЛЮБОМ приложении — Блокнот, Word, браузер, мессенджеры, редакторы кода
• 100% офлайн — вся обработка на устройстве, никаких облаков и утечек
• Глобальные горячие клавиши — старт/стоп диктовки даже когда VoiceType свёрнут
• 17 языков — автоопределение или выбор: EN, RU, DE, FR, ES, ZH, JA, KO, PT, IT, AR, HI, TR, UK, PL, NL
• Бесплатно и с открытым кодом — без рекламы, подписок и сбора данных

🔹 КАК РАБОТАЕТ:
1. Установите VoiceType из Microsoft Store
2. При первом запуске скачайте модель распознавания (~600 МБ, один раз)
3. Нажмите горячую клавишу (по умолчанию Ctrl+Shift+V) и говорите
4. Текст появляется в активном окне — как по волшебству

🔹 СИСТЕМНЫЕ ТРЕБОВАНИЯ:
• Windows 10 версии 2004+ или Windows 11
• 8 ГБ ОЗУ рекомендуется (4 ГБ минимум)
• Процессор x64
• ~1 ГБ свободного места для AI-модели

🔹 КОНФИДЕНЦИАЛЬНОСТЬ:
VoiceType НЕ собирает, НЕ передаёт и НЕ продаёт ваши данные.
Аудио с микрофона обрабатывается локально и сразу удаляется.
Интернет не требуется после загрузки модели.

💝 Поддержать разработку:
  IBAN (BYN): BY97PJCB30140010095081080933 (Priorbank)
```

#### App Features (short bullets for Store listing)
- [x] Real-time speech-to-text with AI
- [x] Works in any Windows application
- [x] 100% offline — no cloud, total privacy
- [x] Global hotkeys (Ctrl+Shift+V)
- [x] 17 languages (EN, RU, DE, FR, ES, ZH, JA, KO, PT, IT, AR, HI, TR, UK, PL, NL + auto)
- [x] Free and open-source

---

## 4. Обязательные Store Assets

Для публикации нужны иконки точных размеров. Текущие ассеты в проекте:

| Файл | Размер | Статус | Назначение |
|---|---|---|---|
| `StoreLogo.png` | 50×50 | ✅ | Store listing logo |
| `Square150x150Logo.scale-200.png` | 300×300 | ✅ | Medium tile |
| `Square44x44Logo.scale-200.png` | 88×88 | ✅ | App list icon |
| `Wide310x150Logo.scale-200.png` | 620×300 | ✅ | Wide tile |
| `SplashScreen.scale-200.png` | 1240×1240 | ✅ | Splash screen |
| `LockScreenLogo.scale-200.png` | 48×48 | ✅ | Lock screen |
| `AppIcon.ico` | 256×256 | ✅ | App icon |

### Дополнительные ассеты, требуемые Store:

Создайте следующие размеры (можно сгенерировать из `app-icon.png` через [App Icon Generator](https://www.microsoft.com/en-us/p/app-icon-generator/) или скриптом):

```powershell
# Генерация через ImageMagick (установите предварительно)
magick Assets\app-icon.png -resize 44x44   Assets\Square44x44Logo.png
magick Assets\app-icon.png -resize 50x50   Assets\StoreLogo.png
magick Assets\app-icon.png -resize 71x71   Assets\Square44x44Logo.scale-150.png
magick Assets\app-icon.png -resize 150x150 Assets\Square150x150Logo.png
magick Assets\app-icon.png -resize 200x200 Assets\Square150x150Logo.scale-150.png
magick Assets\app-icon.png -resize 310x150 Assets\Wide310x150Logo.png
magick Assets\app-icon.png -resize 620x300 Assets\Wide310x150Logo.scale-200.png
```

### Store Listing Screenshots (Partner Center)
- Минимум **1 screenshot** (рекомендуется 3-5), разрешение **1366×768** или выше
- Покажите: главное окно с распознанным текстом, окно настроек, процесс загрузки модели

---

## 5. Restricted Capability Justification

Приложение использует ограниченную capability `runFullTrust`. При подаче в Store потребуется **justification**:

> **Justification text (for Partner Center):**
>
> VoiceType is a desktop dictation application that requires `runFullTrust` for three core features that are impossible without full trust:
>
> 1. **Global hotkeys (RegisterHotKey):** Users must be able to start/stop dictation via keyboard shortcuts even when VoiceType is not the focused window (e.g., while typing in Word or a browser).
>
> 2. **Text injection (SendInput):** The primary function of the app is to type recognized speech into any third-party application. This requires synthesizing keystrokes via the Win32 SendInput API, which is blocked in AppContainer sandbox.
>
> 3. **Low-level keyboard hook (SetWindowsHookEx):** Push-to-talk functionality requires monitoring keyboard state globally.
>
> All audio processing is local (on-device ONNX Runtime inference). No audio data leaves the user's machine.
>
> The app is distributed as a packaged MSIX desktop application (Windows App SDK / WinUI 3).

---

## 6. Сборка Release Package

### Команда сборки Store-пакета:

```powershell
# Очистка и публикация x64 Release MSIX
dotnet publish VoiceType.WinUI\VoiceType.WinUI.csproj `
  -c Release `
  -p:Platform=x64 `
  -p:GpuArch=CPU `
  -p:PublishProfile=Properties\PublishProfiles\win-x64.pubxml
```

### Что происходит при сборке:
- ✅ Генерируется `.msix` файл в `VoiceType.WinUI\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\`
- ✅ `PublishReadyToRun=true` — AOT-компиляция для быстрого запуска
- ✅ `PublishTrimmed=true` — удаление неиспользуемого IL-кода
- ✅ `SelfContained=true` — .NET Runtime включён в пакет
- ✅ ORT 1.25.1 native DLL копируются в AppX
- ✅ `nemotron_swish_cpu.dll` custom op копируется в AppX

### Проверка пакета перед загрузкой:

```powershell
# Установите Windows SDK (если ещё нет)
# Запустите Windows App Certification Kit:
# Start Menu → Windows Kits → Windows App Cert Kit

# Или проверьте вручную:
dotnet build VoiceType.WinUI\VoiceType.WinUI.csproj -c Release -p:GpuArch=CPU
```

---

## 7. Partner Center Submission Checklist

Ниже чек-лист по страницам Partner Center (соответствует [официальной документации](https://learn.microsoft.com/ru-ru/windows/apps/publish/publish-your-app/msix/create-app-submission)):

### 7.1. Pricing & Availability (Цены и доступность)
- [ ] **Markets:** All possible markets (или выберите нужные)
- [ ] **Audience:** Public audience
- [ ] **Discoverability:** Make this product available and discoverable in Microsoft Store
- [ ] **Schedule:** Release as soon as possible
- [ ] **Base price:** Free
- [ ] **Free trial:** Not applicable
- [ ] **Organization licensing:** Not applicable

### 7.2. Properties (Свойства)
- [ ] **Category:** Utilities & Tools
- [ ] **Subcategory:** (необязательно)
- [ ] **Privacy policy URL:** `https://dimq1.github.io/nemotron-speech-csharp/privacy` (или ваш URL)
- [ ] **Website:** `https://github.com/DimQ1/nemotron-speech-csharp`
- [ ] **Support contact:** ваш email

### 7.3. Age Ratings (Возрастные рейтинги)
- [ ] Пройти IARC-опросник
- [ ] Рейтинг: **3+ (E for Everyone)**

### 7.4. Packages (Пакеты)
- [ ] Загрузить `.msixupload` (см. §6)
- [ ] Архитектура: **x64** (основная), опционально ARM64
- [ ] **Device family availability:** Windows.Desktop

### 7.5. Store Listing (Список магазина)
- [ ] **Description:** скопировать из §3.5 (EN + RU)
- [ ] **What's new in this version:** (можно пропустить для первой версии)
- [ ] **App features:** 17 languages, real-time, offline, global hotkeys, etc.
- [ ] **Screenshots:** минимум 1 (рекомендуется 4+), 1366×768+
- [ ] **Store logos:** все размеры готовы (см. §4)
- [ ] **Keywords:** voice, dictation, speech-to-text, ASR, AI, offline, nemotron
- [ ] **Copyright:** © 2026 DimQ1

### 7.6. Submission Options (Параметры отправки)
- [ ] **Restricted capabilities justification** — ОБЯЗАТЕЛЬНО (см. §5)
- [ ] **Notes for certification:**
  ```
  VoiceType requires internet access only for one-time model download (~600 MB)
  from HuggingFace. After download, all processing is local. No cloud services.
  Audio is never uploaded. The app uses runFullTrust for global hotkeys,
  text injection (Win32 SendInput), and low-level keyboard hooks.
  ```
- [ ] **Submission notification audience:** (необязательно)

---

## 8. Типичные ошибки при WACK-проверке

| Ошибка | Решение |
|---|---|
| **API not supported** | Убедитесь, что не используются заблокированные Win32 API. `SendInput` и `RegisterHotKey` разрешены с `runFullTrust`. |
| **Missing DllMain** | Проверьте, что native DLL (ort, swish) помечены как `Content` и копируются в AppX. |
| **Unsupported file type** | Убедитесь, что в пакете нет `.pdb` файлов. |
| **Bundle manifest error** | Проверьте, что версия в `.appxmanifest` состоит из 4 частей (Major.Minor.Build.Revision). |
| **Digital signature error** | Используйте корректный EV Code Signing сертификат. |

---

## 9. После публикации

- [ ] Настройте **gradual rollout** (поэтапное развёртывание) для снижения рисков
- [ ] Включите **app analytics** в Partner Center
- [ ] Настройте **crash reports** через Partner Center или OpenTelemetry
- [ ] Обновляйте `AGENTS.md` при изменениях процесса сборки

---

## Ссылки

- [Microsoft Store Policies](https://docs.microsoft.com/en-us/windows/uwp/publish/store-policies)
- [MSIX Packaging](https://docs.microsoft.com/en-us/windows/msix/)
- [Windows App SDK deployment](https://docs.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-overview)
- [Restricted Capabilities](https://docs.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations#restricted-capabilities)
- [Partner Center Dashboard](https://partner.microsoft.com/dashboard)
