# Parakeet TDT 0.6B v3 → ONNX конвертация и интеграция

Конвертация [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
в ONNX (FP32 + INT4) и интеграция в C#-стек проекта.

## Статус

| Шаг | Статус |
|---|---|
| Оценка качества и стриминга | ✅ `docs/research/asr/parakeet-tdt-0.6b-v3-evaluation.md` |
| Экспорт ONNX (FP32) | ✅ `export_onnx.py` + CI `convert-parakeet-tdt.yml`; запуск вручную |
| Квантизация INT4 | ✅ включена в `export_onnx.py` (MatMul4BitsQuantizer) |
| Загрузка на HuggingFace | ✅ токен есть (`hf auth list`), скрипт `upload_to_hf.ps1` готов |
| Интеграция в приложения | ⏳ блокировано — ORT GenAI не поддерживает TDT |

## Блокеры (важно прочитать до запуска)

### 1. ONNX Runtime GenAI не поддерживает TDT-декодер

Текущий C#-стек использует `Microsoft.ML.OnnxRuntimeGenAI` (0.15.2). Его
поддержка ASR (`nemotron_speech.py`, `StreamingProcessor` + `Generator` +
`Tokenizer`) реализована **только для Nemotron 3.5 ASR с RNN-T декодером**
(компоненты `encoder` + `decoder`/predictor + `joint` в `genai_config.json`).

`parakeet-tdt-0.6b-v3` — это **FastConformer-TDT**: декодер предсказывает пару
`(token, duration)`, а не классический RNN-T с отдельным joint. Такой декодер
**не входит** в поддерживаемую ORT GenAI модель.

Следствие: даже корректно экспортированный TDT-ONNX не запустится через
текущий C# API без доработки C++-ядра `onnxruntime-genai`.

**Возможные пути (нужно решение):**
- **A.** Доработать `onnxruntime-genai` (C++) для TDT — большой объём, вне рамок рецепта.
- **B.** Экспортировать TDT-компоненты в обычный ONNX и реализовать TDT beam-search
  декодирование на C# через `Microsoft.ML.OnnxRuntime` (новый провайдер `SpeechLib.ParakeetTdt`).
- **C.** Использовать `NeMo-Speech.cpp` (GGUF `parakeet-tdt-0.6b-v3.q8_0.gguf`)
  как sidecar через P/Invoke или subprocess — не ONNX, но рабочий путь.

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

## Порядок работы (после снятия блокеров)

1. Запустить CI-конвертацию: GitHub → Actions → «Convert Parakeet TDT to ONNX» → Run workflow.
   (или локально: `.\setup-env.ps1` затем `python export_onnx.py`)
2. `export_onnx.py` экспортирует encoder (FP32 + INT4) и диагностирует TDT-декодер.
3. Артефакты — как workflow artifact + авто-загрузка на HF (секрет `HF_TOKEN`).
4. Интеграция в приложения — после решения блокера 1 (путь B или C).

## Референсы

- Существующий конвертер (RNNT): `tools/converters/NemotronAsr/`
- TDT paper: https://arxiv.org/abs/2304.06795
- NeMo streaming: https://github.com/NVIDIA/NeMo/blob/main/examples/asr/asr_chunked_inference/rnnt/speech_to_text_streaming_infer_rnnt.py
