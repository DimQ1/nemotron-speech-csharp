# Конвертация Nemotron ASR под opset 24 (CPU)

Дата: 2026-07-21
Модель: `nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b`
Целевая платформа: **CPU**, C# ONNX Runtime **1.25.1** (через OnnxRuntimeGenAI 0.14.1 + override)

---

## 1. Что сделано

Сконвертированы ASR-модели с нативным **opset 24** для всех компонентов
(encoder / decoder / joint) в двух прецизионах и двух streaming-окнах:

| Модель | Прецизион | Окно (chunk) | encoder.data | Статус E2E |
|---|---|---|---:|---|
| `cpu-opset24-fp32-c056` | FP32 | 0.56s | 2380 MB | ✅ |
| `cpu-opset24-fp32-c112` | FP32 | 1.12s | 2380 MB | ✅ |
| `cpu-opset24-int4-c056` | INT4 (MatMulNBits) | 0.56s | 650 MB | ✅ |
| `cpu-opset24-int4-c112` | INT4 (MatMulNBits) | 1.12s | 650 MB | ✅ |

Все 4 модели загружаются и транскрибируют на C# ORT 1.25.1 (проверено E2E).

### Фактический opset графов
- encoder / decoder / joint: `ai.onnx: 24` ✅
- **Swish экспортируется как `Mul`+`Sigmoid`** (не нативный `Swish-24`) →
  кастомный Swish-kernel **не требуется**, модели работают «из коробки».
- **`Attention` (opset 23/24) НЕ появился** — dynamo-экспортёр не распознал
  SDPA-паттерн NeMo RelPosition-MHA (из-за ветвления с масками/causal).
  Граф операторов идентичен opset-21 версии, изменился только штамп opset.

> Вывод: переход на opset 24 для этой модели даёт **лишь обновление штампа** —
> никаких новых фьюженных операторов. Это подтверждает более ранний анализ
> (`Doc/opset24-analysis.md`): реальную выгоду даёт обновление рантайма ORT,
> а не смена opset. Модели opset 24 тем не менее валидны и совместимы с ORT 1.25.1.

---

## 2. Поддержка разных окон (chunk_size) для точности ASR

Реализована **параметризация streaming-окна** при экспорте. Чем больше окно,
тем больше контекста видит encoder → выше потенциальная точность (ниже WER),
ценой большей задержки и памяти.

### Как задать окно

```bash
# 0.56s — рекомендуемое NVIDIA (баланс latency/accuracy)
python src/optimize.py --encoder-precision fp32 --target-opset 24 --chunk-size 0.56

# 1.12s — максимальный контекст (лучшая точность)
python src/optimize.py --encoder-precision fp32 --target-opset 24 --chunk-size 1.12

# также доступны 0.08 / 0.16 (минимальная задержка)
```

### Подтверждённые различия в shapes

| Параметр | chunk 0.56 | chunk 1.12 |
|---|---:|---:|
| `chunk_samples` | 8960 | 17920 |
| `audio_signal` | [1, 65, 128] | [1, 121, 128] |
| `cache_last_channel` | [1, 24, 70, 1024] | [1, 24, 140, 1024] |
| `left_context` | 70 | 140 |

Приложение (`ModelSession`) читает `chunk_samples` из `genai_config.json` —
никаких изменений кода для смены окна не требуется.

### Наблюдение по точности (субъективно, на HIN_M_AbhishekS.mp3, Hindi+English)
- **FP32 c056 vs c112**: c112 дал более связный текст с сохранением Hindi-сегментов.
- **INT4 c056 vs c112**: разница заметнее — c056 потерял Hindi-часть почти полностью,
  c112 частично восстановил (больше контекста компенсирует квант-шум).
- **Вывод**: большее окно (1.12s) особенно полезно для **INT4** и для
  **код-свитчинга / многоязычного** аудио.

---

## 3. Производительность (CPU i7-8700K, sample-0.mp3 = 10.1s аудио)

| Модель | encoder.data | Время инференса |
|---|---:|---:|
| INT4 c056 | 650 MB | 5.35 s |
| INT4 c112 | 650 MB | 5.28 s |
| FP32 (любое окно) | 2380 MB | (см. BenchmarkSuite1 ~2.6–3.2 s/op в BDN) |

- INT4 в **3.7× меньше** FP32 (650 vs 2380 MB).
- Размер не зависит от окна (веса те же, меняются только shapes/кэши).

---

## 4. Технические изменения в конвертере

### Окружение
- `torch 2.13.0`, `onnx 1.22.0` (opset до 27), `onnxruntime 1.23.2`.
- C# рантайм: `Microsoft.ML.OnnxRuntime 1.25.1` (поддерживает opset 24 + native
  `Attention` на CPU — проверено отдельным тестом `work/Opset24Check`).

### Изменённые/новые файлы
- `converter/src/nemotron_model_load.py` — `CHUNK_SIZE` стал ленивым
  (`_chunk_size()` читает `NEMOTRON_CHUNK_SIZE` при обращении, не при импорте).
- `converter/src/optimize.py`:
  - CLI `--target-opset` (переопределяет opset во всех OnnxConversion passes);
  - CLI `--chunk-size {0.08,0.16,0.56,1.12}`;
  - `_to_new_run_format()` — адаптер legacy-конфигов под схему Olive ≥ 0.7
    (`passes{NAME:{type,config}}`, `pass_flows`, `engine{}`);
  - уникальный `workflow_id` по chunk+opset (иначе Olive переиспользовал кэш
    экспорта от другого окна → одинаковые shapes при разных chunk);
  - совместимость с `olive.workflows.run` (Olive 0.8 убрал top-level `olive.run`).
- `converter/src/patch_olive_torch_export.py` — runtime-патч: torch 2.12+
  убрал kwarg `fallback` из `torch.onnx.export`, а Olive 0.8 его передаёт.
- `converter/src/flatten_output.py` — раскладывает вложенный вывод Olive
  (`<comp>.onnx/output_model/model/model.onnx`) в плоскую структуру приложения.
- `converter/src/quantize_int4.py` — прямая INT4-квантизация FP32 encoder через
  `onnxruntime.quantization.matmul_nbits_quantizer.MatMulNBitsQuantizer`
  (Olive 0.8 ссылается на `matmul_4bits_quantizer`, отсутствующий в ORT 1.23.2;
  `OnnxKQuantQuantization` переименован в `OnnxMatMul4Quantizer`).

### Известные несовместимости (workaround применён)
| Проблема | Причина | Решение |
|---|---|---|
| `olive.run` ImportError | Olive 0.8 переместил API | `olive.workflows.run` |
| `fallback` kwarg | torch 2.12+ удалил | runtime-патч export |
| `no_artifacts` / формат | Olive 0.7+ новая схема | `_to_new_run_format()` |
| одинаковые shapes при разном chunk | кэш Olive | уникальный `workflow_id` |
| `OnnxKQuantQuantization` не найден | переименован | `OnnxMatMul4Quantizer` |
| `matmul_4bits_quantizer` нет | ORT 1.23.2 | прямой `MatMulNBitsQuantizer` |

---

## 5. Рекомендации

1. **Для продакшена на CPU — INT4 c112**: лучший баланс размера (650 MB),
   скорости и точности (большее окно компенсирует квант-шум).
2. **opset 24 не обязателен** — тот же результат даёт opset 21 + ORT 1.25.1
   (автофузия MHA работает и на старом штампе). Но opset 24-модели валидны и
   совместимы, можно использовать как основу.
3. **FP32** имеет смысл только если критична максимальная точность и не важен
   размер/память.
4. При смене окна переконвертировать нужно **только encoder** (decoder/joint от
   окна не зависят) — это ускоряет итерации.
