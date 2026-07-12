"""
Real NeMo Sortformer → ONNX FP32 Export (Opset 21).

Upgraded from opset 17 to opset 21 for:
  - MatMulNBits compatibility (INT4/INT8 quantization via ORT MatMulNBitsQuantizer)
  - Better ONNX Runtime GenAI integration
  - Future-proof graph optimizations

Uses torch.onnx.export with dynamo=True (new exporter) when possible,
falling back to legacy TorchScript exporter.
"""

# ── MUST BE FIRST: Windows CPU patches ────────────────────────────────
import signal as _signal
if not hasattr(_signal, "SIGKILL"):
    _signal.SIGKILL = _signal.SIGTERM
    _signal.SIGSTOP = _signal.SIGTERM

import sys
from pathlib import Path

# Add parent to path so we can import stubs
sys.path.insert(0, str(Path(__file__).resolve().parent))
import neptune_stubs  # noqa: E402 — must be before nemo imports

import torch
import onnx
import numpy as np
import yaml
from collections import Counter

ROOT = Path(__file__).resolve().parent.parent
CHECKPOINT_DIR = ROOT / "raw" / "extracted"
OUTPUT_DIR = ROOT / "models" / "sortformer_fp32"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"

SAMPLE_RATE = 16000
NUM_SPEAKERS = 4
OPSET_TARGET = 21


def load_model_from_config_and_weights(config_path: Path, weights_path: Path):
    """Load SortformerEncLabelModel with NeMo."""
    print(f"\nLoading model...")
    print(f"  Config: {config_path}")
    print(f"  Weights: {weights_path}")

    with open(config_path) as f:
        cfg = yaml.safe_load(f)

    cfg["streaming_mode"] = False

    sfm = cfg.get("sortformer_modules", {})
    for old_param in ["spkcache_len", "fifo_len", "chunk_len", "spkcache_update_period",
                      "chunk_left_context", "chunk_right_context",
                      "spkcache_sil_frames_per_spk", "scores_add_rnd",
                      "causal_attn_rate", "causal_attn_rc"]:
        sfm.pop(old_param, None)
    if "hidden_size" not in sfm:
        sfm["hidden_size"] = cfg.get("transformer_encoder", {}).get("hidden_size", 192)

    fixed_config_path = CHECKPOINT_DIR / "model_config_fixed.yaml"
    with open(fixed_config_path, "w") as f:
        yaml.dump(cfg, f)
    print(f"  Fixed config written to {fixed_config_path}")

    from nemo.collections.asr.models.sortformer_diar_models import SortformerEncLabelModel

    print(f"  Trying from_config_file with fixed config...")
    model = SortformerEncLabelModel.from_config_file(str(fixed_config_path))
    print(f"  Model created from fixed config.")

    checkpoint = torch.load(str(weights_path), map_location="cpu", weights_only=False)
    if "state_dict" in checkpoint:
        state_dict = checkpoint["state_dict"]
    else:
        state_dict = checkpoint

    cleaned = {}
    for k, v in state_dict.items():
        new_k = k
        if k.startswith("model."):
            new_k = k[6:]
        cleaned[new_k] = v

    missing, unexpected = model.load_state_dict(cleaned, strict=False)
    if missing:
        print(f"  Missing keys: {len(missing)}")
        for mk in missing[:5]:
            print(f"    - {mk}")
        if len(missing) > 5:
            print(f"    ... and {len(missing) - 5} more")
    if unexpected:
        print(f"  Unexpected keys: {len(unexpected)}")
        for uk in unexpected[:5]:
            print(f"    + {uk}")

    model.eval()
    model.to("cpu")

    total_params = sum(p.numel() for p in model.parameters())
    print(f"  Params: {total_params:,}")
    return model


class SortformerWrapper(torch.nn.Module):
    """Wraps NeMo model for ONNX export.

    Input:  processed_signal [B, n_mels, T_mel] — mel spectrogram features
    Output: speaker_logits    [B, T_diar, num_speakers] — per-frame speaker probabilities

    Audio preprocessing (STFT → mel) is done separately in C#/Python.
    """

    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, processed_signal: torch.Tensor) -> torch.Tensor:
        batch = processed_signal.shape[0]
        mel_len = processed_signal.shape[2]
        processed_signal_length = torch.full((batch,), mel_len, dtype=torch.long,
                                             device=processed_signal.device)

        emb_seq, emb_seq_length = self.model.frontend_encoder(
            processed_signal=processed_signal,
            processed_signal_length=processed_signal_length
        )
        preds = self.model.forward_infer(emb_seq, emb_seq_length)
        return preds


def export_onnx(wrapper, output_path: Path):
    """Export to ONNX opset 21 — try dynamo first, fall back to legacy."""
    print(f"\nExporting to ONNX (target opset {OPSET_TARGET})...")

    n_mels = 128
    t_mel = 500  # 5 seconds at 10ms stride

    dummy = torch.randn(1, n_mels, t_mel)
    print(f"  Input shape: {list(dummy.shape)} (mel spectrogram)")

    wrapper.eval()

    with torch.no_grad():
        test_out = wrapper(dummy)
        print(f"  Output shape: {list(test_out.shape)}")

        export_kwargs = dict(
            input_names=["processed_signal"],
            output_names=["speaker_logits"],
            dynamic_axes={
                "processed_signal": {0: "batch_size", 2: "mel_frames"},
                "speaker_logits": {0: "batch_size", 1: "diar_frames"},
            },
            opset_version=OPSET_TARGET,
            do_constant_folding=True,
            export_params=True,
        )

        # ── Attempt 1: dynamo=True (new torch.export-based exporter) ──
        try:
            print(f"  Attempting dynamo=True export...")
            torch.onnx.export(wrapper, dummy, str(output_path), dynamo=True, **export_kwargs)
            print(f"  ✅ dynamo=True export succeeded")
        except Exception as e:
            print(f"  ⚠️  dynamo=True failed: {e}")
            print(f"  Falling back to legacy TorchScript exporter...")
            try:
                torch.onnx.export(wrapper, dummy, str(output_path), **export_kwargs)
                print(f"  ✅ Legacy export succeeded")
            except Exception as e2:
                print(f"  ❌ Legacy export also failed: {e2}")
                raise

    size_mb = output_path.stat().st_size / (1024 * 1024)
    print(f"  Exported: {size_mb:.1f} MB")


def validate_onnx(output_path: Path):
    """Validate ONNX model — structure, opset, ORT inference test."""
    print(f"\n{'=' * 60}")
    print(f"Validating ONNX model")
    print(f"{'=' * 60}")

    m = onnx.load(str(output_path))
    onnx.checker.check_model(m)

    size_mb = output_path.stat().st_size / (1024 * 1024)
    print(f"  File size:  {size_mb:.1f} MB")
    print(f"  Opset:      {m.opset_import[0].version}")

    # Input shape info
    for inp in m.graph.input:
        shape = [d.dim_value or "dynamic" for d in inp.type.tensor_type.shape.dim]
        print(f"  Input:      {inp.name} → {shape}")
    for out in m.graph.output:
        shape = [d.dim_value or "dynamic" for d in out.type.tensor_type.shape.dim]
        print(f"  Output:     {out.name} → {shape}")

    # Op statistics — key for quantization planning
    ops = Counter(n.op_type for n in m.graph.node)
    print(f"  Total ops:  {len(m.graph.node)}, unique types: {len(ops)}")
    print(f"\n  Top operations:")
    for op_name, count in ops.most_common(12):
        marker = " ← quantizable" if op_name in ("MatMul", "Gemm") else ""
        print(f"    {op_name}: {count}{marker}")

    matmul_count = ops.get("MatMul", 0)
    gemm_count = ops.get("Gemm", 0)
    quantizable = matmul_count + gemm_count
    print(f"\n  → {quantizable} quantizable MatMul/Gemm nodes ({ops.get('MatMul',0)} MatMul + {ops.get('Gemm',0)} Gemm)")

    # ONNX Runtime inference test
    print(f"\n  ONNX Runtime CPU test...")
    import onnxruntime as ort

    sess = ort.InferenceSession(str(output_path), providers=["CPUExecutionProvider"])

    n_mels = 128
    for dur in [1.0, 3.0, 5.0, 8.0]:
        t_mel = int(dur / 0.01)
        inp = np.random.randn(1, n_mels, t_mel).astype(np.float32)
        out = sess.run(None, {"processed_signal": inp})
        status = "OK" if not np.any(np.isnan(out[0])) else "NaN!"
        print(f"    {dur:.1f}s ({t_mel} mel frames) → {out[0].shape} {status}")

    print(f"\n✅ ONNX model validated!")

    # Return op stats for quantization planning
    return {"matmul_nodes": matmul_count, "gemm_nodes": gemm_count}


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 60)
    print(f"Sortformer 4spk-v2 → ONNX FP32 (Opset {OPSET_TARGET})")
    print("=" * 60)

    config_path = CHECKPOINT_DIR / "model_config.yaml"
    weights_path = CHECKPOINT_DIR / "model_weights.ckpt"

    # 1. Load
    model = load_model_from_config_and_weights(config_path, weights_path)

    # 2. Wrap
    wrapper = SortformerWrapper(model)

    # 3. Export
    export_onnx(wrapper, OUTPUT_PATH)

    # 4. Validate
    stats = validate_onnx(OUTPUT_PATH)

    print(f"\n{'=' * 60}")
    print(f"✅ DONE — Model: {OUTPUT_PATH}")
    print(f"   Opset: {OPSET_TARGET}")
    print(f"   Size:  {OUTPUT_PATH.stat().st_size / (1024 * 1024):.1f} MB")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
