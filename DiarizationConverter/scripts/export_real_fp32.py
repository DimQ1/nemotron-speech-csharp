"""
Export REAL Sortformer 4spk-v2 from NeMo checkpoint → ONNX FP32.

Loads the .nemo checkpoint via NeMo toolkit, traces the forward pass,
and exports a self-contained ONNX model for CPU inference.

Usage:
    python scripts/export_real_fp32.py
"""

import sys
from pathlib import Path

import torch
import onnx
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
CHECKPOINT_DIR = ROOT / "raw" / "extracted"
OUTPUT_DIR = ROOT / "models" / "sortformer_fp32"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"

SAMPLE_RATE = 16000
CHUNK_SECONDS = 5.0
CHUNK_SAMPLES = int(SAMPLE_RATE * CHUNK_SECONDS)


def load_sortformer_model(ckpt_dir: Path):
    """
    Load SortformerEncLabelModel from extracted .nemo checkpoint.
    """
    import nemo.collections.asr as nemo_asr

    config_path = ckpt_dir / "model_config.yaml"
    weights_path = ckpt_dir / "model_weights.ckpt"

    if not config_path.exists():
        raise FileNotFoundError(f"Config not found: {config_path}")
    if not weights_path.exists():
        raise FileNotFoundError(f"Weights not found: {weights_path}")

    print(f"Loading SortformerEncLabelModel from: {ckpt_dir}")

    # Load via config + weights
    model = nemo_asr.models.SortformerEncLabelModel.from_config_file(
        str(config_path)
    )

    # Load weights
    checkpoint = torch.load(str(weights_path), map_location="cpu", weights_only=False)
    # NeMo checkpoints have 'state_dict' key
    if "state_dict" in checkpoint:
        state_dict = checkpoint["state_dict"]
    else:
        state_dict = checkpoint

    # Remove prefix if present (NeMo sometimes adds 'model.' prefix)
    cleaned = {}
    for k, v in state_dict.items():
        if k.startswith("model."):
            cleaned[k[6:]] = v
        else:
            cleaned[k] = v

    model.load_state_dict(cleaned, strict=False)
    model.eval()
    model.to("cpu")

    print(f"  Model loaded. Parameters: {sum(p.numel() for p in model.parameters()):,}")
    return model


class SortformerExportWrapper(torch.nn.Module):
    """
    Wrapper for ONNX export.
    
    The NeMo SortformerEncLabelModel.forward() expects:
        input_signal:   [B, T]      float32 waveform
        input_signal_length: [B]     int64 tensor with sample counts
    
    Returns (typically):
        logits:  [B, num_frames, num_speakers]
        OR dict with 'logits' key
    """
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, audio: torch.Tensor) -> torch.Tensor:
        """
        Args:
            audio: [batch, samples] float32 waveform
        Returns:
            speaker_logits: [batch, num_frames, num_speakers]
        """
        batch_size = audio.shape[0]
        num_samples = audio.shape[1]

        # Create signal_length from audio shape
        signal_length = torch.full(
            (batch_size,), num_samples, dtype=torch.long, device=audio.device
        )

        # Call NeMo model
        output = self.model(
            input_signal=audio,
            input_signal_length=signal_length,
        )

        # Handle different output formats
        if isinstance(output, dict):
            # NeMo models typically return dict
            if "logits" in output:
                return output["logits"]
            elif "log_probs" in output:
                return output["log_probs"]
            else:
                # Return first tensor value
                for v in output.values():
                    if isinstance(v, torch.Tensor):
                        return v
                raise KeyError(f"No tensor found in output dict: {list(output.keys())}")
        elif isinstance(output, torch.Tensor):
            return output
        elif isinstance(output, (list, tuple)):
            return output[0]
        else:
            raise TypeError(f"Unexpected output type: {type(output)}")


def test_forward(model, wrapper):
    """Test forward pass and inspect shapes."""
    print("\nTesting forward pass...")

    test_durations = [1.0, 2.5, 5.0, 10.0]
    for dur in test_durations:
        samples = int(SAMPLE_RATE * dur)
        dummy = torch.randn(1, samples)

        with torch.no_grad():
            output = wrapper(dummy)

        print(f"  {dur:.1f}s ({samples} samples) → output shape: {list(output.shape)}")

        # Check output is valid
        assert not torch.isnan(output).any(), f"NaN in output for {dur}s!"
        assert not torch.isinf(output).any(), f"Inf in output for {dur}s!"
        assert output.shape[0] == 1, f"Batch dim mismatch for {dur}s!"
        assert output.shape[-1] == 4, f"Expected 4 speakers, got {output.shape[-1]} for {dur}s!"

    print("  ✓ All tests passed")


def export_to_onnx(wrapper, output_path: Path):
    """Export wrapped model to ONNX with dynamic axes."""
    dummy_input = torch.randn(1, CHUNK_SAMPLES)

    print(f"\nExporting to ONNX...")
    print(f"  Dummy input shape: {list(dummy_input.shape)}")

    with torch.no_grad():
        # Test run to get output shape
        test_out = wrapper(dummy_input)
        print(f"  Output shape: {list(test_out.shape)}")

        torch.onnx.export(
            wrapper,
            dummy_input,
            str(output_path),
            input_names=["audio"],
            output_names=["speaker_logits"],
            dynamic_axes={
                "audio": {0: "batch_size", 1: "num_samples"},
                "speaker_logits": {0: "batch_size", 1: "num_frames"},
            },
            opset_version=17,
            do_constant_folding=True,
            export_params=True,
        )

    print(f"  ✓ Exported to {output_path}")


def validate_onnx(output_path: Path):
    """Validate the exported ONNX model."""
    print(f"\nValidating ONNX model...")

    # ONNX checker
    onnx_model = onnx.load(str(output_path))
    onnx.checker.check_model(onnx_model)

    size_mb = output_path.stat().st_size / (1024 * 1024)
    print(f"  Size: {size_mb:.1f} MB")
    print(f"  IR version: {onnx_model.ir_version}")
    print(f"  Opset: {onnx_model.opset_import[0].version}")

    # Count unique ops
    ops = {}
    for node in onnx_model.graph.node:
        ops[node.op_type] = ops.get(node.op_type, 0) + 1
    print(f"  Total ops: {len(onnx_model.graph.node)}, unique: {len(ops)}")
    for op, count in sorted(ops.items(), key=lambda x: -x[1])[:10]:
        print(f"    {op}: {count}")

    # I/O shapes
    for inp in onnx_model.graph.input:
        shape = [d.dim_value if d.dim_value else "dynamic" for d in inp.type.tensor_type.shape.dim]
        print(f"  Input:  {inp.name} → {shape}")
    for out in onnx_model.graph.output:
        shape = [d.dim_value if d.dim_value else "dynamic" for d in out.type.tensor_type.shape.dim]
        print(f"  Output: {out.name} → {shape}")

    # ONNX Runtime test
    print(f"\n  ONNX Runtime inference test...")
    try:
        import onnxruntime as ort
        sess_options = ort.SessionOptions()
        sess_options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        sess = ort.InferenceSession(str(output_path), sess_options=sess_options,
                                     providers=["CPUExecutionProvider"])

        test_input = np.random.randn(1, 16000).astype(np.float32)
        out = sess.run(None, {"audio": test_input})
        print(f"  ✓ Output shape: {out[0].shape}")
        print(f"  ✓ No NaN: {not np.any(np.isnan(out[0]))}")

        # Test with different lengths
        for dur in [2.0, 5.0, 8.0]:
            samples = int(SAMPLE_RATE * dur)
            inp = np.random.randn(1, samples).astype(np.float32)
            out = sess.run(None, {"audio": inp})
            print(f"    {dur:.1f}s → {out[0].shape}")

    except Exception as e:
        print(f"  ⚠️  ONNX Runtime test: {e}")

    print(f"\n✅ ONNX validation complete!")


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 60)
    print("Sortformer 4spk-v2 → ONNX FP32 (Real Export)")
    print("=" * 60)

    # Step 1: Load model
    model = load_sortformer_model(CHECKPOINT_DIR)

    # Step 2: Wrap for ONNX
    wrapper = SortformerExportWrapper(model)

    # Step 3: Test forward pass
    test_forward(model, wrapper)

    # Step 4: Export to ONNX
    export_to_onnx(wrapper, OUTPUT_PATH)

    # Step 5: Validate
    validate_onnx(OUTPUT_PATH)

    print(f"\n{'=' * 60}")
    print(f"✅ Real Sortformer ONNX model ready: {OUTPUT_PATH}")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
