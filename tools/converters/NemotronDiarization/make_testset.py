"""Build synthetic multi-speaker "conversations" from Test-Audio/cv17/{en,ru} with ground-truth RTTM.

Speaker identity: the CV17 rows API exposes `client_id` (anonymous speaker hash). We fetch the metadata
(text only, no audio) for the first rows of the test split and match it to Test-Audio/cv17/<lang>/NNNN.txt
by sentence (the downloader saved rows in order). Clips whose sentence is ambiguous are dropped.
Two clips are treated as the same speaker iff they share a client_id; different client_ids are treated
as different people (CV client_ids can in rare cases be the same person on two accounts — not verifiable).

Each conversation: 2-4 speakers, ~60-180 s, turns of 1-3 clips, pauses 0.2-1.0 s, ~15% of turn
changes overlap the previous turn by 0.3-1.2 s. Clips are trimmed of leading/trailing silence.
Reference RTTM (`.rttm`): per clip, an energy VAD (10 ms frames above clip max - 35 dB, gaps < 0.3 s
merged, 50 ms padding, islands < 0.1 s dropped) so pauses inside a clip are NOT labelled as speech
(like forced-alignment references). `.rttm_clip` keeps the coarse whole-clip labels for comparison.

Usage: python make_testset.py --out <dir> [--n 12] [--seed 0]
"""
from __future__ import annotations

import argparse
import json
import random
import urllib.parse
import urllib.request
from collections import defaultdict
from pathlib import Path

import numpy as np
import soundfile as sf

import diar_common as dc

ROWS = "https://datasets-server.huggingface.co/rows"


def fetch_meta(lang: str, n_rows: int, cache: Path):
    if cache.exists():
        return json.loads(cache.read_text(encoding="utf-8"))
    rows = []
    for off in range(0, n_rows, 100):
        q = urllib.parse.urlencode({"dataset": "fixie-ai/common_voice_17_0", "config": lang, "split": "test",
                                    "offset": off, "length": 100})
        with urllib.request.urlopen(f"{ROWS}?{q}", timeout=60) as r:
            d = json.loads(r.read().decode())
        rows += [{"client_id": x["row"]["client_id"], "sentence": (x["row"]["sentence"] or "").strip(),
                  "gender": x["row"].get("gender", "")} for x in d["rows"]]
    cache.write_text(json.dumps(rows), encoding="utf-8")
    return rows


def trim(a: np.ndarray, thr_db=-40.0):
    fr = 160
    n = len(a) // fr
    e = 10 * np.log10(np.mean(a[: n * fr].reshape(n, fr) ** 2, axis=1) + 1e-10)
    act = np.nonzero(e > e.max() + thr_db)[0]
    if len(act) == 0:
        return a
    s, t = max(0, act[0] * fr - 800), min(len(a), (act[-1] + 1) * fr + 800)
    return a[s:t]


def speakers_for(lang: str, meta_cache: Path):
    d = dc.REPO / "Test-Audio" / "cv17" / lang
    files = sorted(d.glob("*.wav"))
    meta = fetch_meta(lang, len(files) + 100, meta_cache)
    by_sent = defaultdict(set)
    for m in meta:
        by_sent[m["sentence"]].add(m["client_id"])
    spk = defaultdict(list)
    for w in files:
        s = w.with_suffix(".txt").read_text(encoding="utf-8").strip()
        ids = by_sent.get(s, set())
        if len(ids) == 1:
            spk[next(iter(ids))].append(w)
    return spk


def build(lang, spk_files, rng: random.Random, n_spk: int, target_s: float):
    # prefer speakers with several clips so repeated turns use different utterances of the same voice
    cands = [k for k, v in spk_files.items() if len(v) >= 2] or list(spk_files)
    chosen = rng.sample(cands, n_spk)
    pools = {i: [trim(dc.load_wav(p)) for p in spk_files[c]] for i, c in enumerate(chosen)}
    # loudness-normalise each speaker to -23 dBFS-ish RMS
    for i, clips in pools.items():
        for j, a in enumerate(clips):
            rms = np.sqrt(np.mean(a ** 2)) + 1e-9
            clips[j] = (a * (0.07 / rms)).astype(np.float32)
    used = {i: 0 for i in pools}
    audio = np.zeros(0, np.float32)
    rttm, vad_rttm, cur, last = [], [], 0.0, -1
    while cur < target_s:
        s = rng.choice([i for i in pools if i != last]) if len(pools) > 1 else 0
        overlap = last >= 0 and rng.random() < 0.15
        start = cur - rng.uniform(0.3, 1.2) if overlap else cur + rng.uniform(0.2, 1.0)
        start = max(0.0, start)
        for _ in range(rng.randint(1, 3)):
            clip = pools[s][used[s] % len(pools[s])]
            used[s] += 1
            a0 = int(round(start * dc.SR))
            end = a0 + len(clip)
            if end > len(audio):
                audio = np.concatenate([audio, np.zeros(end - len(audio), np.float32)])
            audio[a0:end] += clip
            rttm.append((start, len(clip) / dc.SR, s))
            vad_rttm.extend((start + vs, vd, s) for vs, vd in vad_segments(clip))
            start = end / dc.SR + rng.uniform(0.15, 0.5)
        cur = max(cur, rttm[-1][0] + rttm[-1][1])
        last = s
    audio = np.concatenate([audio, np.zeros(dc.SR // 2, np.float32)])
    peak = np.abs(audio).max()
    if peak > 0.99:
        audio *= 0.99 / peak
    return audio, rttm, vad_rttm, chosen


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--n", type=int, default=12)
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(a.seed)
    spk = {lang: speakers_for(lang, out / f"cv17_{lang}_meta.json") for lang in ("en", "ru")}
    for lang in spk:
        multi = sum(1 for v in spk[lang].values() if len(v) >= 2)
        print(f"{lang}: {sum(len(v) for v in spk[lang].values())} clips, {len(spk[lang])} speakers ({multi} with >=2 clips)")
    manifest = []
    for i in range(a.n):
        lang = "en" if i % 2 == 0 else "ru"
        n_spk = 2 + (i // 2) % 3
        target = rng.uniform(60, 180)
        audio, rttm, vad_rttm, chosen = build(lang, spk[lang], rng, n_spk, target)
        name = f"conv{i:02d}_{lang}_{n_spk}spk"
        sf.write(out / f"{name}.wav", audio, dc.SR, subtype="PCM_16")
        for ext, segs in ((".rttm", vad_rttm), (".rttm_clip", rttm)):
            with open(out / f"{name}{ext}", "w") as f:
                for st, du, s in segs:
                    f.write(f"SPEAKER {name} 1 {st:.3f} {du:.3f} <NA> <NA> spk{s} <NA> <NA>\n")
        ov = overlap_ratio(vad_rttm, len(audio) / dc.SR)
        manifest.append({"name": name, "lang": lang, "n_spk": n_spk, "duration": round(len(audio) / dc.SR, 2),
                         "turns": len(rttm), "overlap_ratio": round(ov, 3), "client_ids": [c[:12] for c in chosen]})
        print(manifest[-1])
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1))


def vad_segments(clip: np.ndarray, rel_db=-35.0, min_gap=0.3, pad=0.05, min_len=0.1):
    fr = 160
    n = len(clip) // fr
    e = 10 * np.log10(np.mean(clip[: n * fr].reshape(n, fr) ** 2, axis=1) + 1e-10)
    act = e > e.max() + rel_db
    segs, i = [], 0
    while i < n:
        if act[i]:
            j = i
            while j < n and act[j]:
                j += 1
            segs.append([i * 0.01, j * 0.01])
            i = j
        else:
            i += 1
    merged = []
    for s in segs:
        if merged and s[0] - merged[-1][1] < min_gap:
            merged[-1][1] = s[1]
        else:
            merged.append(s)
    dur = len(clip) / dc.SR
    return [(max(0.0, a - pad), min(dur, b + pad) - max(0.0, a - pad)) for a, b in merged if b - a >= min_len]


def overlap_ratio(rttm, dur):
    t = np.zeros(int(dur * 100) + 1, np.int16)
    for st, du, _ in rttm:
        t[int(st * 100):int((st + du) * 100)] += 1
    return float((t > 1).sum() / max(1, (t > 0).sum()))


if __name__ == "__main__":
    main()
