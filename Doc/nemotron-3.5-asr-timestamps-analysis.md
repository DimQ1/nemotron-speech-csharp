# Анализ: временные метки в nvidia/nemotron-3.5-asr-streaming-0.6b

**Дата:** 2026-07-11  
**Модель:** [nvidia/nemotron-3.5-asr-streaming-0.6b](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)  
**Архитектура:** FastConformer-CacheAware-RNNT (600M параметров)

---

## Краткий ответ

**Да, модель может возвращать временные метки для распознанного звука.**

Поддержка реализована через API 🤗 Transformers (v5.13.0+). Метод `generate()` возвращает не только последовательности токенов, но и пошаговые длительности (`durations`), которые `processor.decode()` преобразует в токен-уровневые временные метки в секундах.

---

## Как это работает

### 1. Генерация: `model.generate()`

Метод `generate()` (унаследованный от `ParakeetRNNTGenerationMixin`) выполняет RNN-T декодирование и собирает:
- `sequences` — выходные токены
- `durations` — пошаговые продвижения по кадрам энкодера (0/1 на каждый шаг)

```python
# generation_nemotron_asr_streaming.py, строка 1760:
# "Parakeet's generate() runs the decoding loop and assembles sequences + per-step durations."
```

Возвращается объект `NemotronAsrStreamingGenerateOutput(sequences=..., durations=...)`.

### 2. Декодирование с метками: `processor.decode()`

Метод `Nemotron3_5AsrProcessor.decode()` принимает параметр `durations`:

```python
# processing_nemotron3_5_asr.py, строки 2354-2654:
def decode(self, *args, durations=None, **kwargs):
    """
    Forward arguments to PreTrainedTokenizer.decode and post-process the
    token-level timestamps (if durations are provided) as in the NeMo library.
    """
```

Алгоритм преобразования durations → временные метки:

1. **Кумулятивная сумма** durations → позиция кадра для каждого токена
2. **Фильтрация** padding/blank токенов
3. **Конвертация** индексов кадров в секунды:

   ```
   frame_rate = hop_length / sampling_rate * subsampling_factor
   start_sec = frame_index * frame_rate
   end_sec = (frame_index + 1) * frame_rate
   ```

4. Возвращает `(decoded_text, timestamps)`

---

## Формат временных меток

Метки возвращаются на **уровне токенов/символов** (не слов):

```python
[
    {"token": "h", "start": 0.0,   "end": 0.32},
    {"token": "e", "start": 0.32,  "end": 0.64},
    {"token": "l", "start": 0.64,  "end": 0.96},
    {"token": "l", "start": 0.96,  "end": 1.28},
    {"token": "o", "start": 1.28,  "end": 1.60},
    ...
]
```

Каждый RNN-T токен охватывает **ровно один кадр энкодера**.  
Длительность кадра = `subsampling_factor × hop_length / sampling_rate`.

| Параметр | Значение |
|---|---|
| `hop_length` | 160 (10ms при 16kHz) |
| `subsampling_factor` | 8 |
| Длительность кадра | 8 × 160 / 16000 = **80ms** |
| С включенным lookahead | 80ms × (lookahead + 1) |

---

## Важные ограничения

| Аспект | Детали |
|---|---|
| **Гранулярность** | Посимвольная / токен-уровневая (**не** пословная) |
| **Разрешение** | 1 кадр энкодера (~80ms с шагом subsampling) |
| **API** | Только через `transformers`; NeMo inference возвращает чистый текст |
| **Стриминг** | `durations` доступны и в потоковом (streaming) режиме |
| **Версия transformers** | ≥ 5.13.0 |

---

## Пример использования

### Offline (полный файл)

```python
from transformers import AutoProcessor, Nemotron3_5AsrForRNNT
import torch

model_id = "nvidia/nemotron-3.5-asr-streaming-0.6b"
processor = AutoProcessor.from_pretrained(model_id)
model = Nemotron3_5AsrForRNNT.from_pretrained(model_id, device_map="auto")

# Загрузка аудио
import librosa
audio, sr = librosa.load("audio.wav", sr=16000)

# Подготовка входа
inputs = processor(audio, language="ru-RU", return_tensors="pt")
inputs = inputs.to(model.device, dtype=model.dtype)

# Генерация
output = model.generate(**inputs)

# Декодирование с временными метками
text, timestamps = processor.decode(
    output.sequences[0],
    durations=output.durations[0]
)

print(text)
# => "привет мир"

for ts in timestamps:
    print(f"  [{ts['start']:.2f}s - {ts['end']:.2f}s] {ts['token']}")
# => [0.00s - 0.32s] п
#    [0.32s - 0.64s] р
#    [0.64s - 0.96s] и
#    ...
```

### Streaming (потоковый режим)

```python
from transformers import AutoProcessor, AutoModelForRNNT, TextIteratorStreamer
from transformers.audio_utils import load_audio
from threading import Thread

processor = AutoProcessor.from_pretrained(model_id)
model = AutoModelForRNNT.from_pretrained(model_id, device_map="auto")
processor.set_num_lookahead_tokens(6)  # 560ms latency

language = "ru-RU"
sampling_rate = processor.feature_extractor.sampling_rate
audio = load_audio("audio.wav", sampling_rate=sampling_rate)

first_chunk_inputs = processor(
    audio[:processor.num_samples_first_audio_chunk],
    sampling_rate=sampling_rate,
    is_streaming=True,
    is_first_audio_chunk=True,
    language=language,
    return_tensors="pt",
).to(model.device, dtype=model.dtype)

def input_features_generator():
    yield first_chunk_inputs.input_features[:, :processor.num_mel_frames_first_audio_chunk, :]
    # ... последующие чанки ...

streamer = TextIteratorStreamer(processor.tokenizer, skip_special_tokens=True)
generate_kwargs = {
    **first_chunk_inputs,
    "input_features": input_features_generator(),
    "streamer": streamer,
}

thread = Thread(target=model.generate, kwargs=generate_kwargs)
thread.start()

for text_chunk in streamer:
    print(text_chunk, end="", flush=True)
thread.join()
```

---

## Получение пословных меток

Поскольку модель выдаёт посимвольные метки, для получения пословных меток нужна пост-обработка:

```python
def char_to_word_timestamps(char_ts, text):
    """
    Группирует посимвольные метки в пословные.
    """
    words = []
    current_word = ""
    word_start = None
    word_end = None

    for ct, ch in zip(char_ts, text):
        if ch.isspace():
            if current_word:
                words.append({
                    "word": current_word,
                    "start": word_start,
                    "end": word_end
                })
                current_word = ""
                word_start = None
        else:
            if word_start is None:
                word_start = ct["start"]
            current_word += ch
            word_end = ct["end"]

    if current_word:
        words.append({
            "word": current_word,
            "start": word_start,
            "end": word_end
        })

    return words
```

---

## Сравнение с другими моделями NVIDIA

| Модель | Архитектура | Временные метки | Уровень |
|---|---|---|---|
| Nemotron 3.5 ASR Streaming | FastConformer-RNNT | ✅ Да (token-level) | Символ |
| Parakeet RNNT 1.1B | FastConformer-RNNT | ✅ Да (token-level) | Символ |
| Canary 1B | FastConformer-Transducer | ✅ Да (через CTC) | Символ/слово |
| Parakeet CTC 1.1B | FastConformer-CTC | ✅ Да (frame-level) | Кадр (естественно выровнен) |

---

## Технические детали реализации

### Цепочка наследования (transformers)

```
Nemotron3_5AsrGenerationMixin
  └─ NemotronAsrStreamingGenerationMixin
       └─ ParakeetRNNTGenerationMixin  ← здесь durations
            └─ GenerationMixin
```

### Ключевые файлы исходного кода

| Файл | Описание |
|---|---|
| `generation_nemotron_asr_streaming.py` | Streaming generate, возвращает `NemotronAsrStreamingGenerateOutput(sequences, durations)` |
| `generation_parakeet.py` | `ParakeetRNNTGenerateOutput` с полями `sequences` и `durations` |
| `processing_nemotron3_5_asr.py` | `decode()` с пост-обработкой durations → timestamp dicts |
| `modeling_nemotron3_5_asr.py` | `Nemotron3_5AsrForRNNT` — основная модель |

### Ссылки

- [Model card на Hugging Face](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)
- [Документация Transformers](https://huggingface.co/docs/transformers/en/model_doc/nemotron3_5_asr)
- [Исходный код генерации](https://github.com/huggingface/transformers/blob/v5.13.1/src/transformers/models/nemotron_asr_streaming/generation_nemotron_asr_streaming.py)
- [Исходный код процессинга](https://github.com/huggingface/transformers/blob/v5.13.1/src/transformers/models/nemotron3_5_asr/processing_nemotron3_5_asr.py)
- [NeMo compute_rnnt_timestamps](https://github.com/NVIDIA-NeMo/NeMo/blob/1692a8fb97e1aadc883cfadd2a57c4e8a1b793aa/nemo/collections/asr/parts/submodules/rnnt_decoding.py#L993)

---

## Вывод

Модель **nvidia/nemotron-3.5-asr-streaming-0.6b** полностью поддерживает возврат временных меток через API 🤗 Transformers. Метки посимвольные, с разрешением ~80ms на токен. Для получения пословных меток требуется дополнительная группировка на пост-обработке. В потоковом режиме метки также доступны.

---

## Исследование для текущего .NET/ONNX Runtime GenAI-пайплайна

**Дата обновления:** 2026-08-01
**Статус:** исследование завершено, реализация отложена по решению пользователя
**Область:** `SpeechLib`, `NemotronSpeech`, `VoiceType.WinUI`, ONNX Runtime GenAI `0.15.0`

Этот раздел является рабочей заметкой для будущих агентов. Он уточняет, что выводы выше относятся к Transformers/NeMo API и не означают, что текущий C# runtime уже умеет возвращать timestamps.

### Решение и текущий статус

- Timestamp-функциональность пока **не реализуется**.
- ONNX-модели не регенерируются и не конвертируются заново.
- Пакеты GenAI не обновляются в рамках этой задачи.
- Не менять существующую эвристику `AddWordTimings()` без отдельного задания: она распределяет текст по временным окнам чанков и не является акустическим выравниванием.
- При возобновлении работы сначала реализовать frame/token-level метаданные в runtime, затем обернуть их в C# API и только после этого менять WinUI или text injection.

### Что подтверждено в текущем репозитории

Текущий путь распознавания теряет информацию о временной позиции токена:

1. `SpeechLib/ModelSession.cs` выполняет RNNT decode и возвращает `DecodeResult(Text, TokenCount)`.
2. `SpeechLib/Interfaces/IStreamingSpeechRecognizer.cs` предоставляет строки результата и `LastTokenCount`, но не frame offsets или durations.
3. `SpeechLib/Transcriber.cs` содержит `AddWordTimings()`, который использует эвристику по чанкам, символам и оценке количества токенов.
4. `SpeechLib/Models/WordTiming.cs` уже задаёт модель `Word`, `StartSeconds`, `EndSeconds`, но источник акустических границ в неё не передаётся.
5. `VoiceType.WinUI/Services/RecognitionService.cs`, `IRecognitionService` и `MainViewModel` работают со строковыми partial/final результатами. UI timestamps не отображает.

Следствие: `TokenCount` нельзя трактовать как количество аудио-кадров. В RNNT на одном кадре может быть несколько emitted tokens, а на других кадрах токены могут не испускаться.

### Проверенные runtime и package выводы

Для существующих CPU FP32/INT8/INT4 и CUDA ONNX-графов были проверены сигнатуры входов/выходов. Они соответствуют текущему `genai_config.json`; переход с GenAI `0.14.1` на обычный runtime `0.15.0` сам по себе не требует регенерации моделей.

| Backend | Текущее состояние | GenAI `0.15.0` | Следствие |
|---|---|---|---|
| CPU | используется `0.14.1` | стабильный пакет есть, требует ORT `1.28.0` | upgrade возможен без обязательной конвертации модели |
| CUDA | используется `0.14.1` | стабильный пакет есть, требует ORT GPU `1.28.0` | upgrade возможен без обязательной конвертации модели |
| Blackwell CUDA | dev-пакет `0.15.0-dev-*` | зависит от nightly feed | проверять отдельно при обновлении |
| DirectML | используется `0.14.1` | стабильного `0.15.0` не найдено | оставить `0.14.1` до появления совместимого пакета |

Явный override ORT в исследованной конфигурации был `1.25.1`; для стабильных GenAI `0.15.0` потребуется согласовать его с требуемой версией `1.28.0`. Это отдельная задача совместимости пакетов, а не задача timestamps.

### Что именно нужно получить от runtime

Публичный C# API текущей интеграции возвращает только текст/счётчик токенов. Для timestamps нужен низкоуровневый результат RNNT decode, сохраняющий как минимум:

```text
(token_id, encoder_time_step)
```

Эквивалентный вариант через `duration` также подходит, если duration на каждом decode step соответствует продвижению по кадрам энкодера. Важно сохранить эту информацию до того, как decode loop преобразует токены в строку.

### Рекомендуемый вариант: frame/token-level timestamps

Первый вариант реализации без изменения ONNX-графов:

1. Расширить native GenAI bridge или native decode loop так, чтобы он возвращал `token_id` вместе с `time_step`/`duration`.
2. Добавить C ABI для получения структурированного результата и корректно освободить native memory.
3. Обернуть ABI в `SpeechLib` и не ломать существующие string-only интерфейсы.
4. Преобразовать `time_step` в секунды по формуле:

    ```text
    seconds = global_encoder_frame * 160 * 8 / 16000
    seconds = global_encoder_frame * 0.080
    ```

5. Сопоставить tokenizer pieces с исходным текстом и сгруппировать pieces в `WordTiming` по пробелам, языковым правилам и punctuation.
6. Отдельно обработать streaming chunk offset, pre-encode cache, left/right context, lookahead, padding и flush.

Ожидаемое базовое разрешение для текущей модели -- около 80 ms на encoder frame. Это frame/token-level timing, а не гарантия точной акустической границы слова.

### Альтернатива: отдельный forced aligner

Если требуются более точные акустические границы начала и конца слова, нужен отдельный forced-alignment этап. Он принимает аудио и уже распознанный текст, поэтому потенциально точнее, но добавляет:

- второй inference path и дополнительные модели;
- задержку, особенно для live mode;
- сложность упаковки CPU/CUDA/DirectML;
- необходимость синхронизации исправлений streaming текста с уже выданными timestamps;
- отдельную валидацию для языков, punctuation, чисел и code-switching.

Forced aligner не следует добавлять в первый этап, пока не доказано, что frame-level timestamps недостаточны.

### Оценка объёма

Оценка дана для текущей архитектуры и не включает исследование нового aligner или конвертацию моделей:

| Вариант | Оценка | Состав работ |
|---|---:|---|
| Frame/token-level timestamps | 3--7 рабочих дней | native metadata, C ABI, C# wrapper, chunk offset, grouping в слова, unit/E2E tests |
| Forced aligner | 5--12 рабочих дней | выбор модели, новый inference path, упаковка, latency, языки, regression tests |

Оценка может вырасти, если текущая используемая native библиотека не предоставляет доступ к decode loop и потребуется поддерживать несколько несовместимых backend bridge.

### Риски и edge cases

- локальный `time_step` нужно преобразовать в глобальное время между streaming chunks;
- cache и lookahead могут сдвигать момент, когда токен становится доступен;
- несколько RNNT tokens могут иметь один frame;
- blank tokens и frames без emissions должны быть отфильтрованы корректно;
- language tags, special tokens, punctuation и tokenizer pieces не должны становиться отдельными словами;
- текст streaming может быть пересмотрен последующим chunk, поэтому timestamps должны поддерживать cumulative text/delta semantics;
- flush и trailing silence требуют отдельного определения конечной границы слова;
- native DLL должны корректно поставляться для CPU, CUDA, Blackwell и DirectML вариантов;
- text injection должен продолжить получать cumulative text или delta в текущем формате;
- существующая baseline-проверка word timings проверяет математическую регрессию, но не акустическую точность.

### План возобновления работы

1. Зафиксировать точную версию native GenAI API и найти место decode loop, где доступны `token_id` и frame progression.
2. Добавить минимальный native probe, возвращающий token/frame pairs для одного offline sample.
3. Проверить, что frame offsets монотонны и корректно продолжаются через streaming chunk boundary.
4. Добавить `TokenTiming`/расширенный decode result в `SpeechLib`, сохранив обратную совместимость старых интерфейсов.
5. Реализовать tokenizer-piece-to-word grouping и unit tests для whitespace, punctuation, language tags и нескольких tokens на одном frame.
6. Добавить E2E regression с аудио и ожидаемыми допусками, а также benchmark latency/memory.
7. Только после стабилизации backend решить, нужен ли timestamp display в `VoiceType.WinUI`.
8. Отдельным решением оценить package upgrade на GenAI `0.15.0`; DirectML нельзя считать покрытым, пока для него нет стабильного пакета.

### Не делать без отдельного решения

- Не считать текущий `LastTokenCount` временной информацией.
- Не считать существующие `WordTiming` акустически точными.
- Не менять ONNX graph/opset только ради timestamps: runtime upgrade или opset change сами по себе не создают alignment metadata.
- Не добавлять VAD как решение задачи выравнивания: VAD сокращает inference на тишине, но не возвращает token/frame alignment.
- Не начинать forced alignment, пока не измерена точность и полезность frame-level варианта.
