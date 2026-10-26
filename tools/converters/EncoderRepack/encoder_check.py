"""Numerical comparison of two encoders of the same model on real audio (the equivalence gate of the repack).

Nemotron streaming encoders are run chunk by chunk exactly as the C# engine feeds them (`NemotronRnntEngine`):
[pre_encode_cache_size + chunk] mel rows per run, the previous chunk's last rows as the head, zero caches at the start,
every `*_next` output fed back. Features come from a NeMo-style numpy log-mel (128 slaney mels, pre-emphasis 0.97,
centred 512-point FFT, log(x + 2^-24)), close to but not bit-identical with `NemotronStreamingMel`, which is enough
to drive both encoders with realistic activations. Parakeet TDT encoders are run on whole utterances with the
folder's own `nemo128.onnx` front end (the one the app uses).

Usage:
    python encoder_check.py --reference <model folder> --candidate <model folder> --audio <wav or folder> [...]
"""

from __future__ import annotations

import argparse
import gc
import json
import time
import wave
from pathlib import Path

import numpy as np
import onnxruntime as ort

from model_folder import ModelFolder

# Nemotron language ids (prompt indices, libraries/SpeechLib/src/SpeechLib.Providers/LanguageMapper.cs).
LANGUAGE_IDS = {"en": 0, "ru": 11, "de": 2, "es": 3, "fr": 4, "it": 10, "uk": 31}


def read_wav(path: Path) -> np.ndarray:
    with wave.open(str(path), "rb") as w:
        if w.getframerate() != 16000 or w.getsampwidth() != 2:
            raise SystemExit(f"{path}: expected 16 kHz PCM16 audio")
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
        if w.getnchannels() > 1:
            data = data.reshape(-1, w.getnchannels()).mean(axis=1)
    return data


def audio_files(paths: list[str], per_folder: int) -> list[Path]:
    files: list[Path] = []
    for p in map(Path, paths):
        if p.is_dir():
            files += sorted(p.glob("*.wav"))[:per_folder]
        elif p.exists():
            files.append(p)
        else:
            raise SystemExit(f"audio not found: {p}")
    if not files:
        raise SystemExit("no .wav files to verify with")
    return files


def language_of(path: Path, default: int = 0) -> int:
    return LANGUAGE_IDS.get(path.parent.name.lower(), default)


def _hz_to_mel(f):
    f = np.asarray(f, dtype=np.float64)
    mel = f / (200.0 / 3)
    log_t = f >= 1000.0
    return np.where(log_t, 15.0 + np.log(np.maximum(f, 1e-10) / 1000.0) / (np.log(6.4) / 27.0), mel)


def _mel_to_hz(m):
    m = np.asarray(m, dtype=np.float64)
    f = m * (200.0 / 3)
    log_t = m >= 15.0
    return np.where(log_t, 1000.0 * np.exp((np.log(6.4) / 27.0) * (m - 15.0)), f)


def slaney_filterbank(sr: int, n_fft: int, n_mels: int, fmin: float, fmax: float) -> np.ndarray:
    fft_freqs = np.linspace(0, sr / 2, n_fft // 2 + 1)
    mel_f = _mel_to_hz(np.linspace(_hz_to_mel(fmin), _hz_to_mel(fmax), n_mels + 2))
    fdiff = np.diff(mel_f)
    ramps = mel_f[:, None] - fft_freqs[None, :]
    weights = np.maximum(0, np.minimum(-ramps[:-2] / fdiff[:-1, None], ramps[2:] / fdiff[1:, None]))
    weights *= (2.0 / (mel_f[2 : n_mels + 2] - mel_f[:n_mels]))[:, None]
    return weights.astype(np.float32)


def log_mel(wave_: np.ndarray, n_mels=128, n_fft=512, hop=160, win=400, preemph=0.97, eps=2.0**-24) -> np.ndarray:
    """[frames, n_mels] NeMo-style log-mel (centred frames: frames = 1 + len // hop)."""
    x = np.concatenate([wave_[:1], wave_[1:] - preemph * wave_[:-1]]).astype(np.float32)
    x = np.pad(x, (n_fft // 2, n_fft // 2))
    window = np.zeros(n_fft, dtype=np.float32)
    off = (n_fft - win) // 2
    window[off : off + win] = np.hanning(win)  # symmetric Hann, like torch.hann_window(periodic=False) in NeMo
    frames = 1 + (len(x) - n_fft) // hop
    idx = np.arange(n_fft)[None, :] + hop * np.arange(frames)[:, None]
    spec = np.abs(np.fft.rfft(x[idx] * window, n=n_fft)) ** 2
    mel = spec.astype(np.float32) @ slaney_filterbank(16000, n_fft, n_mels, 0.0, 8000.0).T
    return np.log(mel + eps).astype(np.float32)


def session(path: Path, threads: int) -> ort.InferenceSession:
    so = ort.SessionOptions()
    so.intra_op_num_threads = threads
    so.inter_op_num_threads = 1
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.add_session_config_entry("session.intra_op.allow_spinning", "0")
    so.log_severity_level = 3
    return ort.InferenceSession(str(path), so, providers=["CPUExecutionProvider"])


class NemotronFeeds:
    """Chunked encoder inputs of one utterance, built the way NemotronRnntEngine builds them."""

    def __init__(self, folder: ModelFolder, sess: ort.InferenceSession):
        m = folder.config["model"]
        self.hop = int(m["hop_length"])
        self.chunk_rows = int(m["chunk_samples"]) // self.hop
        self.cache_rows = int(m["pre_encode_cache_size"])
        self.n_mels = int(m["num_mels"])
        self.inputs = {i.name: i for i in sess.get_inputs()}
        enc = m["encoder"]
        self.names = enc["inputs"]
        self.outs = enc["outputs"]

    def chunks(self, mel: np.ndarray):
        n = mel.shape[0] // self.chunk_rows
        head = np.zeros((self.cache_rows, self.n_mels), dtype=np.float32)
        for k in range(n):
            rows = mel[k * self.chunk_rows : (k + 1) * self.chunk_rows]
            block = np.concatenate([head, rows])[None]
            head = block[0, -self.cache_rows :]
            yield np.ascontiguousarray(block)

    def zero_state(self) -> dict:
        state = {}
        for key in ("cache_last_channel", "cache_last_time"):
            meta = self.inputs[self.names[key]]
            state[self.names[key]] = np.zeros(meta.shape, dtype=np.float32)
        state[self.names["cache_last_channel_len"]] = np.zeros([1], dtype=np.int64)
        return state


def run_nemotron(sess: ort.InferenceSession, folder: ModelFolder, mel: np.ndarray, lang: int) -> list[np.ndarray]:
    feeds = NemotronFeeds(folder, sess)
    state = feeds.zero_state()
    names, outs = feeds.names, feeds.outs
    out_names = [outs["encoder_outputs"], outs["cache_last_channel_next"], outs["cache_last_time_next"],
                 outs["cache_last_channel_len_next"]]
    results = []
    for block in feeds.chunks(mel):
        feed = {names["audio_features"]: block, names["input_lengths"]: np.array([block.shape[1]], dtype=np.int64)}
        feed.update(state)
        if names.get("lang_id") in feeds.inputs:
            feed[names["lang_id"]] = np.array([lang], dtype=np.int64)
        enc, ch, tm, ln = sess.run(out_names, feed)
        state = {names["cache_last_channel"]: ch, names["cache_last_time"]: tm, names["cache_last_channel_len"]: ln}
        results.append(enc)
    results += [state[names["cache_last_channel"]], state[names["cache_last_time"]]]
    return results


def parakeet_features(folder: ModelFolder, wave_: np.ndarray) -> np.ndarray:
    pre = folder.path / "nemo128.onnx"
    sess = session(pre, 1)
    feats, _ = sess.run(None, {"waveforms": wave_[None].astype(np.float32),
                               "waveforms_lens": np.array([len(wave_)], dtype=np.int64)})
    return feats


def run_parakeet(sess: ort.InferenceSession, feats: np.ndarray) -> list[np.ndarray]:
    names = [i.name for i in sess.get_inputs()]
    outputs = sess.run(None, {names[0]: feats, names[1]: np.array([feats.shape[2]], dtype=np.int64)})
    return [outputs[0]]


def run_encoder(folder: ModelFolder, encoder_path: Path, files: list[Path], threads: int) -> list[list[np.ndarray]]:
    sess = session(encoder_path, threads)
    runs = []
    for f in files:
        audio = read_wav(f)
        if folder.kind == "nemotron":
            runs.append(run_nemotron(sess, folder, log_mel(audio), language_of(f)))
        elif folder.kind == "parakeet":
            runs.append(run_parakeet(sess, parakeet_features(folder, audio)))
        else:
            raise SystemExit("encoder_check needs a Nemotron or Parakeet folder")
    del sess
    gc.collect()
    return runs


def compare_runs(reference: list[list[np.ndarray]], candidate: list[list[np.ndarray]]) -> dict:
    """max |diff|, the reference's max |value|, relative RMS error and cosine over every output of every file."""
    ref = np.concatenate([a.ravel().astype(np.float64) for run in reference for a in run])
    cand = np.concatenate([a.ravel().astype(np.float64) for run in candidate for a in run])
    if ref.shape != cand.shape:
        raise SystemExit(f"output shapes differ: {ref.shape} vs {cand.shape}")
    diff = cand - ref
    return {
        "values": int(ref.size),
        "max_abs_diff": float(np.max(np.abs(diff))),
        "ref_max_abs": float(np.max(np.abs(ref))),
        "rel_max_abs_diff": float(np.max(np.abs(diff)) / max(np.max(np.abs(ref)), 1e-30)),
        "rel_rms_diff": float(np.sqrt(np.mean(diff**2) / max(np.mean(ref**2), 1e-30))),
        "cosine": float(np.dot(ref, cand) / max(np.linalg.norm(ref) * np.linalg.norm(cand), 1e-30)),
        "bit_identical": bool(np.array_equal(ref, cand)),
    }


def compare_encoders(reference: ModelFolder, reference_path: Path, candidate_path: Path, files: list[Path],
                     threads: int) -> dict:
    t0 = time.time()
    ref = run_encoder(reference, reference_path, files, threads)
    cand = run_encoder(reference, candidate_path, files, threads)
    stats = compare_runs(ref, cand)
    stats["files"] = [str(f) for f in files]
    stats["seconds"] = round(time.time() - t0, 1)
    return stats


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--reference", required=True, help="model folder whose encoder is the reference")
    ap.add_argument("--candidate", required=True, help="model folder whose encoder is compared")
    ap.add_argument("--audio", nargs="+", required=True, help=".wav files or folders (16 kHz PCM16)")
    ap.add_argument("--files", type=int, default=2, help="files taken from each audio folder (default 2)")
    ap.add_argument("--threads", type=int, default=4)
    args = ap.parse_args()
    ref = ModelFolder.detect(Path(args.reference))
    cand = ModelFolder.detect(Path(args.candidate))
    stats = compare_encoders(ref, ref.encoder_path, cand.encoder_path, audio_files(args.audio, args.files), args.threads)
    print(json.dumps(stats, indent=2))


if __name__ == "__main__":
    main()
