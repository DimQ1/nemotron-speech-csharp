"""Shared helpers for the Nemotron-3-Diarization ONNX spike.

Contains:
  * paths / constants,
  * the HF (PyTorch) reference runners (offline + streaming, exactly as the model card),
  * a NumPy port of the Arrival-Order Speaker Cache (AOSC) + FIFO update
    (`AoscState`) that mirrors `Nemotron3DiarizationSpeakerCache` line by line —
    this is the logic the C# runtime must implement (see tools/DiarBench),
  * an ONNX streaming / offline driver that uses `diar_step.onnx` + `AoscState`,
  * the front-end contract (pre-emphasis + padding done by the caller,
    STFT/mel/log inside `diar_frontend.onnx`).
"""
from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[3]
HF_DIR = Path(os.environ.get("DIAR_HF_DIR", REPO / "models" / "nemotron-3-diarization-hf"))
ONNX_DIR = Path(os.environ.get("DIAR_ONNX_DIR", REPO / "models" / "nemotron-3-diarization-onnx"))

SR = 16000
HOP = 160
N_FFT = 512
WIN = 400
N_MELS = 128
PREEMPH = 0.97
LOG_GUARD = 2.0 ** -24
SUB = 8  # mel frames per encoder frame (80 ms)
N_SPK = 8
HIDDEN = 512

# name -> (chunk_len, right_context, fifo_len, update_period) in 80 ms encoder frames
STREAMING_MODES = {
    "low_latency": (9, 4, 264, 222),        # 1.04 s, model-card default
    "very_low_latency": (6, 2, 264, 222),   # 0.64 s
    "ultra_low_latency": (3, 1, 264, 222),  # 0.32 s
    # NeMo-Speech.cpp `v3-streaming` preset: 13-frame (1.04 s) chunk, 1 frame rc, FIFO 80
    "ncpp_v3_streaming": (13, 1, 80, 40),   # 1.12 s buffer, ~2.3x cheaper per audio second
    # spike proposal for phones: 1.92 s hop, 2.08 s buffer, small FIFO -> ~4.5x cheaper than low_latency
    "eco_2s": (24, 2, 48, 48),
}
# offline-style configuration from the model card / config.json (30.4 s buffer)
OFFLINE = (340, 40, 40, 300)


# --------------------------------------------------------------------------------------
# Front-end
# --------------------------------------------------------------------------------------
def preemphasize(audio: np.ndarray) -> np.ndarray:
    """y[0] = x[0]; y[n] = x[n] - 0.97 x[n-1] (HF applies it to the whole utterance)."""
    y = np.empty_like(audio, dtype=np.float32)
    y[0] = audio[0]
    y[1:] = audio[1:] - PREEMPH * audio[:-1]
    return y


def frontend_input(audio: np.ndarray) -> np.ndarray:
    """Signal fed to diar_frontend.onnx for a full utterance: preemph, then 256 zeros left
    (center=True) and 256 zeros right. Frame k covers padded[k*160 : k*160+512].
    Valid frames: T = floor(len/160) (HF masks frame T, the extra centered one, to 0)."""
    y = preemphasize(audio.astype(np.float32))
    pad = np.zeros(N_FFT // 2, np.float32)
    return np.concatenate([pad, y, pad])


def num_valid_mel_frames(num_samples: int) -> int:
    return num_samples // HOP


def frontend_numpy(padded: np.ndarray) -> np.ndarray:
    """Pure NumPy reference of diar_frontend.onnx (FFT based)."""
    import librosa

    n = 1 + (len(padded) - N_FFT) // HOP
    win = np.zeros(N_FFT, np.float64)
    off = (N_FFT - WIN) // 2
    win[off:off + WIN] = np.hanning(WIN)  # symmetric == torch.hann_window(periodic=False)
    idx = np.arange(N_FFT)[None, :] + HOP * np.arange(n)[:, None]
    frames = padded.astype(np.float64)[idx] * win
    spec = np.abs(np.fft.rfft(frames, axis=-1)) ** 2
    fb = librosa.filters.mel(sr=SR, n_fft=N_FFT, n_mels=N_MELS, fmin=0.0, fmax=SR / 2, norm="slaney")
    return np.log(spec @ fb.T + LOG_GUARD).astype(np.float32)


# --------------------------------------------------------------------------------------
# AOSC state (NumPy port of transformers Nemotron3DiarizationSpeakerCache, batch = 1)
# --------------------------------------------------------------------------------------
@dataclass
class AoscConfig:
    fifo_len: int
    update_period: int
    spkcache_len: int = 264
    sil_frames_per_spk: int = 1
    pred_score_threshold: float = 0.25
    latest_frames_score_boost: float = 0.05
    strong_boost_rate: float = 0.75
    weak_boost_rate: float = 1.5
    min_pos_scores_rate: float = 0.5
    n_spk: int = N_SPK


class AoscState:
    def __init__(self, cfg: AoscConfig, silence_embed: np.ndarray):
        self.cfg = cfg
        self.sil = silence_embed.astype(np.float32)
        budget = cfg.spkcache_len // cfg.n_spk - cfg.sil_frames_per_spk
        self.min_pos = math.floor(budget * cfg.min_pos_scores_rate)
        self.n_strong = math.floor(budget * cfg.strong_boost_rate)
        self.n_weak = math.floor(budget * cfg.weak_boost_rate)
        self.embeds = np.zeros((0, HIDDEN), np.float32)
        self.probs = np.zeros((0, cfg.n_spk), np.float32)
        self.fifo = np.zeros((0, HIDDEN), np.float32)
        self.is_compressed = False

    def cached(self) -> np.ndarray:
        return np.concatenate([self.embeds, self.fifo], axis=0)

    def update(self, chunk_embeds: np.ndarray, frame_probs: np.ndarray):
        """chunk_embeds: (n_chunk, 512) — the chunk's own frames (no look-ahead).
        frame_probs: (n_cached + n_input, 8) — sigmoid probs avg-pooled to 80 ms over the whole step input."""
        c = self.cfg
        nc = self.embeds.shape[0]
        fifo_embeds = np.concatenate([self.fifo, chunk_embeds], axis=0)
        n = fifo_embeds.shape[0]
        popped = 0 if n <= c.fifo_len else min(max(c.update_period, n - c.fifo_len), n)
        if popped:
            fifo_probs = frame_probs[nc:nc + n]
            stored = self.probs[:nc] if self.is_compressed else frame_probs[:nc]
            cache_e = np.concatenate([self.embeds, fifo_embeds[:popped]], axis=0)
            cache_p = np.concatenate([stored, fifo_probs[:popped]], axis=0)
            fifo_embeds = fifo_embeds[popped:]
            if cache_e.shape[0] > c.spkcache_len:
                cache_e, cache_p = self._compress(cache_e, cache_p)
                self.is_compressed = True
            self.embeds, self.probs = cache_e, cache_p.astype(np.float32)
        self.fifo = fifo_embeds

    def _frame_scores(self, p: np.ndarray) -> np.ndarray:
        thr = self.cfg.pred_score_threshold
        lp = np.log(np.maximum(p, thr))
        lc = np.log(np.maximum(1.0 - p, thr))
        s = lp - lc + lc.sum(-1, keepdims=True) - math.log(0.5)
        speech = p > 0.5
        s = np.where(speech, s, -np.inf)
        pos = s > 0
        enough = pos.sum(axis=0, keepdims=True) >= self.min_pos
        s = np.where(~pos & speech & enough, -np.inf, s)
        return s

    @staticmethod
    def _boost(s: np.ndarray, k: int, boost: float) -> np.ndarray:
        s = s.copy()
        for spk in range(s.shape[1]):
            col = s[:, spk]
            # deterministic tie-break (lowest frame first) — torch.topk's tie order is implementation-defined
            idx = np.argsort(-col, kind="stable")[:k]
            col[idx] += boost
        return s

    def _compress(self, embeds: np.ndarray, probs: np.ndarray):
        c = self.cfg
        n, S = probs.shape
        s = self._frame_scores(probs.astype(np.float32)).astype(np.float32)
        s[c.spkcache_len:] += c.latest_frames_score_boost
        s = self._boost(s, self.n_strong, -2.0 * math.log(0.5))
        s = self._boost(s, self.n_weak, -math.log(0.5))
        s = np.concatenate([s, np.full((c.sil_frames_per_spk, S), np.inf, np.float32)], axis=0)
        embeds = np.concatenate([embeds, self.sil[None]], axis=0)
        probs = np.concatenate([probs, np.zeros((1, S), probs.dtype)], axis=0)
        n_scored = n + c.sil_frames_per_spk
        sentinel = n_scored * S
        flat = s.T.reshape(-1)
        k = c.spkcache_len
        top = np.argsort(-flat, kind="stable")[:k]  # ties -> lowest (speaker, frame) index first
        self.last_tie_count = int(np.sum(flat == flat[top[-1]])) - int(np.sum(flat[top] == flat[top[-1]]))
        top = np.where(flat[top] == -np.inf, sentinel, top)
        top = np.sort(top)
        frame = np.where(top == sentinel, n, np.minimum(top % n_scored, n))
        return embeds[frame], probs[frame]


# --------------------------------------------------------------------------------------
# Chunk schedules (identical to the HF processor / model loops)
# --------------------------------------------------------------------------------------
def streaming_schedule(num_samples: int, chunk: int, rc: int):
    """Yields (mel_start, mel_end, is_last) for a HF-processor streaming session."""
    t_valid = num_valid_mel_frames(num_samples)
    c8, f = chunk * SUB, (chunk + rc) * SUB
    samples_per_chunk = f * HOP + WIN  # processor.num_samples_per_audio_chunk
    out = [(0, f, False)]
    k = 1
    while (k * c8 * HOP - N_FFT // 2) + samples_per_chunk <= num_samples:
        out.append((k * c8, k * c8 + f, False))
        k += 1
    if k * c8 < t_valid:
        out.append((k * c8, t_valid, True))
    return out


# --------------------------------------------------------------------------------------
# ONNX driver
# --------------------------------------------------------------------------------------
class OnnxDiarizer:
    def __init__(self, step_path: Path | str, frontend_path: Path | str | None = None, threads: int = 0):
        import onnxruntime as ort

        so = ort.SessionOptions()
        if threads:
            so.intra_op_num_threads = threads
        self.step = ort.InferenceSession(str(step_path), so, providers=["CPUExecutionProvider"])
        self.frontend = None
        if frontend_path:
            self.frontend = ort.InferenceSession(str(frontend_path), so, providers=["CPUExecutionProvider"])
        meta = json.loads((Path(step_path).parent / "diar_config.json").read_text())
        self.silence = np.asarray(meta["silence_embed"], np.float32)

    def mel(self, audio: np.ndarray) -> np.ndarray:
        """Full-utterance log-mel, T = floor(len/160) valid frames."""
        x = frontend_input(audio)[None]
        m = self.frontend.run(None, {"audio": x})[0][0]
        return m[: num_valid_mel_frames(len(audio))]

    def run_step(self, feats: np.ndarray, cached: np.ndarray):
        # graph contract: mel frame count must be a multiple of 8 (zero-pad, as HF feature stacking does)
        pad = -feats.shape[0] % SUB
        if pad:
            feats = np.concatenate([feats, np.zeros((pad, feats.shape[1]), np.float32)], axis=0)
        ce, p10, p80 = self.step.run(None, {"features": feats[None].astype(np.float32),
                                             "cache_embeds": cached[None].astype(np.float32)})
        return ce[0], p10[0], p80[0]

    def stream(self, mel: np.ndarray, num_samples: int, chunk: int, rc: int, fifo: int, upd: int):
        st = AoscState(AoscConfig(fifo_len=fifo, update_period=upd), self.silence)
        out = []
        for ms, me, last in streaming_schedule(num_samples, chunk, rc):
            feats = mel[ms:me]
            n_in = -(-feats.shape[0] // SUB)
            n_chunk = n_in if last else chunk
            cached = st.cached()
            nc = cached.shape[0]
            ce, p10, p80 = self.run_step(feats, cached)
            st.update(ce[:n_chunk], p80)
            seg = p10[nc * SUB:(nc + n_chunk) * SUB]
            out.append(seg[: feats.shape[0]] if last else seg)
        return np.concatenate(out, axis=0)

    def offline(self, feats: np.ndarray, chunk=OFFLINE[0], rc=OFFLINE[1], fifo=OFFLINE[2], upd=OFFLINE[3]):
        """HF offline mode: `feats` = whole-recording features (any length)."""
        t = feats.shape[0]
        pad = -t % SUB
        fp = np.concatenate([feats, np.zeros((pad, feats.shape[1]), np.float32)], axis=0)
        ne = fp.shape[0] // SUB
        st = AoscState(AoscConfig(fifo_len=fifo, update_period=upd), self.silence)
        out = []
        for s in range(0, ne, chunk):
            e = min(s + chunk, ne)
            ce_end = min(e + rc, ne)
            cached = st.cached()
            nc = cached.shape[0]
            ce, p10, p80 = self.run_step(fp[s * SUB:ce_end * SUB], cached)
            st.update(ce[: e - s], p80)
            out.append(p10[nc * SUB:(nc + e - s) * SUB])
        return np.concatenate(out, axis=0)[:t]


# --------------------------------------------------------------------------------------
# HF reference
# --------------------------------------------------------------------------------------
def load_hf(attn="eager"):
    import torch
    from transformers import AutoModelForAudioFrameClassification, AutoProcessor

    torch.set_grad_enabled(False)
    proc = AutoProcessor.from_pretrained(str(HF_DIR))
    model = AutoModelForAudioFrameClassification.from_pretrained(str(HF_DIR), attn_implementation=attn).eval()
    return proc, model


def hf_offline_features(proc, audio: np.ndarray) -> np.ndarray:
    inputs = proc(audio, sampling_rate=SR, return_tensors="pt")
    return inputs["input_features"][0].numpy()


def hf_offline(proc, model, audio: np.ndarray) -> np.ndarray:
    import torch

    feats = torch.from_numpy(hf_offline_features(proc, audio))[None]
    return model(input_features=feats).logits.sigmoid()[0].numpy()


def hf_streaming(proc, model, audio: np.ndarray, mode: str, collect_features=False):
    """Exact copy of the model-card streaming generator (only for the processor's named modes)."""
    import torch

    proc.set_streaming_mode(mode)
    chunks = []

    def gen():
        yield proc(audio[: proc.num_samples_first_audio_chunk], sampling_rate=SR, is_streaming=True,
                   is_first_audio_chunk=True, return_tensors="pt")
        mel_idx = proc.num_mel_frames_per_step
        start = proc.audio_chunk_start(mel_idx)
        while (end := start + proc.num_samples_per_audio_chunk) <= audio.shape[0]:
            yield proc(audio[start:end], sampling_rate=SR, is_streaming=True, is_first_audio_chunk=False,
                       return_tensors="pt")
            mel_idx += proc.num_mel_frames_per_step
            start = proc.audio_chunk_start(mel_idx)
        yield proc(audio[start:], sampling_rate=SR, is_streaming=True, is_first_audio_chunk=False,
                   is_last_audio_chunk=True, return_tensors="pt")

    cache, logits = None, []
    for inputs in gen():
        if inputs["input_features"].shape[1] == 0:
            continue
        if collect_features:
            chunks.append(inputs["input_features"][0].numpy())
        kw = {"input_features": inputs["input_features"]}
        if "num_lookahead_frames" in inputs:
            kw["num_lookahead_frames"] = int(inputs["num_lookahead_frames"])
        out = model(**kw, speaker_cache=cache)
        logits.append(out.logits)
        cache = out.speaker_cache
    probs = torch.cat(logits, dim=1).sigmoid()[0].numpy()
    return (probs, chunks) if collect_features else probs


# --------------------------------------------------------------------------------------
# Post-processing / scoring helpers
# --------------------------------------------------------------------------------------
def probs_to_segments(probs: np.ndarray, threshold=0.5, frame_s=HOP / SR):
    """HF `extract_speaker_dict` semantics: (start, end, speaker) tuples."""
    active = (probs > threshold).astype(np.int8)
    pad = np.zeros((1, active.shape[1]), np.int8)
    ch = np.diff(np.concatenate([pad, active, pad]), axis=0)
    segs = []
    for spk in range(active.shape[1]):
        st = np.nonzero(ch[:, spk] == 1)[0]
        en = np.nonzero(ch[:, spk] == -1)[0]
        segs += [(round(a * frame_s, 2), round(b * frame_s, 2), spk) for a, b in zip(st, en)]
    return sorted(segs)


def load_wav(path) -> np.ndarray:
    import soundfile as sf

    a, sr = sf.read(str(path), dtype="float32")
    if a.ndim > 1:
        a = a.mean(1)
    assert sr == SR, f"{path}: {sr} Hz"
    return a
