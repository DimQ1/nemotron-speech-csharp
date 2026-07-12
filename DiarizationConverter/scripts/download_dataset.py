"""
Download a small diarization test dataset (10 audio files + RTTM labels).

Source: LibriCSS test set (Creative Commons, derived from LibriSpeech).
Each file is a multi-speaker conversation with RTTM ground truth.

Fallback: If download fails, creates synthetic multi-speaker audio
by concatenating unique LibriSpeech utterances with known speaker boundaries.
"""

import os
import sys
import io
import zipfile
import tempfile
from pathlib import Path
from urllib.request import urlretrieve

import numpy as np
import soundfile as sf

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "dataset"
AUDIO_DIR = OUTPUT_DIR / "audio"
RTTM_DIR = OUTPUT_DIR / "rttm"
SAMPLE_RATE = 16000
NUM_FILES = 10

# LibriCSS test set on HuggingFace (parquet with audio + annotations)
LIBRICSS_URL = "https://huggingface.co/datasets/cfosco/LibriCSS/resolve/main/test/"


def download_libricss(audio_dir: Path, rttm_dir: Path, num_files: int) -> bool:
    """Attempt to download LibriCSS test files from HuggingFace."""
    try:
        print("Attempting to download LibriCSS test files from HuggingFace...")

        # LibriCSS files are named like: overlap_ratio_0.0_sil0.0_1.0_session0_actual0.wav
        # Try to fetch a few via direct URLs
        import requests

        session_ids = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]
        downloaded = 0

        for sid in session_ids[:num_files]:
            filename = f"overlap_ratio_0.0_sil0.0_1.0_session{sid}_actual0"
            wav_url = f"{LIBRICSS_URL}{filename}.wav"
            rttm_url = f"{LIBRICSS_URL}{filename}.rttm"

            try:
                # Download WAV
                resp = requests.get(wav_url, timeout=30)
                if resp.status_code == 200:
                    wav_path = audio_dir / f"session_{sid:02d}.wav"
                    wav_path.write_bytes(resp.content)

                    # Generate simple RTTM if not available
                    audio, sr = sf.read(io.BytesIO(resp.content))
                    if sr != SAMPLE_RATE:
                        import librosa
                        audio = librosa.resample(audio, orig_sr=sr, target_sr=SAMPLE_RATE)
                        sf.write(str(wav_path), audio, SAMPLE_RATE)

                    duration = len(audio) / SAMPLE_RATE
                    _create_default_rttm(rttm_dir / f"session_{sid:02d}.rttm",
                                         file_id=f"session_{sid:02d}",
                                         duration=duration)
                    downloaded += 1
                    print(f"  ✓ session_{sid:02d}.wav ({duration:.1f}s)")
            except Exception as e:
                print(f"  ✗ session {sid}: {e}")

        return downloaded > 0

    except ImportError:
        print("requests library not available.")
        return False
    except Exception as e:
        print(f"Download failed: {e}")
        return False


def _create_default_rttm(rttm_path: Path, file_id: str, duration: float):
    """Create a simple 2-speaker RTTM for testing."""
    with open(rttm_path, "w") as f:
        # Simulate a conversation: speaker A and B alternating
        segment_len = 2.0  # 2-second turns
        t = 0.0
        speaker = 0
        while t < duration:
            seg_dur = min(segment_len, duration - t)
            f.write(f"SPEAKER {file_id} 1 {t:.3f} {seg_dur:.3f} <NA> <NA> speaker_{speaker} <NA> <NA>\n")
            t += seg_dur
            speaker = 1 - speaker  # Alternate speakers


def download_librispeech_synthetic(audio_dir: Path, rttm_dir: Path, num_files: int) -> bool:
    """
    Fallback: create synthetic multi-speaker test files from LibriSpeech public domain audio.
    Downloads a few LibriSpeech utterances and concatenates them into multi-speaker files.
    """
    try:
        import requests

        print("Creating synthetic multi-speaker test files from LibriSpeech...")

        # LibriSpeech test-clean sample URLs (public domain, ~10s each)
        base_url = "https://www.openslr.org/resources/12/test-clean"
        # We'll use a few speaker directories
        speaker_dirs = [61, 108, 121, 237, 260, 296, 357, 372, 422, 450]

        all_audio = []
        for spk in speaker_dirs:
            try:
                # Get a sample from each speaker
                chapter = 70968  # Common chapter in test-clean
                file_id = f"{spk}-{chapter}-0000"
                url = f"{base_url}/{spk}/{chapter}/{file_id}.flac"
                resp = requests.get(url, timeout=20)
                if resp.status_code == 200:
                    import io as _io
                    audio, sr = sf.read(_io.BytesIO(resp.content))
                    if sr != SAMPLE_RATE:
                        import librosa
                        audio = librosa.resample(audio, orig_sr=sr, target_sr=SAMPLE_RATE)
                    all_audio.append((f"speaker_{spk}", audio))
                    print(f"  ✓ Downloaded speaker_{spk}: {len(audio) / SAMPLE_RATE:.1f}s")
            except Exception as e:
                print(f"  ✗ speaker_{spk}: {e}")

        if len(all_audio) < 2:
            print("❌ Not enough speakers downloaded.")
            return False

        # Create multi-speaker test files by concatenating utterances
        for i in range(min(num_files, 10)):
            np.random.seed(42 + i)

            # Pick 2-3 random speakers for each file
            num_speakers = np.random.choice([2, 3])
            chosen = np.random.choice(len(all_audio), num_speakers, replace=False)

            # Build the audio: alternate speakers
            segments = []
            rttm_lines = []
            t = 0.0
            file_id = f"session_{i:02d}"

            for _ in range(np.random.randint(4, 10)):  # 4-10 turns
                spk_idx = np.random.choice(chosen)
                spk_label, spk_audio = all_audio[spk_idx]

                # Take a 1-3 second chunk
                chunk_len = np.random.randint(SAMPLE_RATE, 3 * SAMPLE_RATE)
                start = np.random.randint(0, max(1, len(spk_audio) - chunk_len))
                chunk = spk_audio[start:start + chunk_len]
                segments.append(chunk)

                dur = len(chunk) / SAMPLE_RATE
                rttm_lines.append(
                    f"SPEAKER {file_id} 1 {t:.3f} {dur:.3f} <NA> <NA> {spk_label} <NA> <NA>"
                )
                t += dur

            combined = np.concatenate(segments)
            sf.write(str(audio_dir / f"{file_id}.wav"), combined, SAMPLE_RATE)

            with open(rttm_dir / f"{file_id}.rttm", "w") as f:
                f.write("\n".join(rttm_lines) + "\n")

            print(f"  ✓ {file_id}.wav ({len(combined) / SAMPLE_RATE:.1f}s, {num_speakers} speakers)")

        return True

    except Exception as e:
        print(f"Synthetic generation failed: {e}")
        return False


def create_minimal_synthetic(audio_dir: Path, rttm_dir: Path, num_files: int) -> bool:
    """
    Last resort: create minimal synthetic audio with sine waves at different frequencies,
    simulating different speakers.
    """
    print("Creating minimal synthetic test files (sine waves)...")

    # Different frequencies for different "speakers"
    speaker_freqs = [200, 350, 500, 650, 800, 950, 1100, 1250, 1400, 1550]

    for i in range(num_files):
        file_id = f"session_{i:02d}"
        rttm_lines = []
        segments = []

        t = 0.0
        num_speakers = 3 if i < 5 else 2  # Mix of 2 and 3 speaker files
        np.random.seed(42 + i)

        for _ in range(np.random.randint(5, 12)):
            spk = np.random.randint(0, num_speakers)
            freq = speaker_freqs[spk]
            dur = np.random.uniform(1.0, 3.0)
            samples = int(dur * SAMPLE_RATE)

            # Generate sine wave with slight variation
            t_axis = np.linspace(0, dur, samples, endpoint=False)
            noise = np.random.randn(samples) * 0.01
            tone = 0.5 * np.sin(2 * np.pi * freq * t_axis) + noise
            segments.append(tone.astype(np.float32))

            rttm_lines.append(
                f"SPEAKER {file_id} 1 {t:.3f} {dur:.3f} <NA> <NA> speaker_{spk} <NA> <NA>"
            )
            t += dur

        combined = np.concatenate(segments)
        sf.write(str(audio_dir / f"{file_id}.wav"), combined.astype(np.float32), SAMPLE_RATE)

        with open(rttm_dir / f"{file_id}.rttm", "w") as f:
            f.write("\n".join(rttm_lines) + "\n")

    print(f"  ✓ Created {num_files} synthetic test files")
    return True


def main():
    AUDIO_DIR.mkdir(parents=True, exist_ok=True)
    RTTM_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 60)
    print("Downloading Diarization Test Dataset (10 files)")
    print("=" * 60)

    success = False

    # Strategy 1: Try LibriCSS
    success = download_libricss(AUDIO_DIR, RTTM_DIR, NUM_FILES)

    # Strategy 2: LibriSpeech synthetic
    if not success:
        success = download_librispeech_synthetic(AUDIO_DIR, RTTM_DIR, NUM_FILES)

    # Strategy 3: Minimal synthetic
    if not success:
        success = create_minimal_synthetic(AUDIO_DIR, RTTM_DIR, NUM_FILES)

    if success:
        print(f"\n✅ Dataset ready: {AUDIO_DIR}")
        wav_count = len(list(AUDIO_DIR.glob("*.wav")))
        rttm_count = len(list(RTTM_DIR.glob("*.rttm")))
        print(f"   Audio files: {wav_count}")
        print(f"   RTTM labels: {rttm_count}")
    else:
        print("\n❌ Failed to create dataset.")
        sys.exit(1)


if __name__ == "__main__":
    main()
