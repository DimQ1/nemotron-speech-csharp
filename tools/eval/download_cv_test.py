"""Download N Common Voice 17 clips + reference sentences for ru and en.

Fetches rows from the Hugging Face datasets-server REST API (no `datasets`
library needed, avoiding the torchcodec/FFmpeg issue), decodes the MP3 with
soundfile, resamples to 16 kHz mono and writes {idx}.wav + {idx}.txt pairs.

The output lands in <repo>/Test-Audio/cv17/{lang}, which is gitignored.

Usage:
  python tools/eval/download_cv_test.py --lang ru --count 250
  python tools/eval/download_cv_test.py --lang en --count 250
"""
import argparse
import io
import json
import time
import urllib.parse
import urllib.request
from pathlib import Path

import numpy as np
import soundfile as sf

ROWS_URL = "https://datasets-server.huggingface.co/rows"
DATASET = "fixie-ai/common_voice_17_0"
OUT_ROOT = Path(__file__).resolve().parents[2] / "Test-Audio" / "cv17"

UA = {"User-Agent": "Mozilla/5.0 (voice-type-eval)"}


def get_json(url, retries=4):
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers=UA)
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read().decode("utf-8"))
        except Exception as e:  # noqa: BLE001
            if attempt == retries - 1:
                raise
            time.sleep(2 * (attempt + 1))


def download_bytes(url, retries=4):
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers=UA)
            with urllib.request.urlopen(req, timeout=90) as r:
                return r.read()
        except Exception as e:  # noqa: BLE001
            if attempt == retries - 1:
                raise
            time.sleep(2 * (attempt + 1))


def decode_to_16k_mono(data):
    # soundfile handles MP3/OGG/WAV/FLAC; fall back to librosa on a temp file.
    audio, sr = None, None
    try:
        audio, sr = sf.read(io.BytesIO(data), dtype="float32")
    except Exception:  # noqa: BLE001
        import tempfile
        import librosa
        with tempfile.NamedTemporaryFile(suffix=".mp3", delete=False) as tmp:
            tmp.write(data)
            tmp_path = tmp.name
        try:
            audio, sr = librosa.load(tmp_path, sr=None, mono=True)
        finally:
            Path(tmp_path).unlink(missing_ok=True)

    audio = np.asarray(audio, dtype=np.float32)
    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    if sr is None or sr == 0:
        sr = 16000
    if sr != 16000:
        import librosa
        audio = librosa.resample(audio, orig_sr=sr, target_sr=16000)
    return audio


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", choices=["ru", "en"], required=True)
    ap.add_argument("--count", type=int, default=250)
    args = ap.parse_args()

    out_dir = OUT_ROOT / args.lang
    out_dir.mkdir(parents=True, exist_ok=True)

    saved = 0
    offset = 0
    page = 100
    t0 = time.time()
    while saved < args.count:
        q = urllib.parse.urlencode({
            "dataset": DATASET,
            "config": args.lang,
            "split": "test",
            "offset": offset,
            "length": page,
        })
        payload = get_json(f"{ROWS_URL}?{q}")
        rows = payload.get("rows", [])
        if not rows:
            print("  no more rows", flush=True)
            break

        for row in rows:
            if saved >= args.count:
                break
            r = row.get("row", {})
            sentence = (r.get("sentence") or "").strip()
            audio_list = r.get("audio") or []
            if not sentence or not audio_list:
                offset += 1
                continue
            src = audio_list[0].get("src")
            if not src:
                offset += 1
                continue

            try:
                data = download_bytes(src)
                audio = decode_to_16k_mono(data)
                if audio.size == 0:
                    offset += 1
                    continue
            except Exception as e:  # noqa: BLE001
                print(f"  skip row (dl/decode error): {e}", flush=True)
                offset += 1
                continue

            idx = f"{saved + 1:04d}"
            wav_path = out_dir / f"{idx}.wav"
            txt_path = out_dir / f"{idx}.txt"
            sf.write(wav_path, audio, 16000, subtype="PCM_16")
            txt_path.write_text(sentence + "\n", encoding="utf-8")
            saved += 1
            offset += 1
            if saved % 25 == 0:
                print(f"  {args.lang}: {saved}/{args.count} saved ({time.time() - t0:.0f}s)", flush=True)

    print(f"done {args.lang}: {saved} files -> {out_dir} ({time.time() - t0:.0f}s)")


if __name__ == "__main__":
    main()
