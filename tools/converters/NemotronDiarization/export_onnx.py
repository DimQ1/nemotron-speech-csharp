"""Export Nemotron-3-Diarization (HF Transformers implementation) to ONNX.

Outputs (in models/nemotron-3-diarization-onnx/ by default):
  diar_step.onnx        fp32 stateless step graph (features + cached embeds -> chunk embeds + probs)
  diar_step.int8.onnx   dynamic int8 (MatMul weights, per-channel) quantization of the step graph
  diar_frontend.onnx    STFT + mel + log front-end (caller does pre-emphasis + padding)
  diar_config.json      geometry, front-end spec, AOSC hyper-parameters, silence embedding

Usage:
  python export_onnx.py [--out DIR] [--opset 17] [--skip-int8]
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np
import torch
from torch import nn

import diar_common as dc


class StepGraph(nn.Module):
    """One AOSC streaming step without the (data-dependent) cache update.

    features      [1, 8*Ne, 128]  log-mel of the chunk incl. look-ahead, zero-padded to a multiple of 8
    cache_embeds  [1, Nc, 512]    speaker-cache frames followed by FIFO frames (Nc may be 0)
    ->
    chunk_embeds  [1, Ne, 512]    stacked+projected chunk frames (push the first n_chunk of them to the FIFO)
    probs         [1, 8*(Nc+Ne), 8]  sigmoid speaker activity at 10 ms for the whole input
    frame_probs   [1, Nc+Ne, 8]   probs avg-pooled to 80 ms (input of the cache update)
    """

    def __init__(self, hf):
        super().__init__()
        self.proj_in = hf.model.audio_tower.embedder.projection
        self.model = hf.model
        self.classifier = hf.classifier

    def forward(self, features, cache_embeds):
        b = features.shape[0]
        stacked = features.reshape(b, -1, dc.SUB * dc.N_MELS)
        chunk_embeds = self.proj_in(stacked)
        x = torch.cat([cache_embeds, chunk_embeds], dim=1)
        pos = torch.arange(x.shape[1], device=x.device).unsqueeze(0)
        h = self.model(inputs_embeds=x, position_ids=pos).last_hidden_state
        probs = self.classifier(h).sigmoid()
        frame_probs = probs.reshape(b, -1, dc.SUB, dc.N_SPK).mean(dim=2)
        return chunk_embeds, probs, frame_probs


class FrontendGraph(nn.Module):
    """audio [1, N] (pre-emphasized, padded by the caller) -> log-mel [1, T, 128], T = 1 + (N-512)//160.
    STFT as a strided Conv1d with a windowed real-DFT basis (no STFT op needed, works on every EP)."""

    def __init__(self):
        super().__init__()
        import librosa

        n = np.arange(dc.N_FFT)
        k = np.arange(dc.N_FFT // 2 + 1)
        win = np.zeros(dc.N_FFT)
        off = (dc.N_FFT - dc.WIN) // 2
        win[off:off + dc.WIN] = np.hanning(dc.WIN)
        ang = 2 * np.pi * np.outer(k, n) / dc.N_FFT
        basis = np.concatenate([np.cos(ang) * win, -np.sin(ang) * win], axis=0)  # [514, 512]
        self.register_buffer("basis", torch.tensor(basis[:, None, :], dtype=torch.float32))
        fb = librosa.filters.mel(sr=dc.SR, n_fft=dc.N_FFT, n_mels=dc.N_MELS, fmin=0.0, fmax=dc.SR / 2, norm="slaney")
        self.register_buffer("mel_fb", torch.tensor(fb.T, dtype=torch.float32))  # [257, 128]

    def forward(self, audio):
        spec = nn.functional.conv1d(audio.unsqueeze(1), self.basis, stride=dc.HOP)  # [1, 514, T]
        re, im = spec[:, : dc.N_FFT // 2 + 1], spec[:, dc.N_FFT // 2 + 1:]
        power = (re * re + im * im).transpose(1, 2)  # [1, T, 257]
        return torch.log(power @ self.mel_fb + dc.LOG_GUARD)


def export(module, args, path, names_in, names_out, dyn, opset):
    torch.onnx.export(module, args, str(path), input_names=names_in, output_names=names_out,
                      dynamic_axes=dyn, opset_version=opset, dynamo=False, do_constant_folding=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(dc.ONNX_DIR))
    ap.add_argument("--opset", type=int, default=17)
    ap.add_argument("--skip-int8", action="store_true")
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    proc, hf = dc.load_hf("eager")
    step = StepGraph(hf).eval()
    feats = torch.randn(1, 13 * dc.SUB, dc.N_MELS)
    cache = torch.randn(1, 40, dc.HIDDEN)
    export(step, (feats, cache), out / "diar_step.onnx", ["features", "cache_embeds"],
           ["chunk_embeds", "probs", "frame_probs"],
           {"features": {1: "mel_frames"}, "cache_embeds": {1: "cached_frames"},
            "chunk_embeds": {1: "chunk_frames"}, "probs": {1: "out_frames_10ms"},
            "frame_probs": {1: "out_frames_80ms"}}, a.opset)
    print("exported diar_step.onnx")

    fe = FrontendGraph().eval()
    export(fe, (torch.randn(1, 16000),), out / "diar_frontend.onnx", ["audio"], ["log_mel"],
           {"audio": {1: "samples"}, "log_mel": {1: "frames"}}, a.opset)
    print("exported diar_frontend.onnx")

    if not a.skip_int8:
        from onnxruntime.quantization import QuantType, quantize_dynamic

        quantize_dynamic(str(out / "diar_step.onnx"), str(out / "diar_step.int8.onnx"),
                         weight_type=QuantType.QInt8, per_channel=True, op_types_to_quantize=["MatMul"])
        print("exported diar_step.int8.onnx")

    sc = hf.config.streaming_config
    meta = {
        "source": "nvidia/Nemotron-3-Diarization (HF Transformers implementation)",
        "opset": a.opset,
        "sample_rate": dc.SR,
        "frontend": {
            "graph": "diar_frontend.onnx", "preemphasis": dc.PREEMPH, "preemphasis_first_sample": "unchanged",
            "n_fft": dc.N_FFT, "win_length": dc.WIN, "hop_length": dc.HOP, "window": "hann symmetric (periodic=False), centered in n_fft",
            "center_pad": dc.N_FFT // 2, "pad_mode": "zeros after pre-emphasis", "power": 2.0,
            "n_mels": dc.N_MELS, "mel": "librosa slaney, fmin 0, fmax 8000, norm=slaney", "log_guard": dc.LOG_GUARD,
            "normalization": "none", "valid_frames": "floor(num_samples / 160)",
        },
        "subsampling_factor": dc.SUB, "num_speakers": dc.N_SPK, "hidden_size": dc.HIDDEN,
        "aosc": {
            "spkcache_len": sc.speaker_cache_length, "sil_frames_per_spk": sc.speaker_cache_silence_frames_per_speaker,
            "pred_score_threshold": sc.prediction_score_threshold,
            "latest_frames_score_boost": sc.latest_frames_score_boost,
            "strong_boost_rate": sc.strong_boost_rate, "weak_boost_rate": sc.weak_boost_rate,
            "min_pos_scores_rate": sc.min_positive_scores_rate,
        },
        "modes": {k: dict(zip(["chunk_len", "right_context", "fifo_len", "update_period"], v))
                  for k, v in dc.STREAMING_MODES.items()},
        "offline": dict(zip(["chunk_len", "right_context", "fifo_len", "update_period"], dc.OFFLINE)),
        "silence_embed": hf.silence_embeds.detach().numpy().astype(float).tolist(),
    }
    (out / "diar_config.json").write_text(json.dumps(meta, indent=1))
    for f in sorted(out.iterdir()):
        print(f"{f.name:28s} {f.stat().st_size / 1e6:8.1f} MB")


if __name__ == "__main__":
    main()
