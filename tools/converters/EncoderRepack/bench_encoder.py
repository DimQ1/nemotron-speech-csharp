"""Interleaved encoder timing and memory A/B of several model folders (same model family).

Each repetition visits every folder (the order rotates from one repetition to the next): create the encoder session,
warm up, time `--runs` encoder calls on real audio, record the process's private bytes, drop the session. Nemotron
encoders get consecutive streaming chunks with the caches fed back (as the C# engine runs them); Parakeet encoders get
a `--window-seconds` window (the streaming recognizer re-encodes ~6-8 s windows: 5 s left context + 0.32 s chunk +
1 s right context).

The numbers are encoder-only (no decoder, no front end) on ONNX Runtime with the session options of the apps
(ORT_ENABLE_ALL, spinning off, `--threads` intra-op threads).

Usage:
    python bench_encoder.py --models <folder> <folder> ... --audio <wav> [--reps 5] [--runs 40] [--threads 4]
"""

from __future__ import annotations

import argparse
import ctypes
import gc
import json
import statistics
import sys
import time
from ctypes import wintypes
from pathlib import Path

import numpy as np

from encoder_check import NemotronFeeds, language_of, log_mel, parakeet_features, read_wav, session
from model_folder import ModelFolder


def private_bytes() -> int:
    """Private (commit) bytes of this process; 0 where the Windows API is not available."""
    if sys.platform != "win32":
        return 0

    class Counters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                    ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                    ("PrivateUsage", ctypes.c_size_t)]

    c = Counters()
    c.cb = ctypes.sizeof(c)
    get = ctypes.WinDLL("psapi").GetProcessMemoryInfo
    get.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    get(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(c), c.cb)
    return int(c.PrivateUsage)


def nemotron_runner(folder: ModelFolder, sess, audio_path: Path):
    feeds = NemotronFeeds(folder, sess)
    blocks = list(feeds.chunks(log_mel(read_wav(audio_path))))
    names, outs = feeds.names, feeds.outs
    out_names = [outs["encoder_outputs"], outs["cache_last_channel_next"], outs["cache_last_time_next"],
                 outs["cache_last_channel_len_next"]]
    lang = np.array([language_of(audio_path)], dtype=np.int64)
    state = {"s": feeds.zero_state(), "i": 0}

    def step():
        block = blocks[state["i"] % len(blocks)]
        if state["i"] % len(blocks) == 0:
            state["s"] = feeds.zero_state()
        feed = {names["audio_features"]: block, names["input_lengths"]: np.array([block.shape[1]], dtype=np.int64)}
        feed.update(state["s"])
        if names.get("lang_id") in feeds.inputs:
            feed[names["lang_id"]] = lang
        _, ch, tm, ln = sess.run(out_names, feed)
        state["s"] = {names["cache_last_channel"]: ch, names["cache_last_time"]: tm, names["cache_last_channel_len"]: ln}
        state["i"] += 1

    return step


def parakeet_runner(folder: ModelFolder, sess, audio_path: Path, window_seconds: float):
    audio = read_wav(audio_path)
    n = int(window_seconds * 16000)
    if len(audio) < n:
        audio = np.tile(audio, n // len(audio) + 1)
    feats = parakeet_features(folder, audio[:n])
    names = [i.name for i in sess.get_inputs()]
    feed = {names[0]: feats, names[1]: np.array([feats.shape[2]], dtype=np.int64)}

    def step():
        sess.run(None, feed)

    return step


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models", nargs="+", required=True, help="model folders to compare")
    ap.add_argument("--audio", required=True, help="a 16 kHz .wav (Nemotron: chunks cycle through it)")
    ap.add_argument("--reps", type=int, default=5)
    ap.add_argument("--runs", type=int, default=40, help="timed encoder calls per repetition")
    ap.add_argument("--warmup", type=int, default=5)
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--window-seconds", type=float, default=6.32, help="Parakeet window (default 5 + 0.32 + 1 s)")
    ap.add_argument("--json", help="write the raw numbers here")
    args = ap.parse_args()

    folders = [ModelFolder.detect(Path(m)) for m in args.models]
    audio = Path(args.audio)
    results = {str(f.path): {"ms": [], "load_s": [], "private_mb": []} for f in folders}
    for rep in range(args.reps):
        order = folders[rep % len(folders):] + folders[: rep % len(folders)]
        for f in order:
            gc.collect()
            base = private_bytes()
            t0 = time.perf_counter()
            sess = session(f.encoder_path, args.threads)
            load = time.perf_counter() - t0
            step = (nemotron_runner(f, sess, audio) if f.kind == "nemotron"
                    else parakeet_runner(f, sess, audio, args.window_seconds))
            for _ in range(args.warmup):
                step()
            times = []
            for _ in range(args.runs):
                t = time.perf_counter()
                step()
                times.append((time.perf_counter() - t) * 1000)
            r = results[str(f.path)]
            r["ms"].append(statistics.median(times))
            r["load_s"].append(load)
            r["private_mb"].append((private_bytes() - base) / 2**20)
            del sess, step
            gc.collect()
            print(f"rep {rep + 1}/{args.reps} {f.path.name}: median {r['ms'][-1]:.2f} ms/call, "
                  f"load {load:.1f} s, +{r['private_mb'][-1]:.0f} MB private", flush=True)

    base_ms = statistics.median(results[str(folders[0].path)]["ms"])
    print(f"\n{'model':60s} {'ms/call':>9s} {'min':>7s} {'vs 1st':>7s} {'load s':>7s} {'priv MB':>8s}")
    for f in folders:
        r = results[str(f.path)]
        med = statistics.median(r["ms"])
        print(f"{f.path.name[:60]:60s} {med:9.2f} {min(r['ms']):7.2f} {med / base_ms - 1:+7.1%} "
              f"{statistics.median(r['load_s']):7.1f} {statistics.median(r['private_mb']):8.0f}")
    if args.json:
        Path(args.json).write_text(json.dumps({"args": vars(args), "results": results}, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
