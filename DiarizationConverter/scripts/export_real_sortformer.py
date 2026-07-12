"""
Real NeMo Sortformer → ONNX export — Windows CPU.

Uses precise stubs for NVIDIA GPU packages.
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

ROOT = Path(__file__).resolve().parent.parent
CHECKPOINT_DIR = ROOT / "raw" / "extracted"
OUTPUT_DIR = ROOT / "models" / "sortformer_fp32"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"

SAMPLE_RATE = 16000
NUM_SPEAKERS = 4


def load_model_from_config_and_weights(config_path: Path, weights_path: Path):
    """Load SortformerEncLabelModel with NeMo."""
    print(f"\nLoading model...")
    print(f"  Config: {config_path}")
    print(f"  Weights: {weights_path}")

    # Fix config for NeMo 2.2.0 compatibility
    # Model was saved with nemo_version 2.2.0rc0 which has different SortformerModules API
    with open(config_path) as f:
        cfg = yaml.safe_load(f)

    # Disable streaming mode — not supported in NeMo 2.2.0 release
    cfg["streaming_mode"] = False

    # Fix sortformer_modules: remove streaming params, add hidden_size
    sfm = cfg.get("sortformer_modules", {})
    # Remove rc0-specific streaming params not in 2.2.0 release
    for old_param in ["spkcache_len", "fifo_len", "chunk_len", "spkcache_update_period",
                      "chunk_left_context", "chunk_right_context",
                      "spkcache_sil_frames_per_spk", "scores_add_rnd",
                      "causal_attn_rate", "causal_attn_rc"]:
        sfm.pop(old_param, None)
    # Add hidden_size (required by 2.2.0, not present in rc0 config)
    if "hidden_size" not in sfm:
        sfm["hidden_size"] = cfg.get("transformer_encoder", {}).get("hidden_size", 192)
    # Remove _target_ from sortformer_modules (NeMo 2.2.0 handles it differently)
    # Keep it - it's needed

    # Write fixed config
    fixed_config_path = CHECKPOINT_DIR / "model_config_fixed.yaml"
    with open(fixed_config_path, "w") as f:
        yaml.dump(cfg, f)
    print(f"  Fixed config written to {fixed_config_path}")

    # Try from_config_file with FIXED config first (most reliable for version mismatches)
    from nemo.collections.asr.models.sortformer_diar_models import SortformerEncLabelModel

    nemo_path = ROOT / "raw" / "diar_streaming_sortformer_4spk-v2.nemo"
    model = None

    print(f"  Trying from_config_file with fixed config...")
    model = SortformerEncLabelModel.from_config_file(str(fixed_config_path))
    print(f"  Model created from fixed config.")

    # Load weights
    checkpoint = torch.load(str(weights_path), map_location="cpu", weights_only=False)
    if "state_dict" in checkpoint:
        state_dict = checkpoint["state_dict"]
    else:
        state_dict = checkpoint

    # Clean NeMo prefix
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

    if model is None:
        raise RuntimeError("Failed to load model.")

    model.eval()
    model.to("cpu")

    total_params = sum(p.numel() for p in model.parameters())
    print(f"  Params: {total_params:,}")
    return model


def test_forward(model):
    """Quick forward-pass test using processed_signal (mel spectrogram)."""
    print(f"\nTesting forward pass...")

    # Preprocess: audio → mel spectrogram
    for dur in [1.0, 2.5, 5.0, 10.0]:
        samples = int(SAMPLE_RATE * dur)
        audio = torch.randn(1, samples)
        sig_len = torch.tensor([samples], dtype=torch.long)

        with torch.no_grad():
            processed, proc_len = model.process_signal(
                audio_signal=audio, audio_signal_length=sig_len
            )
            # Call full model
            output = model(audio_signal=audio, audio_signal_length=sig_len)

        print(f"  {dur:.1f}s → mel: {list(processed.shape)}, output: {list(output.shape)}")


class SortformerWrapper(torch.nn.Module):
    """
    Wraps NeMo model for ONNX export.
    
    Input:  processed_signal [B, n_mels, T_mel] — mel spectrogram features
    Output: speaker_logits    [B, T_diar, num_speakers] — per-frame speaker probabilities
    
    The audio preprocessing (STFT → mel) must be done separately in C#/Python
    before calling this model. This is because ONNX doesn't support complex STFT.
    
    Preprocessing params (from model config):
      - sample_rate: 16000
      - window_size: 0.025s (25ms)
      - window_stride: 0.01s (10ms)  
      - n_fft: 512
      - n_mels: 128
      - window: hann
    """
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, processed_signal: torch.Tensor) -> torch.Tensor:
        """
        Args:
            processed_signal: [B, n_mels, T_mel] mel spectrogram features
        Returns:
            speaker_logits: [B, T_diar, 4] per-frame speaker probabilities (sigmoid)
        """
        # Compute processed_signal_length from input shape
        batch = processed_signal.shape[0]
        mel_len = processed_signal.shape[2]
        processed_signal_length = torch.full((batch,), mel_len, dtype=torch.long, device=processed_signal.device)

        # Forward through encoder
        emb_seq, emb_seq_length = self.model.frontend_encoder(
            processed_signal=processed_signal,
            processed_signal_length=processed_signal_length
        )

        # Forward through sortformer inference
        preds = self.model.forward_infer(emb_seq, emb_seq_length)
        return preds


def export_onnx(wrapper, output_path: Path):
    """Export to ONNX with dynamic axes."""
    print(f"\nExporting to ONNX...")

    # Input: mel spectrogram [B, 128, T_mel] — 128 mel bins, variable time
    # For 5 seconds: T_mel = 5 / 0.01 = 500 frames
    n_mels = 128
    t_mel = 500  # 5 seconds at 10ms stride

    dummy = torch.randn(1, n_mels, t_mel)
    print(f"  Input shape: {list(dummy.shape)} (mel spectrogram)")

    with torch.no_grad():
        test_out = wrapper(dummy)
        print(f"  Output shape: {list(test_out.shape)}")

        torch.onnx.export(
            wrapper,
            dummy,
            str(output_path),
            input_names=["processed_signal"],
            output_names=["speaker_logits"],
            dynamic_axes={
                "processed_signal": {0: "batch_size", 2: "mel_frames"},
                "speaker_logits": {0: "batch_size", 1: "diar_frames"},
            },
            opset_version=17,
            do_constant_folding=True,
            export_params=True,
        )

    size_mb = output_path.stat().st_size / (1024 * 1024)
    print(f"  Exported: {size_mb:.1f} MB")


def validate_onnx(output_path: Path):
    """Validate ONNX model."""
    print(f"\nValidating ONNX...")

    # ONNX check
    m = onnx.load(str(output_path))
    onnx.checker.check_model(m)

    size_mb = output_path.stat().st_size / (1024 * 1024)
    print(f"  Size: {size_mb:.1f} MB")
    print(f"  Opset: {m.opset_import[0].version}")

    # Op stats
    ops = {}
    for n in m.graph.node:
        ops[n.op_type] = ops.get(n.op_type, 0) + 1
    print(f"  Total ops: {len(m.graph.node)}, unique: {len(ops)}")
    top_ops = sorted(ops.items(), key=lambda x: -x[1])[:8]
    for op, count in top_ops:
        print(f"    {op}: {count}")

    # I/O
    for inp in m.graph.input:
        shape = [d.dim_value or "dynamic" for d in inp.type.tensor_type.shape.dim]
        print(f"  Input:  {inp.name} → {shape}")
    for out in m.graph.output:
        shape = [d.dim_value or "dynamic" for d in out.type.tensor_type.shape.dim]
        print(f"  Output: {out.name} → {shape}")

    # ONNX Runtime test
    print(f"\n  ONNX Runtime CPU test...")
    import onnxruntime as ort
    sess = ort.InferenceSession(str(output_path), providers=["CPUExecutionProvider"])

    n_mels = 128
    for dur in [1.0, 3.0, 5.0, 8.0]:
        t_mel = int(dur / 0.01)  # 10ms stride
        inp = np.random.randn(1, n_mels, t_mel).astype(np.float32)
        out = sess.run(None, {"processed_signal": inp})
        ok = "OK" if not np.any(np.isnan(out[0])) else "NaN!"
        print(f"    {dur:.1f}s ({t_mel} mel frames) → {out[0].shape} {ok}")

    print(f"\n✅ ONNX model validated!")


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 60)
    print("Sortformer 4spk-v2  →  ONNX FP32  (Real Export)")
    print("=" * 60)

    config_path = CHECKPOINT_DIR / "model_config.yaml"
    weights_path = CHECKPOINT_DIR / "model_weights.ckpt"

    # 1. Load
    model = load_model_from_config_and_weights(config_path, weights_path)

    # 2. Test
    test_forward(model)

    # 3. Wrap
    wrapper = SortformerWrapper(model)
    wrapper.eval()

    # 4. Export
    export_onnx(wrapper, OUTPUT_PATH)

    # 5. Validate
    validate_onnx(OUTPUT_PATH)

    print(f"\n{'=' * 60}")
    print(f"✅ DONE — Model: {OUTPUT_PATH}")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
