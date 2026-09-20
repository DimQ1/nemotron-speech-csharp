# Что из ONNX opset 23/24 реально применимо к нашим моделям (ORT 1.30, CPU)

Дата: 2026-09-19. Дополняет и уточняет `opset24-analysis.md` (тот анализ был
сделан под ORT 1.23–1.25 и только для Nemotron).

Проверено по `docs/OperatorKernels.md` тега `rel-1.30.0` и через
`onnx.defs.get_schema` (onnx 1.22 в `.venv`), плюс дамп операторов локальных графов
(`build/graph_ops.py`).

---

## 1. Механизм: что является настоящим оператором, а что раскрывается в узлы

| Оператор | Opset | В ONNX | CPU-ядро в ORT 1.30 | Типы |
|---|---:|---|---|---|
| `Attention` (ai.onnx) | 24+ | настоящий оп | ✅ | float, float16 |
| `GroupQueryAttention` (com.microsoft) | 1+ | настоящий оп | ✅ | float, float16; кэш float/fp16/int8/uint8 |
| `RMSNormalization` (ai.onnx) | 23+ | настоящий оп | ✅ | double, float, float16 |
| `RotaryEmbedding` (ai.onnx) | 23+ | настоящий оп | ✅ | float, float16 |
| `TensorScatter` (ai.onnx) | 24+ | настоящий оп | ✅ | все числовые типы |
| `MultiHeadAttention` (com.microsoft) | 1+ | настоящий оп | ✅ | float |
| **`Swish` (ai.onnx)** | 24+ | **function-оп, 5 узлов в теле** | **нет строки в таблице** | — |

> **Поправка к `opset24-analysis.md`.** Там записано, что CPU-ядро `Swish-24`
> «уже присутствует в релизной сборке — сессия создаётся и вычисляет корректно без
> custom ops» (эксперимент 2026-07-21). В таблице ядер 1.30 строки `Swish` нет ни в CPU,
> ни в CUDA (единственное совпадение по подстроке — `HardSwish` в CUDA).
> Корректная работа объясняется тем, что `Swish` в ONNX задан **функцией** из
> `Sigmoid`+`Mul`, то есть граф просто раскрывается обратно в 96 пар `Sigmoid`/`Mul`.
> Выигрыша в скорости это не даёт, и как оптимизацию `Swish-24` рассматривать не нужно.

Ключевые входы/выходы, ради которых всё затевается:

- `Attention-24`: `Q, K, V, attn_mask, past_key, past_value, nonpad_kv_seqlen` →
  `Y, present_key, present_value, qk_matmul_output` (KV-кэш внутри оператора).
- `GroupQueryAttention`: `query, key, value, past_key, past_value, seqlens_k,
  total_sequence_length, cos_cache, sin_cache, position_ids, attention_bias, …,
  q_norm_weight, k_norm_weight` → `output, present_key, present_value`.
  То есть GQA умеет **RoPE (cos/sin cache) и QK-norm (q_norm/k_norm) внутри себя**.

## 2. Что из этого применимо к нашим моделям

### Qwen3-ASR — главный кандидат

Факты по графам (`encoder.int4.onnx`, `decoder_init/step.int4.onnx`, opset **18**):

- Декодер: 28 слоёв, KV-голова `[28, batch, 8, past_seq, 128]` — **GQA, 8 KV-голов**,
  head_dim 128. 197 `MatMulNBits` (int4) на граф.
- `SimplifiedLayerNormalization` уже 113 (это и есть RMSNorm-ядра, включая q_norm/k_norm).
- **Внимание не сфьюжено**: 28 `Softmax`, 147 `Transpose`, и на каждый шаг
  `Concat` 120 + `Slice` 114 для наращивания KV-кэша; кэш ходит через хост целиком
  (`past_keys`/`past_values` → `present_keys`/`present_values`).
- Энкодер: 195 `MatMul` **fp32** — так задумано: `tools/converters/Qwen3Asr/quantize_int4.py`
  прямо пишет «keeps the encoder in FP32 for CPU performance», а имя `encoder.int4.onnx`
  оставлено для совместимости с C#-провайдером.

Что даёт `GroupQueryAttention` на CPU:

1. Один узел вместо блока attention (сейчас `Transpose`+`MatMul`+`Softmax`+`MatMul`+`Transpose`).
2. `past_present_share_buffer` — обновление KV-кэша **на месте**, без выгрузки/загрузки
   всего кэша на хост. Замерено: только копирование `ToArray` для logits/keys/values
   это **1.7 с из 26 с** (6.5%) на 5 файлах, плюс внутренние копии ORT.
3. `cos_cache`/`sin_cache` + `q_norm_weight`/`k_norm_weight` убирают отдельные узлы
   RoPE и QK-norm.

Качество: математика та же, fusing не меняет результат — но обязательна проверка WER
(ожидаемое расхождение только на уровне порядка суммирования, как у любого фьюза).

### VibeVoice — самый «раздутый» граф

Факты (`decoder_step.int4.onnx`, opset **18**): **7520 узлов**, из них
`Constant` 2380, `Unsqueeze` 1550, `Shape` 422, `Cast` 288, плюс
**ручной RMSNorm**: `Pow` 57 + `ReduceMean` 57 + `Sqrt` 113 + `Div` 113.
KV-кэш — **56 отдельных тензоров** (`past_key_0..27`, `past_value_0..27`), 2 KV-головы
(тоже GQA), 197 `MatMulNBits`.

Что применимо:

1. `GroupQueryAttention` (или `Attention-24`) — сворачивает 28 блоков внимания и
   56 тензоров кэша в один оператор на слой; убирает большую часть из 1550 `Unsqueeze`
   и 2380 `Constant`, которые обслуживают ручную сборку кэша и форм.
2. `RMSNormalization-23` (или `SimplifiedLayerNormalization`) вместо
   `Pow`+`ReduceMean`+`Sqrt`+`Div` ×113.

Профиль: шаг декодера — это 7520 узлов на **один токен**; при ~500 шагах на 30 с аудио
это ~3.8 млн исполнений узлов. Даже при 1 мкс на узел это секунды чистого overhead
диспетчеризации, поэтому сокращение числа узлов здесь важнее, чем FLOPs.

### Nemotron 3.5 — как в исходном анализе

`encoder.onnx` (opset 21): 24 блока MHA с относительным позиционным байасом
(`Softmax` 24, `Where` 72, `Gather` 48, `Pad` 48, `Slice` 100), 219 `MatMulNBits`,
144 `LayerNormalization`, `Sigmoid`+`Mul` 96 (Swish — см. поправку выше).

- `Attention-24` применим: переводит 24 блока в один узел на слой, float-маска может
  принять относительный байас.
- Отдельно: ORT 1.25 добавил «Nemotron speech conformer encoder MHA fusion» — это
  **runtime-фузия поверх существующего графа**, и она уже работает: мы на 1.30.
  То есть часть выигрыша получена апгрейдом, без переэкспорта.
- `RMSNormalization`/`RotaryEmbedding` неприменимы (Conformer использует LayerNorm и
  relative position, не RoPE).

### Parakeet TDT

Имеет смысл проверить отдельно (графы в `tools/converters/ParakeetTdt`), но TDT-декодер
не transformer-LLM, а FastConformer-энкодер построен как и Nemotron — те же выводы.

## 3. Что НЕ применимо (проверено, не тратить время)

- **FP8 / FP4 / MXFP4** в `QuantizeLinear`/`DequantizeLinear` — нет CPU-ядер.
- **Paged KV cache / `PagedAttention`** — CUDA/WebGPU (и в 1.30 добавили INT4-кэш только там).
- **`VarlenCausalConvWithState`, `GatedDeltaNet`, FP4 QMoE, `SparseAttention`** — CUDA-only
  либо архитектуры, которых у нас нет.
- **KleidiAI** — ARM, для x64 неактуально.
- **`MatMulNBits` accuracy_level / block_size** — не новый опсет, но единственные
  качество-скоростные ручки в текущей квантованной сборке; менять только с замером WER.

## 4. Побочный выигрыш, не связанный с опсетами

`onnxruntime.tools.convert_onnx_models_to_ort` — переводит граф в ORT-формат с уже
применёнными оптимизациями. Для VibeVoice это снимает ~2 с создания сессии на граф
(замерено) и часть работы по разбору 7520-узлового графа. Численно ничего не меняет,
но требует поставки `.ort` вместо `.onnx`.

## 5. Выводы по приоритету

| Приоритет | Что | Модель | Выигрыш | Риск качества | Трудозатраты |
|---|---|---|---|---|---|
| 1 | `GroupQueryAttention` + общий KV-буфер | Qwen3-ASR декодер | убирает хост-копии кэша (6.5% замерено) и ~400 узлов на граф | нейтрально, нужен WER | переэкспорт + проверка |
| 2 | GQA + `RMSNormalization` | VibeVoice декодер | 7520 → сотни узлов, 56 тензоров кэша → один | нейтрально, нужен WER | переэкспорт |
| 3 | `Attention-24` | Nemotron энкодер | 24 блока → 24 узла; часть уже даёт runtime-фузия 1.25+ | нейтрально, нужен WER | переэкспорт |
| — | ~~`Swish-24`~~ | Nemotron | **нет** (function-оп, раскрывается обратно) | — | — |

Всё это — проекты переэкспорта моделей (Python-конвертеры + замер WER на CV17), а не
правка C#. Проверять надо по одной модели за раз: сначала прототип на одном графе
(например `decoder_step` Qwen3), убедиться, что CPU-ядро GQA действительно быстрее
несфьюженного варианта при наших длинах последовательностей (26–400 токенов), и только
потом переэкспортировать остальное.
