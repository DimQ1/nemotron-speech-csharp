"""Download a small real-meeting check set: AMI test meetings (Mix-Headset) + the forced-alignment
reference RTTMs the model card uses (nttcslab-sp/diar-forced-alignment), cropped to the first N seconds.
`--labels pyannote` uses pyannote/AMI-diarization-setup "only_words" RTTMs instead.

Usage: python prepare_ami.py --out <dir> [--meetings ES2004a,IS1009a] [--seconds 600]
"""
from __future__ import annotations

import argparse
import urllib.request
from pathlib import Path

import soundfile as sf

AUDIO = "https://groups.inf.ed.ac.uk/ami/AMICorpusMirror/amicorpus/{m}/audio/{m}.Mix-Headset.wav"
RTTM = {
    "fa": "https://raw.githubusercontent.com/nttcslab-sp/diar-forced-alignment/master/AMI/test/{m}.rttm",
    "pyannote": "https://raw.githubusercontent.com/pyannote/AMI-diarization-setup/main/only_words/rttms/test/{m}.rttm",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--meetings", default="ES2004a,IS1009a")
    ap.add_argument("--seconds", type=float, default=600)
    ap.add_argument("--labels", choices=list(RTTM), default="fa")
    a = ap.parse_args()
    out = Path(a.out)
    raw = out / "_raw"
    raw.mkdir(parents=True, exist_ok=True)
    for m in a.meetings.split(","):
        wav, rttm = raw / f"{m}.wav", raw / f"{m}.{a.labels}.rttm"
        if not wav.exists():
            urllib.request.urlretrieve(AUDIO.format(m=m), wav)
        if not rttm.exists():
            urllib.request.urlretrieve(RTTM[a.labels].format(m=m), rttm)
        audio, sr = sf.read(wav, dtype="float32")
        assert sr == 16000
        n = int(a.seconds * sr)
        name = f"ami_{m}_{int(a.seconds)}s"
        sf.write(out / f"{name}.wav", audio[:n], sr, subtype="PCM_16")
        lines = []
        for line in rttm.read_text().splitlines():
            p = line.split()
            st, du = float(p[3]), float(p[4])
            if st >= a.seconds:
                continue
            du = min(du, a.seconds - st)
            lines.append(f"SPEAKER {name} 1 {st:.3f} {du:.3f} <NA> <NA> {p[7]} <NA> <NA>")
        (out / f"{name}.rttm").write_text("\n".join(lines) + "\n")
        print(name, len(lines), "segments,", len({l.split()[7] for l in lines}), "speakers")


if __name__ == "__main__":
    main()
