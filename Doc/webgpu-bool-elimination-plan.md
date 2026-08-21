# План: устранение CPU-preferred узлов для WebGPU graph capture

## Диагностика

Проблемная цепочка в энкодере:

```
[82] Less(float,float)->bool
[85] And(bool,bool)->bool
[86] Unsqueeze(bool)
[87] Expand(bool)
[88] Tile(bool)
[89] Transpose(bool)
[90] And(bool,bool)->bool
[91] And(bool,bool)->bool
[92] Not(bool)->bool
[95] Slice(bool data)  ← CPU-preferred!
```

Плюс ещё 4 `Less` узла ([6], [21], [39], [59]) с цепочкой:
```
Less(bool) → Unsqueeze → Expand → Cast(bool->float) → Mul(float)
```
Эти уже используют Cast для конвертации в float, но всё равно проходят через bool.

## Корневая причина

ORT `GetCpuPreferredNodes` видит bool-тензоры, идущие в `Slice`, и помечает ВСЮ цепочку как CPU-preferred (shape-calc subgraph). Даже если каждый узел по отдельности поддерживается WebGPU, ORT агрессивно выносит bool→Slice на CPU.

## План замены (порядок: сначала data, потом контроль)

### Шаг 1: Less → немедленный Cast в float

**Замена:**
```
Less(float, float) → bool
```
на:
```
Less(float, float) → bool (оставляем)
Cast(bool → float) → float      ← добавляем сразу за Less
```

**Причина:** Сразу после `Less` конвертировать bool→float. WebGPU Cast(bool→float) нативный. Дальше вся цепочка работает с float.

### Шаг 2: And(bool,bool) → Mul(float,float)

**Замена:**
```
And(bool, bool) → bool
```
на:
```
Mul(float, float) → float
```

**Причина:** Для 0.0/1.0 значений `x * y ≡ x ∧ y`. WebGPU Mul(float) — нативный. Не нужен Cast.

### Шаг 3: Not(bool) → Sub(1.0, float)

**Замена:**
```
Not(bool) → bool
```
на:
```
Sub(const_1.0, float) → float
```

**Причина:** Для 0.0/1.0 значений `1.0 - x ≡ ¬x`. WebGPU Sub(float) — нативный.

### Шаг 4: Очистка Cast-ов

После замены bool→float, промежуточные Cast(bool↔float) становятся identity. Удалить:
- `Cast(bool→float)` — удалить, граф уже float
- `Cast(float→bool)` — удалить, подставить float напрямую

### Шаг 5: Slice(bool) → Slice(float)

После шагов 1-4, все данные в Slice уже float. Slice(float) — полностью нативный WebGPU.

### Шаг 6: Финальная верификация

После всех замен:
1. `onnx.checker.check_model()` — валидация графа
2. `Not/And` узлов = 0
3. Bool входа в Slice = 0
4. Тест: `NemotronSpeech.exe ... webgpu` — НЕ должен упасть с «all compute graph nodes have not been partitioned»

## Оценка рисков

| Риск | Вероятность | Митигация |
|---|---|---|
| Mul вместо And даёт неверный результат для не-0/1 значений | Низкая | Значения гарантированно 0/1 (выход Less/Not) |
| Sub(1,x) даёт -1 вместо 0 для x=2 | Низкая | x всегда 0 или 1 |
| Slice не принимает int32 данные | Средняя | Cast(int32→float) перед Slice, Cast назад после |
| Graph capture всё равно не работает | Средняя | Проверить GetCpuPreferredNodes на оставшихся узлах |
| Модель выдаёт другой транскрипт | Низкая | Эквивалентные математические замены |

---

## Результаты реализации (2026-07-31)

### Скрипт: `transform_encoder.py`

Полный скрипт трансформации — преобразует `modules/asr/webgpu-int4/encoder.onnx` → `modules/asr/webgpu-graph-int4/encoder.onnx`.

**Фактические замены:**

| Фаза | Узлы | Действие |
|---|---|---|
| 1 | Less[6,21,39,59] | Cast(bool→float) сразу за Less. Удалены ставшие no-op Cast[9,24,42,62] |
| 2 | Less[82], GreaterOrEqual[84] | Cast(bool→float) |
| 3 | And[85,90,91] | → Mul(float, float) |
| 4 | Not[92,93] | → Sub(1.0, float) |
| 5 | Initializer `alias` [1,77,77] | bool→float32 (attention mask) |
| 5b | `unsqueeze_24`, `unsqueeze_25` | Cast(float→bool) перед Where (нужен bool condition) |
| 6 | 29 value_info bool entries | Удалены устаревшие объявления типов |

**Итог:** 0 Not, 0 And, 0 bool value_info. ONNX checker проходит.

### Проверка модели

- **Транскрипция:** идентична baseline INT4 (10.1s аудио, 58 токенов)
- **Avg latency (graph-int4):** ~241ms vs baseline INT4 ~201ms (+20%)
- **Причина замедления:** дополнительные Cast/Mul/Sub узлы. Ожидается компенсация при включении graph capture

### Graph Capture

- **Ключ:** `enableGraphCapture` (camelCase), session config option (не provider option)
- **Статус:** PR #6288 влит 2026-07-31, но ещё не вошёл в ORT 1.28.0 / EP.WebGpu 0.2.1
- **Настройка в genai_config.json:** `"session_options": {"enableGraphCapture": "1"}` (будет работать после обновления ORT)
- **Ожидаемый эффект:** устранение per-op dispatch overhead (~10-30ms), компенсация overhead от Cast узлов

### Следующие шаги

1. Обновить ORT и EP.WebGpu до версий с поддержкой graph capture
2. Перетестировать `webgpu-graph-int4` с `enableGraphCapture: "1"`
3. Ожидаемый RTF: ~3.5-4.0x (лучше baseline 2.5x)
