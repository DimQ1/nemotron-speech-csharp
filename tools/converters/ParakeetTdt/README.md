# Parakeet TDT 0.6B v3 → ONNX конвертация и интеграция

Конвертация [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
в ONNX (FP32 + INT4) и интеграция в C#-стек проекта.

## Статус

| Шаг | Статус |
|---|---|
| Оценка качества и стриминга | ✅ `docs/research/asr/parakeet-tdt-0.6b-v3-evaluation.md` |
| Экспорт ONNX (FP32) | ✅ готовые артефакты: `istupakov/parakeet-tdt-0.6b-v3-onnx` |
| Квантизация (INT8/INT4) | ✅ `.int8.onnx` (готовые) + `.int4.onnx` (MatMulNBits, верифицированы) |
| Загрузка на HuggingFace | ✅ `DimQ1/parakeet-tdt-0.6b-v3-onnx` (FP32 + INT8 + INT4) |
| Интеграция в приложения | ✅ `SpeechLib.ParakeetTdt` (C# + OnnxRuntime) — собран, логика верифицирована |
| Потоковое распознавание | ✅ buffer-based (окна left/chunk/right) в C#; cache-aware неприменим (модель «regular» attention) |

## Блокеры (важно прочитать до запуска)

### 1. ONNX Runtime GenAI не поддерживает TDT-декодер

Текущий C#-стек использует `Microsoft.ML.OnnxRuntimeGenAI` (0.15.2). Его
поддержка ASR (`nemotron_speech.py`, `StreamingProcessor` + `Generator` +
`Tokenizer`) реализована **только для Nemotron 3.5 ASR с RNN-T декодером**
(компоненты `encoder` + `decoder`/predictor + `joint` в `genai_config.json`).

`parakeet-tdt-0.6b-v3` — это **FastConformer-TDT**: декодер предсказывает пару
`(token, duration)`, а не классический RNN-T с отдельным joint. Такой декодер
**не входит** в поддерживаемую ORT GenAI модель.

Следствие: TDT-ONNX **не запустится через ORT GenAI**. Но это не блокирует
интеграцию — обычный **`Microsoft.ML.OnnxRuntime`** исполняет `encoder` +
`decoder_joint` TDT-модели, а greedy-декодер реализуется на C# (референс: `onnx-asr`).

## Рабочий путь (проверен)

Конвертация и декодирование TDT уже решены в проекте [onnx-asr](https://github.com/istupakov/onnx-asr) (MIT):

- **Конвертация:** `nemo_asr.models.ASRModel.from_pretrained(...).export("model.onnx")`
  → `encoder-model.onnx` + `decoder_joint-model.onnx` + `nemo128.onnx` + `vocab.txt`
  + `config.json` (`model_type: "nemo-conformer-tdt"`, `features_size: 128`,
  `subsampling_factor: 8`, `max_tokens_per_step: 10`).
- **Готовые артефакты** (идентичны): `istupakov/parakeet-tdt-0.6b-v3-onnx`
  и `PalatineVision/parakeet-tdt-0.6b-v3-onnx` (FP32 + INT8).
- **Точность:** WER 2.16 % LibriSpeech test-clean (INT8 = FP32), RTF ~0.05 (~20× real-time) на CPU.
- **Декодирование** (TDT greedy, `onnx_asr/models/nemo.py`):
  - encoder: `audio_signal [B,128,T], length [B]` → `outputs [B,D,T'], encoded_lengths [B]`.
  - decoder_joint: `encoder_outputs [1,D,1], targets [[token]], target_length [1], input_states_1/2`
    → `outputs [vocab + duration logits], output_states_1/2`.
  - TDT: `vocab_logits = outputs[:vocab_size]`; `duration = argmax(outputs[vocab_size:])`.
- **Сервер-обёртка:** `groxaxo/parakeet-tdt-0.6b-v3-fastapi-openai` (OpenAI-совместимый FastAPI).
- **Собственный HF-репо:** `DimQ1/parakeet-tdt-0.6b-v3-onnx` — каждая квантизация
  в своей папке: `fp32/`, `int8/`, `int4/` (внутри — стандартные имена
  `encoder-model.onnx`, `decoder_joint-model.onnx`, `nemo128.onnx`, `vocab.txt`, `config.json`).
  INT4 создан `MatMulNBitsQuantizer` bits=4 block_size=32 symmetric (encoder INT4
  648 МБ vs FP32 2.36 ГБ, decoder_joint INT4 49 МБ vs 69 МБ); INT4 даёт идентичный
  INT8 текст на тестовом сэмпле LibriSpeech.

### 2. Окружение конвертации — решено через GitHub Actions

Локально `torch`/`nemo_toolkit`/`conda` отсутствуют (системный Python 3.12).
Поэтому конвертация выполняется в CI (`.github/workflows/convert-parakeet-tdt.yml`):
Linux runner + Python 3.10 + NeMo 2.4 + torch CPU (GPU для экспорта не обязателен).
Запуск: `workflow_dispatch`. Для шага загрузки на HF добавьте секрет `HF_TOKEN`.

## Требования

```text
# requirements.txt (установка через setup-env.ps1)
torch>=2.4          # CUDA build
nemo_toolkit[asr]>=2.4
olive-ai            # или torch.onnx + onnxruntime.quantization
onnx>=1.16
onnxruntime>=1.19
sentencepiece
huggingface_hub
transformers
```

## Интеграция в C# (реализовано)

Провайдер `SpeechLib.ParakeetTdt` реализован и компилируется:

- `libraries/SpeechLib/src/SpeechLib.ParakeetTdt/ParakeetTdtRecognizer.cs`
  реализует `IStreamingSpeechRecognizer` поверх `Microsoft.ML.OnnxRuntime`:
  nemo128 → encoder → TDT greedy (duration-продвижение) → детокенизация.
- **Стриминг** — buffer-based (как в NeMo `speech_to_text_streaming_infer_rnnt.py`):
  перекрывающиеся окна `[left | chunk | right]` подаются в полный encoder, и
  декодируются только кадры chunk; TDT-состояние (LSTM) переносится между
  чанками. Конструктор: `new ParakeetTdtRecognizer(dir, chunkSeconds, leftContextSeconds, rightContextSeconds)`.
  Cache-aware `forward_for_export` **неприменим** — у модели `att_context_style="regular"` (full attention).
- Проект добавлен в `NemotronSpeech.slnx`.
- Логика greedy-декодера верифицирована на `Test-Audio/librispeech` (через
  `build/verify_parakeet.py`) — выдаёт корректный текст.

Подключение в приложениях — заменить/дополнить `SpeechLib.Nemotron` на
`SpeechLib.ParakeetTdt`; конструктор принимает папку квантизации:
`new ParakeetTdtRecognizer("models/parakeet-tdt/int8")` (или `.../int4`, `.../fp32`).

### Структура каталога моделей (HF `DimQ1/parakeet-tdt-0.6b-v3-onnx`)

```
parakeet-tdt-0.6b-v3-onnx/
├── fp32/
│   ├── encoder-model.onnx (+ encoder-model.onnx.data)
│   ├── decoder_joint-model.onnx
│   ├── nemo128.onnx, vocab.txt, config.json
├── int8/    (тот же набор, INT8-веса)
└── int4/    (тот же набор, INT4-веса)
```

- `nemo128.onnx` — log-mel препроцессор (waveform → [1,128,T])
- `vocab.txt` — 8193 строки, blank=`<blk>`=8192
- `config.json` — `nemo-conformer-tdt`, features 128, subsampling 8

## Референсы

- Существующий конвертер (RNNT): `tools/converters/NemotronAsr/`
- TDT paper: https://arxiv.org/abs/2304.06795
- NeMo streaming: https://github.com/NVIDIA/NeMo/blob/main/examples/asr/asr_chunked_inference/rnnt/speech_to_text_streaming_infer_rnnt.py
