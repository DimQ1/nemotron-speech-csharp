"""DER of saved probability matrices (run_backends.py output) against the synthetic RTTMs.

Hypothesis = per-speaker 0.5 threshold of the 10 ms probabilities (HF `extract_speaker_dict` semantics,
no extra post-processing). Scored with pyannote.metrics, overlap included, collar 0.25 s (and 0 s).

Usage: python eval_der.py --audio <testset dir> --runs <dir>
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path

import numpy as np
from pyannote.core import Annotation, Segment
from pyannote.metrics.diarization import DiarizationErrorRate

import diar_common as dc


def load_rttm(path: Path) -> Annotation:
    ann = Annotation()
    for line in path.read_text().splitlines():
        p = line.split()
        st, du = float(p[3]), float(p[4])
        ann[Segment(st, st + du)] = p[7]
    return ann


def hyp_from_probs(probs: np.ndarray) -> Annotation:
    ann = Annotation()
    for st, en, spk in dc.probs_to_segments(probs):
        if en > st:
            ann[Segment(st, en)] = f"s{spk}"
    return ann


def n_speakers(probs: np.ndarray, min_s=1.0):
    return int(((probs > 0.5).sum(0) * dc.HOP / dc.SR >= min_s).sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio", required=True)
    ap.add_argument("--runs", required=True)
    a = ap.parse_args()
    audio, runs = Path(a.audio), Path(a.runs)
    refs = {p.stem: load_rttm(p) for p in sorted(audio.glob("*.rttm"))}
    true_n = {k: len(v.labels()) for k, v in refs.items()}
    table = []
    for bdir in sorted(p for p in runs.iterdir() if p.is_dir()):
        for mdir in sorted(bdir.iterdir()):
            files = sorted(mdir.glob("*.npy"))
            if not files:
                continue
            res = {}
            for collar in (0.25, 0.0):
                m = DiarizationErrorRate(collar=collar, skip_overlap=False)
                per_lang = defaultdict(lambda: DiarizationErrorRate(collar=collar, skip_overlap=False))
                for f in files:
                    hyp = hyp_from_probs(np.load(f))
                    m(refs[f.stem], hyp)
                    per_lang[f.stem.split("_")[1]](refs[f.stem], hyp)
                d = m[:]
                tot = d["total"]
                res[collar] = (abs(m), d["missed detection"] / tot, d["false alarm"] / tot, d["confusion"] / tot,
                               {k: abs(v) for k, v in per_lang.items()})
            sca = np.mean([n_speakers(np.load(f)) == true_n[f.stem] for f in files])
            table.append((bdir.name, mdir.name, len(files), res, sca))
    order = ["offline", "low_latency", "very_low_latency", "ultra_low_latency", "ncpp_v3_streaming", "eco_2s"]
    table.sort(key=lambda r: (order.index(r[1]) if r[1] in order else 99, r[0]))
    print("| mode | backend | files | DER c=0.25 | Miss | FA | Conf | DER en | DER ru | DER c=0 | spk-count acc |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for b, mo, n, res, sca in table:
        d, mi, fa, co, pl = res[0.25]
        print(f"| {mo} | {b} | {n} | {100 * d:.2f} | {100 * mi:.2f} | {100 * fa:.2f} | {100 * co:.2f} | "
              f"{100 * pl.get('en', float('nan')):.2f} | {100 * pl.get('ru', float('nan')):.2f} | "
              f"{100 * res[0.0][0]:.2f} | {100 * sca:.0f}% |")


if __name__ == "__main__":
    main()
