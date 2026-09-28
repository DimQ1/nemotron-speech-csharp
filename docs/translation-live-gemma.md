# Live translation with Gemma 4 (LiteRT-LM)

Состояние на 2026-09-28. Оба приложения (VoiceType.WinUI, VoiceType.Uno) и CLI
переводят живой транскрипт локальной моделью Gemma 4 E2B через LiteRT-LM.

## Архитектура

```
Recognizer partials/finals ──► LiveTranslationSession (SpeechLib/Translation)
                                  ├─ буфер транскрипта с поддержкой ревизий
                                  ├─ SentenceSplitter → финальные предложения
                                  ├─ черновик хвоста (debounce, стабильный префикс)
                                  ├─ контекст: предыдущее предложение + его перевод
                                  ├─ memo: предложение → перевод
                                  └─ TranslationOutputCleaner (метки, кавычки, JSON, <unk>, петли)
                                          │
                       ITextTranslator ◄──┘
                       ├─ LiteRTLmNativeTranslator (SpeechLib.LiteRT.Native, in-process)
                       └─ LiteRTLmTranslator (SpeechLib.LiteRT, OpenAI-совместимый HTTP)
```

`LiveTranslationSession` — общий движок; приложения держат только тонкие адаптеры
(`Services/TranslationService.cs`), которые выбирают бэкенд, знают путь к модели и
переводят события в UI-поток. До этого WinUI и Uno имели две расходящиеся копии
координатора по ~800 строк.

## Что изменилось (2026-09-28)

| Было | Стало |
|---|---|
| Сэмплер по умолчанию из файла модели (top-k 40, top-p 0.95, T 1.0): каждый проход черновика давал другой текст, декод ~2× дольше | Greedy-декодирование; при отказе нативного слоя — откат на сэмплер модели |
| Каждое предложение переводилось изолированно | Предыдущее предложение и его перевод передаются как history-ход (native) / пара сообщений (HTTP) |
| Язык источника не передавался | Язык распознавания из настроек уходит в промпт ("from English into Russian") |
| Uno передавал в промпт код ("into ru") и предлагал «auto» как цель | Общий список `TranslationLanguages`: в промпт всегда идёт имя, «auto» исключён |
| Feed предполагал только дописывание текста; ревизия хвоста (Parakeet-превью) портила буфер | Feed сравнивает с прошлым текстом: ревизия хвоста перезапускает черновик, ревизия внутри готового предложения игнорируется |
| WinUI не подавал финальный текст перед flush — исправления финала не переводились | Финальный транскрипт подаётся перед flush |
| Черновик отменялся при каждом обновлении хвоста — при частых partial'ах текст не появлялся до конца фразы | Проход черновика доводится до конца, затем перезапускается с новым хвостом |
| Вывод модели не чистился (метки, кавычки, `<unk>`, зацикливания) | `TranslationOutputCleaner` |
| Смена бэкенда/промпта убивала движок во время декода | Замена движка под воротами декода (`ReplaceTranslatorAsync`) |
| Повторяющиеся фразы декодировались заново | Memo на 256 предложений |

Замеры (20-ядерный ноутбук, CPU, en→ru, предложение из 15 слов):
greedy ≈ 1.3 с, с контекстом ≈ 2.0 с, сэмплер модели ≈ 2.5–3 с; первый черновик
хвоста появляется примерно через 1.5 с после начала фразы.

## NuGet и модель

- **LiteRtLmSharp 1.1.1 → 1.2.0** (нативный LiteRT-LM v0.16.0). Даёт per-send
  параметры (`NoRepeatNgram`, штрафы за повторы), YNNPACK, multi-conversation.
  Проверено на Windows x64 с Gemma 4 E2B: загрузка, greedy, история, стриминг.
  Известная особенность: при нехватке памяти нативный движок может отвергнуть явный
  сэмплер («Sampler type: 3 not implemented yet») — транслятор повторяет вызов без
  него. Linux x64 теперь требует `libvulkan1` (добавлен в Depends deb-пакета);
  Windows больше не требует VC++ Redistributable. Issue LiteRT-LM #2149 (падение
  CPU-декода на Linux с самосборным engine_cpu) остаётся открытым, но официальные
  prebuilt-библиотеки 0.16, которые использует 1.2.0, на Windows работают.
- Прочие обновления (не применены): `Microsoft.ML.OnnxRuntimeGenAI` 0.16.0 → 0.17.0
  (влияет на Nemotron, нужна отдельная проверка), `NAudio` 3.0.1 → 3.1.0,
  `Microsoft.Windows.AI.MachineLearning` 2.1.74 → 2.4.89, тестовые пакеты xunit 2.9.3 /
  runner 4.0.0 / Test.Sdk 18.10.1, `System.CommandLine` 2.0.12, `BenchmarkDotNet` 0.15.8.
- **Модель**: `litert-community/gemma-4-E2B-it-litert-lm` (обновлена 2026-08-31) —
  новее среди E2B нет. В том же репозитории есть `gemma-4-E2B-it-gpu.litertlm` (веса
  под GPU) и варианты под NPU; приложения используют общий файл. Более крупные
  LiteRT-LM-сборки (E4B, 12B, 26B-A4B, 31B от 2026-09-23) для живого перевода на
  CPU непрактичны.

## Настройки

`RecognizerFactoryOptions` не затронуты; параметры перевода задаются в
`LiveTranslationOptions` (debounce, лимит хвоста, memo, контекст) и
`LiteRTLmNativeOptions` (`Greedy`, `NoRepeatNgramSize`, `NumThreads`, `MaxTokens`).
Тесты: `libraries/SpeechLib/tests/SpeechLib.Tests/Unit_LiveTranslationSessionTests.cs`,
`Unit_TranslationOutputCleanerTests.cs`, `Unit_TranslationLanguagesTests.cs`,
`Unit_SentenceSplitterTests.cs`, `Unit_StablePrefixTests.cs`.
