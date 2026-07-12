"""
Download real speech diarization dataset from HuggingFace.

Tries multiple sources in order:
1. LibriCSS (10h, CC-licensed, derived from LibriSpeech)
2. AMI test set (meetings, research license)
3. Mini LibriMix (2-speaker mixtures)

Downloads up to ~100 audio clips with RTTM labels.
Splits long files into 30-60 second segments for efficient testing.
"""

import os, io, sys, json, tarfile, tempfile, shutil
from pathlib import Path
from urllib.request import urlretrieve
from typing import List, Tuple

import requests
import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parent.parent
OUTPUT_AUDIO = ROOT / "dataset" / "audio"
OUTPUT_RTTM = ROOT / "dataset" / "rttm"

SAMPLE_RATE = 16000
SEGMENT_DURATION = 30.0  # seconds per clip
MAX_FILES = 100


def ensure_dir(d: Path):
    d.mkdir(parents=True, exist_ok=True)


def clear_existing():
    """Remove old synthetic files."""
    for f in OUTPUT_AUDIO.glob("session_*.wav"):
        f.unlink()
    for f in OUTPUT_RTTM.glob("session_*.rttm"):
        f.unlink()


def download_file(url: str, dest: Path, timeout: int = 60) -> bool:
    """Download a single file with error handling."""
    try:
        r = requests.get(url, timeout=timeout, stream=True)
        if r.status_code == 200:
            with open(dest, "wb") as f:
                for chunk in r.iter_content(8192):
                    f.write(chunk)
            return True
        else:
            print(f"    HTTP {r.status_code}")
            return False
    except Exception as e:
        print(f"    Error: {e}")
        return False


def resample_and_save(audio: np.ndarray, orig_sr: int, dest: Path):
    """Resample to 16kHz mono and save as WAV."""
    if orig_sr != SAMPLE_RATE:
        import librosa
        audio = librosa.resample(audio, orig_sr=orig_sr, target_sr=SAMPLE_RATE)
    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    audio = audio.astype(np.float32)
    sf.write(str(dest), audio, SAMPLE_RATE)


def split_into_segments(audio: np.ndarray, sr: int, rttm_data: List[str],
                         base_name: str) -> int:
    """
    Split long audio into 30-second segments with corresponding RTTM annotations.
    Returns number of segments created.
    """
    duration = len(audio) / sr
    num_segments = int(np.ceil(duration / SEGMENT_DURATION))
    created = 0

    for seg_idx in range(num_segments):
        t_start = seg_idx * SEGMENT_DURATION
        t_end = min(t_start + SEGMENT_DURATION, duration)

        # Extract audio segment
        sample_start = int(t_start * sr)
        sample_end = int(t_end * sr)
        seg_audio = audio[sample_start:sample_end]

        if len(seg_audio) < sr * 2:  # Skip segments < 2 seconds
            continue

        seg_name = f"{base_name}_{seg_idx:03d}"
        sf.write(str(OUTPUT_AUDIO / f"{seg_name}.wav"), seg_audio.astype(np.float32), sr)

        # Extract RTTM for this segment
        seg_rttm = []
        for line in rttm_data:
            if line.startswith("SPEAKER"):
                parts = line.split()
                if len(parts) >= 8:
                    spk_start = float(parts[3])
                    spk_dur = float(parts[4])
                    spk_end = spk_start + spk_dur

                    # Check if segment overlaps with this time slice
                    if spk_end > t_start and spk_start < t_end:
                        new_start = max(0, spk_start - t_start)
                        new_end = min(t_end, spk_end) - t_start
                        new_dur = new_end - new_start
                        if new_dur > 0.05:
                            seg_rttm.append(
                                f"SPEAKER {seg_name} 1 {new_start:.3f} {new_dur:.3f} "
                                f"<NA> <NA> {parts[7]} <NA> <NA>"
                            )

        if seg_rttm:
            with open(OUTPUT_RTTM / f"{seg_name}.rttm", "w") as f:
                f.write("\n".join(seg_rttm) + "\n")

        created += 1
        if created >= MAX_FILES:
            break

    return created


# ═══════════════════════════════════════════════════════════════════════
# Strategy 1: LibriCSS from HuggingFace
# ═══════════════════════════════════════════════════════════════════════

def download_libricss() -> int:
    """Download LibriCSS test set from HuggingFace datasets."""
    print("\n--- Strategy 1: LibriCSS from HuggingFace ---")

    try:
        from datasets import load_dataset
        print("  Loading LibriCSS test split...")
        ds = load_dataset("cfosco/LibriCSS", "default", split="test", streaming=True,
                          trust_remote_code=False)

        count = 0
        buffer_audio = []
        buffer_rttm = []
        buffer_name = ""

        for sample in ds:
            audio_arr = sample["audio"]["array"]
            audio_sr = sample["audio"]["sampling_rate"]
            rttm_text = sample.get("rttm", "")
            session_id = sample.get("session_id", f"session_{count:03d}")

            # Collect RTTM lines
            rttm_lines = []
            if rttm_text:
                rttm_lines = [l.strip() for l in rttm_text.split("\n") if l.strip().startswith("SPEAKER")]

            # Resample audio
            if audio_sr != SAMPLE_RATE:
                import librosa
                audio_arr = librosa.resample(audio_arr, orig_sr=audio_sr, target_sr=SAMPLE_RATE)
            if audio_arr.ndim > 1:
                audio_arr = audio_arr.mean(axis=1)

            # Split into 30s segments
            n = split_into_segments(audio_arr, SAMPLE_RATE, rttm_lines, f"libricss_{session_id}")
            count += n

            if count >= MAX_FILES:
                break

        print(f"  ✓ Created {count} segments from LibriCSS")
        return count

    except ImportError:
        print("  datasets library not available")
        return 0
    except Exception as e:
        print(f"  LibriCSS download failed: {e}")
        return 0


# ═══════════════════════════════════════════════════════════════════════
# Strategy 2: VoxConverse test set (small subset)
# ═══════════════════════════════════════════════════════════════════════

def download_voxconverse() -> int:
    """Download a small subset of VoxConverse test audio."""
    print("\n--- Strategy 2: VoxConverse test subset ---")

    try:
        # VoxConverse is on HuggingFace as 'diarizers-community/voxconverse'
        # But it's quite large. Let's just try to get the test split.
        from datasets import load_dataset

        ds = load_dataset("diarizers-community/voxconverse", "default",
                          split="test", streaming=True, trust_remote_code=False)

        count = 0
        for sample in ds:
            audio_arr = sample["audio"]["array"]
            audio_sr = sample["audio"]["sampling_rate"]
            rttm_text = sample.get("rttm", "")
            file_id = sample.get("file_id", f"vox_{count:03d}")

            rttm_lines = [l.strip() for l in rttm_text.split("\n")
                          if l.strip().startswith("SPEAKER")] if rttm_text else []

            if audio_sr != SAMPLE_RATE:
                import librosa
                audio_arr = librosa.resample(audio_arr, orig_sr=audio_sr, target_sr=SAMPLE_RATE)
            if audio_arr.ndim > 1:
                audio_arr = audio_arr.mean(axis=1)

            n = split_into_segments(audio_arr, SAMPLE_RATE, rttm_lines, f"vox_{file_id}")
            count += n

            if count >= MAX_FILES:
                break

        print(f"  ✓ Created {count} segments from VoxConverse")
        return count

    except Exception as e:
        print(f"  VoxConverse failed: {e}")
        return 0


# ═══════════════════════════════════════════════════════════════════════
# Strategy 3: LibriSpeech multi-speaker concatenated
# ═══════════════════════════════════════════════════════════════════════

def download_librispeech_multi() -> int:
    """
    Create multi-speaker test files by downloading single-speaker
    LibriSpeech utterances and concatenating them with known speaker boundaries.
    ~8 speakers from test-clean, 10 files each → 80 files.
    """
    print("\n--- Strategy 3: LibriSpeech multi-speaker concatenation ---")

    base_url = "https://www.openslr.org/resources/12/test-clean"

    # Speaker → chapter → utterance mapping (test-clean)
    speakers = {
        61:  [70968, 70969],
        108: [71260, 71261],
        121: [70237, 70238],
        237: [72688, 72689],
        260: [72197, 72198],
        296: [75075, 75076],
        357: [75746, 75747],
        372: [76085, 76086],
    }

    all_speaker_audio = {}  # speaker_id → list of audio segments

    for spk_id, chapters in speakers.items():
        spk_audio = []
        for ch in chapters:
            for utt in range(10):  # Up to 10 utterances per chapter
                file_id = f"{spk_id}-{ch}-{utt:04d}"
                url = f"{base_url}/{spk_id}/{ch}/{file_id}.flac"
                dest = ROOT / "raw" / "librispeech" / f"{file_id}.flac"

                if not dest.exists():
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    if not download_file(url, dest, timeout=20):
                        break

                try:
                    audio, sr = sf.read(str(dest))
                    if sr != SAMPLE_RATE:
                        import librosa
                        audio = librosa.resample(audio, orig_sr=sr, target_sr=SAMPLE_RATE)
                    spk_audio.append(audio.astype(np.float32))
                except Exception:
                    pass

            if len(spk_audio) > 0:
                all_speaker_audio[f"speaker_{spk_id}"] = spk_audio
                print(f"  Speaker {spk_id}: {len(spk_audio)} utterances")

    if len(all_speaker_audio) < 3:
        print("  Not enough speakers downloaded.")
        return 0

    # Create multi-speaker test files
    np.random.seed(42)
    count = 0

    for file_idx in range(min(MAX_FILES, 80)):
        # Pick 2-3 random speakers
        num_spks = np.random.choice([2, 3])
        chosen_spks = np.random.choice(list(all_speaker_audio.keys()), num_spks, replace=False)

        segments_audio = []
        rttm_lines = []
        t = 0.0
        file_id = f"lsmulti_{file_idx:03d}"

        # Create a conversation: 4-12 speaker turns
        num_turns = np.random.randint(4, 12)
        for _ in range(num_turns):
            spk = np.random.choice(chosen_spks)
            utterances = all_speaker_audio[spk]
            utt = np.random.choice(utterances)

            # Take 1-3 seconds from this utterance
            max_len = min(len(utt), int(3.0 * SAMPLE_RATE))
            if max_len < int(0.5 * SAMPLE_RATE):
                chunk = utt
            else:
                start = np.random.randint(0, max(1, len(utt) - int(0.5 * SAMPLE_RATE)))
                chunk_len = np.random.randint(int(0.5 * SAMPLE_RATE), max_len)
                chunk = utt[start:start + chunk_len]

            segments_audio.append(chunk)
            dur = len(chunk) / SAMPLE_RATE
            rttm_lines.append(
                f"SPEAKER {file_id} 1 {t:.3f} {dur:.3f} <NA> <NA> {spk} <NA> <NA>"
            )
            t += dur

            # Small silence gap (100-300ms)
            gap_samples = np.random.randint(int(0.1 * SAMPLE_RATE), int(0.3 * SAMPLE_RATE))
            segments_audio.append(np.zeros(gap_samples, dtype=np.float32))
            t += gap_samples / SAMPLE_RATE

        # Save audio
        combined = np.concatenate(segments_audio)
        if len(combined) < SAMPLE_RATE:  # Skip files < 1 second
            continue

        sf.write(str(OUTPUT_AUDIO / f"{file_id}.wav"), combined.astype(np.float32), SAMPLE_RATE)

        with open(OUTPUT_RTTM / f"{file_id}.rttm", "w") as f:
            f.write("\n".join(rttm_lines) + "\n")

        count += 1
        if count >= MAX_FILES:
            break

    print(f"  ✓ Created {count} multi-speaker files")
    return count


# ═══════════════════════════════════════════════════════════════════════
# Main
# ═══════════════════════════════════════════════════════════════════════

def main():
    ensure_dir(OUTPUT_AUDIO)
    ensure_dir(OUTPUT_RTTM)

    print("=" * 60)
    print("Downloading Real Speech Diarization Dataset")
    print(f"Target: up to {MAX_FILES} files")
    print("=" * 60)

    total = 0

    # Try strategies in order
    total += download_libricss()
    if total < MAX_FILES:
        total += download_voxconverse()
    if total < MAX_FILES:
        total += download_librispeech_multi()

    print(f"\n{'=' * 60}")
    print(f"Total files: {total}")
    print(f"Audio dir:  {OUTPUT_AUDIO}")
    print(f"RTTM dir:   {OUTPUT_RTTM}")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
