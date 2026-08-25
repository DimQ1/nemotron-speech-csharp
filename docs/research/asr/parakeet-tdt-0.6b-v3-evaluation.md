# Оценка: nvidia/parakeet-tdt-0.6b-v3 для ASR-стриминга

**Дата:** 2026-08-26
**Ветка:** `feature/parakeet-tdt-0.6b-v3-eval`
**Кандидат:** [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
**Текущая модель проекта:** [nvidia/nemotron-3.5-asr-streaming-multilingual-0.6B](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-multilingual-0.6B) (FastConformer-CacheAware-RNNT, 600M)

---

## Краткий вывод

- **Качество:** выше, чем у текущей RNN-T модели, за счёт TDT-декодера и более свежего обучения (Granary + NeMo ASR Set 3.0). На Open ASR Leaderboard средний WER **6.32 %**; английский **4.85 %**, русский **5.51 %**, украинский **6.79 %**.
- **Стриминг:** поддерживается. TDT (Token-and-Duration Transducer) изначально спроектирован для потокового распознавания — предсказывает одновременно токен и его длительность, что даёт точные временные метки и стабильный chunked-вывод.
- **Главный риск для этого проекта:** интеграция в C#-стек на ONNX Runtime GenAI. TDT — нестандартный декодер, его конвертация NeMo → ONNX (GenAI) требует проверки; альтернативы — NeMo-Speech.cpp (GGUF) или Transformers (Python).

---

## 1. Описание модели

| Параметр | Значение |
|---|---|
| Архитектура | FastConformer-TDT (энкодер FastConformer + декодер TDT) |
| Параметры | 600M |
| Языки | 25 европейских (вкл. **русский**, **украинский**), автодетект языка |
| Вход | 16 kHz, моно, `.wav`/`.flac` |
| Выход | Текст с пунктуацией и капитализацией |
| Таймстампы | word-level и segment-level (точные, из TDT durations) |
| Длинное аудио | до 24 мин (full attention на A100 80GB), до 3 ч (local attention) |
| Лицензия | CC BY 4.0 (коммерческое использование разрешено) |
| Рантаймы | NeMo (PyTorch), NeMo-Speech.cpp (GGUF), Transformers |
| Дата релиза | 14.08.2025 |

**Что такое TDT.** TDT-декодер (paper [arxiv:2304.06795](https://arxiv.org/abs/2304.06795)) — гибрид CTC и RNN-T. На каждом шаге предсказывает пару `(token, duration)`, поэтому:
- длина вывода привязана к длительности кадра, а не к числу бланк-символов;
- временные метки получаются напрямую без пост-обработки;
- стриминговый вывод стабильнее, чем у классического RNN-T.

---

## 2. Заявленное качество (WER, greedy, без внешней LM)

Источник — model card на Hugging Face.

### Open ASR Leaderboard (английский)

| Метрика | WER |
|---|---|
| Mean WER | **6.32 %** |
| AMI (meetings) | 11.39 % |
| Earnings-22 | 11.42 % |
| GigaSpeech | 9.59 % |
| LibriSpeech (clean) | **1.93 %** |

### Многоязычный eval (избранные языки)

| Язык | WER |
|---|---|
| en | 4.85 % |
| **ru** | **5.51 %** |
| **uk** | **6.79 %** |
| de | 5.04 % |
| fr | 5.15 % |
| es | 3.45 % |
| it | 3.00 % |

> Примечание: WER считается после удаления пунктуации и капитализации.

### Noise robustness (MUSAN)

| SNR | Mean WER | Деградация |
|---|---|---|
| Clean | 6.14 % | — |
| SNR 10 | 6.99 % | −12.28 % |
| SNR 5 | 7.31 % | −29.81 % |
| SNR 0 | 8.91 % | −83.97 % |
| SNR −5 | 15.30 % | −213.64 % |

---

## 3. Стриминг-возможности

**Да, стриминг поддерживается нативно.** В NeMo используется chunked-инференс:

```bash
python speech_to_text_streaming_infer_rnnt.py \
    pretrained_name="nvidia/parakeet-tdt-0.6b-v3" \
    right_context_secs=2.0 \
    chunk_secs=2 \
    left_context_secs=10.0 \
    batch_size=32
```

- `chunk_secs` — размер стримингового чанка.
- `left_context_secs` / `right_context_secs` — контекст внимания вокруг чанка.
- TDT выдаёт временные метки и в потоковом режиме (`timestamps=True` → char/word/segment).

**Следствие для проекта:** модель принципиально подходит для `IStreamingSpeechRecognizer` (тот же контракт, что использует текущий Nemotron-провайдер в `SpeechLib.Nemotron`).

---

## 4. Сравнение с текущей моделью

| Аспект | Nemotron 3.5 ASR (текущая) | Parakeet TDT v3 (кандидат) |
|---|---|---|
| Декодер | CacheAware **RNN-T** | **TDT** (token + duration) |
| Таймстампы | токен/символ, ~80 ms/кадр | **word + segment**, из durations |
| Пунктуация/капитализация | есть | есть (заявлено) |
| Языки | multilingual (streaming) | 25 европейских, автодетект |
| Стриминг | да (RNN-T) | да (TDT, стабильнее) |
| Рантайм в проекте | **ONNX Runtime GenAI (C#)** — уже работает | требуется конвертация/обёртка |

> Для объективного сравнения WER обеих моделей нужен **общий бенчмарк** (один и тот же аудионабор + одна метрика WER с одинаковой нормализацией). Заявленные цифры выше — только для parakeet-tdt-0.6b-v3; метрики Nemotron 3.5 ASR брать из её model card и пересчитывать на том же сете.

---

## 5. План эмпирической проверки (в этой ветке)

1. **Скачать модель и прогон на бенчмарке**
   - Набор: `Test-Audio/librispeech/` (уже в репозитории) + при необходимости новые сэмплы с русской/украинской речью.
   - Измерить WER по эталону через существующие скрипты `compare_wer.py` / `bench_quant.py`.
2. **Проверить стриминг-совместимость с C#-стеком**
   - Путь A (предпочтительный): конвертация NeMo TDT → ONNX через `tools/converters/NemotronAsr`, проверка загрузки в `Microsoft.ML.OnnxRuntimeGenAI`. Риск: ORT GenAI может не поддерживать TDT-декодер.
   - Путь B: обёртка над NeMo-Speech.cpp (GGUF `parakeet-tdt-0.6b-v3.q8_0.gguf`) через P/Invoke. Риск: NeMo-Speech.cpp ориентирован на offline/batch, стриминг под вопросом.
   - Путь C (fallback): Transformers/Python как эталон качества, без интеграции в C#.
3. **Сравнить метрики** с текущей Nemotron-моделью на том же наборе и зафиксировать вывод в этом документе.
4. **Принять решение** о целесообразности замены/добавления провайдера.

### Открытые вопросы

- [ ] Поддерживает ли ORT GenAI 0.15.2 TDT-декодер после конвертации?
- [ ] Какова задержка first-token / real-time factor (RTFx) на CPU (текущий проект CPU-first)?
- [ ] Размер ONNX/GGUF и потребление RAM на CPU (в сравнении с текущей ~2 ГБ).
- [ ] Точность временных меток в стриминговом режиме.

---

## 6. Статус конвертации и интеграции (2026-08-26)

**Конвертация и интеграция решены** — TDT-модель работает через обычный
ONNX Runtime (не GenAI):

- Готовые артефакты: `istupakov/parakeet-tdt-0.6b-v3-onnx` / `PalatineVision/...`
  (`encoder-model.onnx` + `decoder_joint-model.onnx` + `nemo128.onnx` +
  `vocab.txt` + `config.json`; FP32 и INT8). Способ конвертации — NeMo
  `model.export()`, референс-декодер — пакет `onnx-asr` (MIT, istupakov).
- Точность: WER 2.16 % LibriSpeech test-clean (INT8 = FP32), RTF ~0.05 (~20× real-time).
- Реализован C#-провайдер `SpeechLib.ParakeetTdt` (`IStreamingSpeechRecognizer`
  поверх `Microsoft.ML.OnnxRuntime`) — собирается без ошибок, greedy-логика
  верифицирована на `Test-Audio/librispeech` (корректный транскрипт).
- **Стриминг** — buffer-based (перекрывающиеся окна `left|chunk|right` через
  полный encoder, TDT-состояние переносится между чанками). Cache-aware
  `forward_for_export` неприменим: у модели `att_context_style="regular"`
  (full attention) — подтверждено инспекцией NeMo 3.0 `ConformerEncoder`.
- Модель выложена на HF: `DimQ1/parakeet-tdt-0.6b-v3-onnx` (папки
  `fp32/`, `int8/`, `int4/`).
- Сервер-обёртка (альтернатива): `groxaxo/parakeet-tdt-0.6b-v3-fastapi-openai`.

Ограничение: ORT GenAI (текущий Nemotron-стек) TDT не поддерживает — провайдер
использует обычный OnnxRuntime и buffer-based стриминг.

## Источники

- Model card: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3
- Tech report: [arxiv:2509.14128](https://arxiv.org/abs/2509.14128)
- TDT paper: [arxiv:2304.06795](https://arxiv.org/abs/2304.06795)
- NeMo-Speech.cpp: https://github.com/NVIDIA/NeMo-Speech.cpp
- Open ASR Leaderboard: https://huggingface.co/spaces/hf-audio/open_asr_leaderboard
