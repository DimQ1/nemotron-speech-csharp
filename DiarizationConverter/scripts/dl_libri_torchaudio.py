"""Download LibriSpeech via torchaudio, create multi-speaker diarization test files."""
import sys, os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUTPUT_AUDIO = ROOT / "dataset" / "audio"
OUTPUT_RTTM = ROOT / "dataset" / "rttm"
SAMPLE_RATE = 16000
MAX_FILES = 100

OUTPUT_AUDIO.mkdir(parents=True, exist_ok=True)
OUTPUT_RTTM.mkdir(parents=True, exist_ok=True)

print("=" * 60)
print("Downloading LibriSpeech test-clean via torchaudio")
print("=" * 60)

download_dir = str(ROOT / "raw" / "librispeech")
os.makedirs(download_dir, exist_ok=True)

import torchaudio
ds = torchaudio.datasets.LIBRISPEECH(download_dir, url="test-clean", download=True)
print(f"Loaded {len(ds)} utterances")

# Group by speaker
import numpy as np
speaker_audio = {}
for i in range(len(ds)):
    waveform, sr, utterance, speaker_id, chapter_id, utterance_id = ds[i]
    spk = str(speaker_id)
    audio = waveform.squeeze().numpy()

    if sr != SAMPLE_RATE:
        import librosa
        audio = librosa.resample(audio, orig_sr=sr, target_sr=SAMPLE_RATE)

    if spk not in speaker_audio:
        speaker_audio[spk] = []
    speaker_audio[spk].append(audio.astype(np.float32))

print(f"Speakers: {len(speaker_audio)}")
for spk, utts in sorted(speaker_audio.items())[:8]:
    total_sec = sum(len(u) / SAMPLE_RATE for u in utts)
    print(f"  Speaker {spk}: {len(utts)} utterances, {total_sec:.0f}s total")

# Create multi-speaker files
np.random.seed(42)
spk_list = list(speaker_audio.keys())
import soundfile as sf

created = 0
for file_idx in range(MAX_FILES):
    num_spks = np.random.choice([2, 3])
    chosen = np.random.choice(spk_list, min(num_spks, len(spk_list)), replace=False)

    segments = []
    rttm = []
    t = 0.0
    fid = f"ls_real_{file_idx:03d}"

    for _ in range(np.random.randint(5, 15)):
        spk = np.random.choice(chosen)
        utts = speaker_audio[spk]
        utt = utts[np.random.randint(len(utts))]

        max_len = min(len(utt), int(4.0 * SAMPLE_RATE))
        if max_len < int(0.3 * SAMPLE_RATE):
            chunk = utt
        else:
            cl = np.random.randint(int(0.3 * SAMPLE_RATE), max_len)
            st = np.random.randint(0, max(1, len(utt) - cl))
            chunk = utt[st:st + cl]

        segments.append(chunk)
        dur = len(chunk) / SAMPLE_RATE
        rttm.append(f"SPEAKER {fid} 1 {t:.3f} {dur:.3f} <NA> <NA> {spk} <NA> <NA>")
        t += dur
        gap = int(np.random.uniform(0.05, 0.2) * SAMPLE_RATE)
        segments.append(np.zeros(gap, dtype=np.float32))
        t += gap / SAMPLE_RATE

    combined = np.concatenate(segments)
    if len(combined) < SAMPLE_RATE:
        continue

    sf.write(str(OUTPUT_AUDIO / f"{fid}.wav"), combined.astype(np.float32), SAMPLE_RATE)
    with open(OUTPUT_RTTM / f"{fid}.rttm", "w") as f:
        f.write("\n".join(rttm) + "\n")
    created += 1

    if created % 25 == 0:
        print(f"  Created {created}/{MAX_FILES}")

print(f"\n{'=' * 60}")
print(f"Created {created} real-speech multi-speaker files")
print(f"Audio dir: {OUTPUT_AUDIO}")
print(f"RTTM dir:  {OUTPUT_RTTM}")
print(f"{'=' * 60}")
