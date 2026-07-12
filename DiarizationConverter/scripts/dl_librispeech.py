"""Download real speech from LibriSpeech via HuggingFace datasets (test-clean)."""
import numpy as np
import soundfile as sf
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUTPUT_AUDIO = ROOT / "dataset" / "audio"
OUTPUT_RTTM = ROOT / "dataset" / "rttm"
SAMPLE_RATE = 16000
MAX_FILES = 100

OUTPUT_AUDIO.mkdir(parents=True, exist_ok=True)
OUTPUT_RTTM.mkdir(parents=True, exist_ok=True)

print("=" * 60)
print("Downloading LibriSpeech test-clean (real speech)")
print("=" * 60)

# Download LibriSpeech test-clean from HuggingFace
from datasets import load_dataset

print("Loading LibriSpeech test-clean...")
ds = load_dataset("librispeech_asr", "clean", split="test", streaming=True)

speaker_audio = {}
count = 0
for sample in ds:
    spk_id = str(sample.get("speaker_id", "unknown"))
    audio = sample["audio"]["array"]
    sr = sample["audio"]["sampling_rate"]
    text = sample.get("text", "")

    if sr != SAMPLE_RATE:
        import librosa
        audio = librosa.resample(audio, orig_sr=sr, target_sr=SAMPLE_RATE)
    if audio.ndim > 1:
        audio = audio.mean(axis=1)

    if spk_id not in speaker_audio:
        speaker_audio[spk_id] = []
    speaker_audio[spk_id].append(audio.astype(np.float32))
    count += 1
    if count % 500 == 0:
        print(f"  Loaded {count} utterances, {len(speaker_audio)} speakers")

print(f"Total: {count} utterances, {len(speaker_audio)} speakers")

# Create multi-speaker test files
np.random.seed(42)
spk_list = list(speaker_audio.keys())
created = 0

for file_idx in range(MAX_FILES):
    num_spks = np.random.choice([2, 3])
    chosen = np.random.choice(spk_list, min(num_spks, len(spk_list)), replace=False)

    segments_audio = []
    rttm_lines = []
    t = 0.0
    file_id = f"ls_real_{file_idx:03d}"

    num_turns = np.random.randint(5, 15)
    for _ in range(num_turns):
        spk = np.random.choice(chosen)
        utts = speaker_audio[spk]
        utt = utts[np.random.randint(len(utts))]

        max_len = min(len(utt), int(4.0 * SAMPLE_RATE))
        chunk_len = np.random.randint(int(0.3 * SAMPLE_RATE), max(1, max_len))
        start = np.random.randint(0, max(1, len(utt) - chunk_len))
        chunk = utt[start:start + chunk_len]

        segments_audio.append(chunk)
        dur = len(chunk) / SAMPLE_RATE
        rttm_lines.append(
            f"SPEAKER {file_id} 1 {t:.3f} {dur:.3f} <NA> <NA> {spk} <NA> <NA>"
        )
        t += dur
        gap = int(np.random.uniform(0.05, 0.2) * SAMPLE_RATE)
        segments_audio.append(np.zeros(gap, dtype=np.float32))
        t += gap / SAMPLE_RATE

    combined = np.concatenate(segments_audio)
    if len(combined) < SAMPLE_RATE:
        continue

    sf.write(str(OUTPUT_AUDIO / f"{file_id}.wav"), combined.astype(np.float32), SAMPLE_RATE)
    with open(OUTPUT_RTTM / f"{file_id}.rttm", "w") as f:
        f.write("\n".join(rttm_lines) + "\n")
    created += 1

    if created % 20 == 0:
        print(f"  Created {created}/{MAX_FILES} files")

print(f"\n{'=' * 60}")
print(f"Created {created} real-speech multi-speaker files")
print(f"Audio dir: {OUTPUT_AUDIO}")
print(f"RTTM dir:  {OUTPUT_RTTM}")
print(f"{'=' * 60}")
