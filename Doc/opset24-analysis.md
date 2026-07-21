# Анализ: переход на opset 24 при конвертации Nemotron ASR (FP32 / INT8 / INT4)

Дата: 2026-07-20
Модель: `nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b`
Анализируемые ONNX-сборки: `DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-{fp32,int8,int4}-cpu`

---

## 1. Текущее состояние моделей (фактические данные)

Все три сборки экспортированы через Olive (`OnnxConversion`, dynamo exporter),
`target_opset: 21` (см. `converter/src/nemotron_*.json`).

| Компонент | FP32 | INT8 (DimQ1) | INT4 (DimQ1) |
|---|---|---|---|
| **encoder.onnx** opset | 21 | 21 + com.microsoft | 21 + com.microsoft |
| encoder узлов | 1894 | 1894 | 1894 |
| encoder размер (веса) | 2495 MB | 967 MB | 690 MB |
| квант-оператор | — | `MatMulNBits` ×219 | `MatMulNBits` ×219 |
| **decoder.onnx** opset | 21 | 21 | 21 |
| decoder веса | 59.8 MB | 59.8 MB | 59.8 MB (FP32 LSTM ×2) |
| **joint.onnx** opset | 21 | 21 | 21 |
| joint веса | 37.8 MB | 37.8 MB | 37.8 MB (FP32) |
| **silero_vad.onnx** | opset 16 | opset 16 | opset 16 |

> Примечание: в DimQ1 INT8-сборке encoder фактически квантован через
> `MatMulNBits` (contrib-op com.microsoft, k-quant блоки UINT8), а не через
> классический INT8 Q/DQ (`QuantizeLinear`/`MatMulInteger`). Гистограмма
> операторов INT8 и INT4 сборок идентична, отличается только ширина бит
> (`bits=8` vs `bits=4` в атрибутах MatMulNBits) и размер весов.
> Эталонная INT8 Q/DQ-версия (soniqo, opset 17) имеет 7000 узлов —
> `DynamicQuantizeLinear` ×249, `MatMulInteger` ×219, `ConvInteger` ×77 —
> и заметно тяжелее по графу.

### Гистограмма операторов encoder (одинакова для всех трёх сборок)

```
Transpose 246, MatMulNBits 219 (или MatMul 291 в FP32), Add 183, Reshape 169,
Mul 153, LayerNormalization 144, Slice 100, Sigmoid 96, Conv 77, Concat 75,
MatMul 72, Where 72, Unsqueeze 68, Gather 48, Pad 48, Div 27, Softmax 24,
Split 24, Cast 11, ...
```

Архитектура: 24 слоя streaming Conformer (d_model=1024), RNN-T decoder
(2×LSTM 640), joint-сеть. Внимание — **обычный MHA с relative position
bias** (не GQA, не RoPE), активация — **Swish** (`Mul` + `Sigmoid` ×96).

---

## 2. Что даёт opset 23/24 (факты из ONNX Changelog и ORT release notes)

Новые/изменённые операторы ai.onnx:

| Opset | Ключевые нововведения |
|---|---|
| 22 | LSTM/GRU/RNN (FP16-входы), trig-опы, DeformConv, GridSample |
| 23 | **Attention** (SDPA: MHA/GQA/MQA), **RMSNormalization**, **RotaryEmbedding**, FP8 в QuantizeLinear/DequantizeLinear |
| 24 | **Attention v2** (past/present KV, qk_matmul_output), **Swish**, **TensorScatter** (KV-cache update), TopK, SplitToSequence |

Поддержка в ONNX Runtime (проверено по `OperatorKernels.md` и релизам):

| Оператор | CPU | CUDA | DML | ORT версия |
|---|---|---|---|---|
| Attention-23 | ✅ fp32/fp16 | ✅ (+GQA, softcap) | ❌ | 1.23+ |
| Attention-24 (KV in/out) | ✅ (nonpad KV seqlen) | ✅ | ❌ | 1.25 |
| RMSNormalization-23 | ✅ | ✅ | ❌ | 1.23 |
| RotaryEmbedding-23 | ✅ | ✅ | ❌ | 1.23 |
| Swish-24 | ✅ **(есть в ORT 1.25.1, проверено экспериментально 2026-07-21)** | ❌ | ❌ | 1.25.1 |
| TensorScatter-24 | ✅ | ✅ | ❌ | 1.25 |

> **Поправка (2026-07-21):** первоначальный анализ опирался на
> `OperatorKernels.md`, где Swish-24 отсутствовал. Эксперимент с моделью
> `Swish` opset 24 на `Microsoft.ML.OnnxRuntime` 1.25.1 показал, что CPU-kernel
> Swish-24 **уже присутствует** в релизной сборке — сессия создаётся и
> вычисляет корректно без всяких custom ops. Тем не менее custom-op DLL
> (`NemotronSpeech/Native/`) сохранена как запасной путь для старых ORT
> (≤1.23, например транзитивный ORT из `OnnxRuntimeGenAI.Cuda` 0.14.1).

**Текущая установка**: ORT 1.23.0 → `ai.onnx` max **opset 23**.
Opset 24 полностью (включая Swish) поддерживается только начиная с ORT 1.25.
Локальный `onnx` 1.17.0 экспортирует максимум opset 22 — **для конвертации
в opset 24 нужно обновить `onnx` до ≥1.19 и `onnxruntime` до ≥1.25**, а также
проверить Olive/torch.onnx dynamo-экспортёр (torch ≥ 2.9/2.10 экспортирует
opset 23+; opset 24 поддерживается dynamo-экспортёром в свежих nightly).

Дополнительно из ORT 1.25 release notes (прямо относится к этой модели):
- **«Nemotron speech conformer encoder MHA fusion»** (#27764) — ORT добавил
  специальную фузию DQ→MatMulNBits→MHA именно для этого энкодера.
- MatMulNBits: 2-bit zero-point, выше K-параллелизм, DP4A SmallM tiling —
  ускорение INT4 на CPU без смены opset.

---

## 3. Применимость к конкретным операторам модели

### 3.1 Что РЕАЛЬНО может улучшиться

| Паттерн в графе | Сейчас (opset 21) | С opset 23/24 | Выигрыш |
|---|---|---|---|
| **MHA**: `Transpose→MatMul→(Add rel-pos-bias)→Softmax→MatMul→Transpose` ×24 | 6–10 узлов на слой, Softmax отдельно | 1 узел `Attention` | Меньше промежуточных тензоров, фьюженные SDPA-ядра (CPU fp32/fp16, CUDA flash-attn). **Главный кандидат.** |
| **Swish**: `Sigmoid` ×96 + `Mul` ×96 (24 слоя × conv-module + GLU-подобные) | 2 узла ×96 | 1 узел `Swish` ×96 | −96 узлов; но ⚠️ нет CPU-кernels в ORT ≤1.25 — упадёт в fallback/ошибку. Только после появления kernels. |
| **LSTM decoder** | LSTM-14 (opset 21 импорт) | LSTM-22 (FP16-входы, улучшения) | Маргинально; decoder FP32, выигрыш ~0. |
| **Relative position bias** | `Where` ×72, `Gather` ×48, `Pad` ×48, `Slice` ×100 | Остаются как есть (Attention принимает готовый float-байас через attn_mask) | Частично упростится, если rel-pos-bias подать как `attn_mask` (T2, fp32) — возможно, т.к. Attention-23/24 допускает float-маску того же типа. |

### 3.2 Что НЕ улучшится (архитектурно неприменимо)

- **RMSNormalization** — в модели используется `LayerNormalization` ×144 (Conformer), не RMSNorm. Бесполезно.
- **RotaryEmbedding** — Nemotron Conformer использует **relative positional
  encoding** (Transformer-XL style), не RoPE. Бесполезно.
- **TensorScatter** — предназначен для KV-cache в decoder-only LLM. Здесь
  streaming-кэш Conformer'а (`cache_last_channel`, `cache_last_time`)
  обновляется через `Concat`+`Slice` и это работает; TensorScatter семантически
  не подходит (это не KV-cache внимания, а кэш активаций/conv).
- **GQA/MQA-фичи Attention** — модель plain MHA (q_num_heads == kv_num_heads).
- **FP8 в QuantizeLinear/DequantizeLinear** — для CPU не актуально, для CUDA
  возможно позже.

### 3.3 Риски перехода на opset 24

1. **Потеря фузий ORT**: ORT graph-optimizers (в т.ч. свежая «Nemotron
   conformer MHA fusion» в 1.25) работают поверх знакомых паттернов
   (MatMul+Softmax+MatMul). Если экспортировать уже слитый `Attention`,
   CPU-путь в ORT 1.23 упадёт (нет поддержки opset 24), а в 1.25 выигрыш
   против нативной фузии #27764 надо бенчмаркать — не факт, что ручной
   `Attention` быстрее, чем автофузия.
2. **MatMulNBits — contrib-op com.microsoft**: он НЕ меняется при смене
   opset. INT8/INT4-квантование останется на `MatMulNBits` независимо от
   `target_opset`. Размеры и скорость квантованных моделей от opset 24
   **не изменятся**.
3. **Swish-24 без CPU kernels**: замена `Sigmoid+Mul` на `Swish` приведёт к
   неподдерживаемому узлу на CPU EP (по состоянию на ORT 1.25) — риск ошибки
   загрузки или падения производительности (fallback разбивает граф).
4. **Совместимость со стеком**: проект использует `Microsoft.ML.OnnxRuntimeGenAI`
   0.14.1 / `OnnxRuntimeGenAI.Cuda` 0.15.0-dev — они тащат ORT ~1.22–1.23.
   Для opset 24 потребуется обновление GenAI до сборки на ORT ≥1.25, иначе
   модель просто не загрузится.

---

## 4. Рекомендации по прецизионам

### FP32 (encoder 2495 MB)
- **Рекомендуется**: попробовать `target_opset: 23` (не 24) + флаги
  оптимизации ORT. На opset 23 модель загрузится в текущем ORT 1.23 и в
  GenAI 0.14/0.15, а ORT 1.25 сам сфузит MHA (#27764).
- Экспорт с явным `Attention` имеет смысл только для CUDA-сборки и после
  бенчмарка против автофузии.
- Дополнительно (независимо от opset): **FP16-конвертация encoder'а**
  уменьшит веса вдвое (~1.2 GB) с минимальной потерей WER для CPU fp16-
  kernels в ORT 1.23+.

### INT8 (MatMulNBits k-quant, 967 MB)
- Смена opset **ничего не даст** — квант-путь на MatMulNBits не зависит от
  ai.onnx opset. Оставить `target_opset: 21`.
- Реальные улучшения придут из ORT 1.25 (MatMulNBits DP4A tiling,
  DQ→MatMulNBits fusion) — обновить рантайм, не трогая модель.

### INT4 (MatMulNBits k-quant, 690 MB)
- Аналогично INT8: opset не влияет. Оставить 21.
- Проверить `block_size`/`accuracy_level` MatMulNBits и новые режимы
  (2-bit zero-point в ORT 1.25) — потенциал дальнейшего сжатия до ~500 MB
  без смены opset.

### Decoder + Joint (FP32, ~98 MB суммарно)
- Можно квантовать независимо от encoder: decoder LSTM — через
  `DynamicQuantizeLSTM` (com.microsoft), joint (3 MatMul) — через
  `MatMulNBits`. Это −60–70 MB и ускорение токен-степа. Opset не играет роли.

---

## 5. Итоговый вердикт

| Вопрос | Ответ |
|---|---|
| Даст ли opset 24 ускорение FP32? | Потенциально да (единый `Attention`, −96 узлов Swish), но **только на ORT ≥1.25 и после бенчмарка против автофузии MHA**; на текущем стеке (ORT 1.23 / GenAI 0.14–0.15) модель не загрузится. |
| Даст ли opset 24 улучшение INT8/INT4? | **Нет.** Квантование на `MatMulNBits` (com.microsoft) не зависит от ai.onnx opset. |
| Что делать сейчас? | 1) Оставить `target_opset: 21` для INT8/INT4. 2) Для FP32 попробовать **opset 23** (совместим с ORT 1.23). 3) Обновить ORT до 1.25+ ради нативной Nemotron-MHA-фузии и MatMulNBits-улучшений — это даст больше, чем смена opset. 4) Обновить локальный `onnx` до ≥1.19 перед любыми экспериментами с opset 23/24. 5) Проверить квантование decoder/joint. |

---

## Приложение: проверенные источники
- ONNX Changelog (main): новые опы opset 21–24 — перечислены выше.
- ORT `docs/OperatorKernels.md`: Attention 23+/24+ CPU fp32/fp16; RMSNorm 23+; RotaryEmbedding 23+; TensorScatter 24+; **Swish — отсутствует**.
- ORT v1.25.0 release notes: «Attention opset 24 on CUDA», «TensorScatter-24 CPU+CUDA», «Nemotron speech conformer encoder MHA fusion #27764», MatMulNBits DP4A/2-bit-zp.
- Локально: ORT 1.23.0 → max ai.onnx opset 23; onnx 1.17.0 → max opset 22.
