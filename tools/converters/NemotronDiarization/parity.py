"""Parity report.

1. Front-end: diar_frontend.onnx (+ caller pre-emphasis/padding) vs the HF feature extractor
   (offline pass and every streaming chunk of the HF processor), max |diff| of log-mel.
2. Model: compares saved probability matrices of run_backends.py, backend vs `hf`, per mode:
   max |dprob|, mean |dprob|, frames whose 0.5-thresholded activity differs, identical segment lists.

Usage: python parity.py --audio <dir> --runs <dir> [--frontend-files conv10_en_4spk]
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

import diar_common as dc


def frontend_parity(wavs):
    proc, _ = None, None
    from transformers import AutoProcessor

    proc = AutoProcessor.from_pretrained(str(dc.HF_DIR))
    import onnxruntime as ort

    fe = ort.InferenceSession(str(dc.ONNX_DIR / "diar_frontend.onnx"), providers=["CPUExecutionProvider"])
    for w in wavs:
        audio = dc.load_wav(w)
        t = dc.num_valid_mel_frames(len(audio))
        hf = dc.hf_offline_features(proc, audio)
        ox = fe.run(None, {"audio": dc.frontend_input(audio)[None]})[0][0]
        npy = dc.frontend_numpy(dc.frontend_input(audio))
        print(f"[frontend] {w.stem}: HF frames={hf.shape[0]} (valid {t}, last zeroed: {np.all(hf[t] == 0)}) "
              f"ONNX frames={ox.shape[0]}  max|ONNX-HF|={np.abs(ox[:t] - hf[:t]).max():.2e}  "
              f"max|numpyFFT-HF|={np.abs(npy[:t] - hf[:t]).max():.2e}  (log-mel range {hf[:t].min():.1f}..{hf[:t].max():.1f})")
        for mode in ("low_latency", "ultra_low_latency"):
            # HF streaming chunk features == slices of the full-utterance mel?
            chunks = collect_stream_features(proc, audio, mode)
            sched = dc.streaming_schedule(len(audio), *dc.STREAMING_MODES[mode][:2])
            assert len(chunks) == len(sched), (len(chunks), len(sched))
            md = max(np.abs(c - ox[s:e]).max() for c, (s, e, _) in zip(chunks, sched))
            print(f"[frontend] {w.stem} {mode}: {len(chunks)} HF chunks == schedule, max|HFchunk - ONNX full-mel slice|={md:.2e}")


def collect_stream_features(proc, audio, mode):
    proc.set_streaming_mode(mode)
    out = [proc(audio[: proc.num_samples_first_audio_chunk], sampling_rate=dc.SR, is_streaming=True,
                is_first_audio_chunk=True, return_tensors="np")["input_features"][0]]
    mel_idx = proc.num_mel_frames_per_step
    start = proc.audio_chunk_start(mel_idx)
    while (end := start + proc.num_samples_per_audio_chunk) <= audio.shape[0]:
        out.append(proc(audio[start:end], sampling_rate=dc.SR, is_streaming=True, is_first_audio_chunk=False,
                        return_tensors="np")["input_features"][0])
        mel_idx += proc.num_mel_frames_per_step
        start = proc.audio_chunk_start(mel_idx)
    last = proc(audio[start:], sampling_rate=dc.SR, is_streaming=True, is_first_audio_chunk=False,
                is_last_audio_chunk=True, return_tensors="np")["input_features"][0]
    if last.shape[0]:
        out.append(last)
    return out


def model_parity(runs: Path, ref="hf"):
    rows = []
    for bdir in sorted(p for p in runs.iterdir() if p.is_dir() and p.name != ref):
        for mdir in sorted(bdir.iterdir()):
            rdir = runs / ref / mdir.name
            if not rdir.exists():
                continue
            mx, mean, flips, frames, same_seg, n = 0.0, [], 0, 0, 0, 0
            for f in sorted(mdir.glob("*.npy")):
                r = rdir / f.name
                if not r.exists():
                    continue
                a, b = np.load(r), np.load(f)
                assert a.shape == b.shape, (f, a.shape, b.shape)
                d = np.abs(a - b)
                mx = max(mx, float(d.max()))
                mean.append(float(d.mean()))
                flips += int(((a > 0.5) != (b > 0.5)).any(axis=1).sum())
                frames += a.shape[0]
                same_seg += dc.probs_to_segments(a) == dc.probs_to_segments(b)
                n += 1
            if n:
                rows.append((bdir.name, mdir.name, n, mx, np.mean(mean), flips, frames, same_seg))
    print(f"\n| backend | mode | files | max abs dprob | mean abs dprob | frames w/ flipped decision | identical segments |")
    print("|---|---|---|---|---|---|---|")
    for b, m, n, mx, me, fl, fr, ss in rows:
        print(f"| {b} | {m} | {n} | {mx:.2e} | {me:.2e} | {fl} / {fr} ({100 * fl / fr:.3f}%) | {ss}/{n} |")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio", required=True)
    ap.add_argument("--runs", required=True)
    ap.add_argument("--frontend-files", default="conv10_en_4spk,conv01_ru_2spk")
    a = ap.parse_args()
    wavs = [Path(a.audio) / f"{s}.wav" for s in a.frontend_files.split(",") if s]
    if wavs:
        frontend_parity(wavs)
    model_parity(Path(a.runs))


if __name__ == "__main__":
    main()
