"""Run HF PyTorch and ONNX (fp32 / int8) diarization over a folder of wavs and save the 10 ms
probability matrices (`<out>/<backend>/<mode>/<name>.npy`) for parity.py / eval_der.py.

Modes: offline, low_latency, very_low_latency, ultra_low_latency, ncpp_v3_streaming (ONNX only).
ONNX streaming uses the ONNX front-end + diar_step + the NumPy AOSC port (= the C# contract).
ONNX offline uses the ONNX front-end too (T valid frames + the one zeroed frame HF appends).

Usage: python run_backends.py --audio <dir> --out <dir> [--backends hf,onnx_fp32,onnx_int8] [--modes ...]
"""
from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

import diar_common as dc

ALL_MODES = ["offline", "low_latency", "very_low_latency", "ultra_low_latency", "ncpp_v3_streaming", "eco_2s"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--backends", default="hf,onnx_fp32,onnx_int8")
    ap.add_argument("--modes", default=",".join(ALL_MODES))
    ap.add_argument("--files", default="", help="comma-separated stems to restrict to")
    ap.add_argument("--threads", type=int, default=0, help="intra-op threads (torch and ORT); 0 = library default")
    a = ap.parse_args()
    wavs = sorted(Path(a.audio).glob("*.wav"))
    if a.files:
        keep = set(a.files.split(","))
        wavs = [w for w in wavs if w.stem in keep]
    modes = a.modes.split(",")
    out = Path(a.out)
    timings = {}
    tpath = out / "timings.json"
    if tpath.exists():
        timings = json.loads(tpath.read_text())

    for backend in a.backends.split(","):
        if backend == "hf":
            import torch

            if a.threads:
                torch.set_num_threads(a.threads)
            proc, model = dc.load_hf("sdpa")
        else:
            step = dc.ONNX_DIR / ("diar_step.onnx" if backend == "onnx_fp32" else "diar_step.int8.onnx")
            od = dc.OnnxDiarizer(step, dc.ONNX_DIR / "diar_frontend.onnx", threads=a.threads)
        for w in wavs:
            audio = dc.load_wav(w)
            mel = None if backend == "hf" else od.mel(audio)
            for mode in modes:
                if backend == "hf" and mode in ("ncpp_v3_streaming", "eco_2s"):
                    continue
                dst = out / backend / mode / f"{w.stem}.npy"
                if dst.exists():
                    continue
                dst.parent.mkdir(parents=True, exist_ok=True)
                t0 = time.perf_counter()
                if backend == "hf":
                    p = dc.hf_offline(proc, model, audio) if mode == "offline" else dc.hf_streaming(proc, model, audio, mode)
                elif mode == "offline":
                    feats = np.concatenate([mel, np.zeros((1, dc.N_MELS), np.float32)], axis=0)
                    p = od.offline(feats)
                else:
                    c, r, f, u = dc.STREAMING_MODES[mode]
                    p = od.stream(mel, len(audio), c, r, f, u)
                dt = time.perf_counter() - t0
                np.save(dst, p.astype(np.float32))
                timings[f"{backend}/{mode}/{w.stem}"] = {"sec": dt, "audio_sec": len(audio) / dc.SR}
                print(f"{backend:9s} {mode:18s} {w.stem:20s} frames={p.shape[0]} RTF={dt / (len(audio) / dc.SR):.3f}",
                      flush=True)
                tpath.write_text(json.dumps(timings, indent=1))


if __name__ == "__main__":
    main()
