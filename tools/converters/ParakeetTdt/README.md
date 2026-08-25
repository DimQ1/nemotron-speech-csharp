# Parakeet TDT 0.6B v3 → ONNX конвертация и интеграция

Конвертация [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
в ONNX (FP32 + INT4) и интеграция в C#-стек проекта.

## Статус

| Шаг | Статус |
|---|---|
| Оценка качества и стриминга | ✅ `docs/research/asr/parakeet-tdt-0.6b-v3-evaluation.md` |
| Экспорт ONNX (FP32) | ⏳ блокировано — см. «Блокеры» |
| Квантизация INT4 | ⏳ блокировано — требуется FP32-артефакт |
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

### 2. Отсутствует окружение конвертации

Требуется `nemo_toolkit[asr]` (модель в формате NeMo `.nemo`), `olive-ai` или
`torch.onnx`, плюс GPU для калибровки INT4. На машине:

- `torch`, `nemo_toolkit`, `olive-ai`, `transformers` — **не установлены**.
- `conda` — **не установлен** (рекомендуется для NeMo 2.4, Python 3.10).
- Доступен GPU: RTX 5070 Ti Laptop (12 ГБ) — хватит для 0.6B, но впритык для FP32.
- Системный Python 3.12 (NeMo 2.4 официально поддерживает 3.10–3.12).

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

1. Создать окружение: `.\setup-env.ps1`
2. Экспорт компонентов:
   - `encoder` — FastConformer (можно переиспользовать `StreamingEncoderWrapper`
     из `tools/converters/NemotronAsr/src/nemotron_model_load.py`).
   - `decoder` — **TDT-декодер** (потребуется новый wrapper; изучить
     `asr_model.decoder` в NeMo 2.4).
3. Квантизация encoder до INT4 (k-quant, см. `nemotron_encoder_int4_cpu.json`).
4. Проверка вывода (encoder FP32 vs INT4) на `Test-Audio/librispeech/`.
5. Загрузка на HF: `.\upload_to_hf.ps1 -RepoId DimQ1/parakeet-tdt-0.6b-v3-onnx`
6. Интеграция в приложения (путь B или C из блокера 1).

## Референсы

- Существующий конвертер (RNNT): `tools/converters/NemotronAsr/`
- TDT paper: https://arxiv.org/abs/2304.06795
- NeMo streaming: https://github.com/NVIDIA/NeMo/blob/main/examples/asr/asr_chunked_inference/rnnt/speech_to_text_streaming_infer_rnnt.py
