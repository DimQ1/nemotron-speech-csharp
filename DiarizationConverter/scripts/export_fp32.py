"""
Export Sortformer 4spk-v2 from PyTorch → ONNX FP32.

Reads the NeMo checkpoint from raw/ and exports a self-contained ONNX model.

Note on Sortformer export challenges:
- NeMo models use dynamic masking and custom attention ops.
- We attempt torch.jit.trace with a dummy input first.
- If that fails, we try to extract the core transformer and re-wrap it.
- The exported model converts audio waveform → per-frame speaker logits.

References:
- NeMo ONNX export: https://github.com/NVIDIA/NeMo/tree/main/scripts/export
- WeSpeaker ONNX: https://github.com/wenet-e2e/wespeaker/blob/master/wespeaker/bin/infer_onnx.py
"""

import sys
from pathlib import Path

import torch
import onnx
import numpy as np

RAW_DIR = Path(__file__).resolve().parent.parent / "raw"
OUTPUT_DIR = Path(__file__).resolve().parent.parent / "models" / "sortformer_fp32"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"

# Sortformer typical input: 5-second chunks at 16kHz
SAMPLE_RATE = 16000
CHUNK_SECONDS = 5.0
CHUNK_SAMPLES = int(SAMPLE_RATE * CHUNK_SECONDS)


def try_load_nemo_model(model_dir: Path):
    """Attempt to load the NeMo diarization model."""
    try:
        import nemo.collections.asr as nemo_asr
        # NeMo diarization models are loaded via EncDecDiarLabelModel or similar
        # Try common diarization model classes
        model_path = list(model_dir.glob("*.nemo")) + list(model_dir.glob("*.ckpt"))
        if model_path:
            print(f"Found checkpoint: {model_path[0]}")
            model = nemo_asr.models.EncDecDiarLabelModel.restore_from(str(model_path[0]))
            return model

        # Try loading from config
        config_path = model_dir / "model_config.yaml"
        if config_path.exists():
            print(f"Loading from config: {config_path}")
            model = nemo_asr.models.EncDecDiarLabelModel.from_config_file(str(config_path))
            return model

    except ImportError:
        print("NeMo toolkit not installed. Install with: pip install nemo-toolkit")
    except Exception as e:
        print(f"NeMo load failed: {e}")

    return None


def try_load_torch_model(model_dir: Path):
    """Alternative: load raw PyTorch state dict / jit model."""
    pt_files = list(model_dir.glob("*.pt")) + list(model_dir.glob("*.pth"))
    for pt_file in pt_files:
        try:
            model = torch.jit.load(str(pt_file), map_location="cpu")
            print(f"Loaded TorchScript model: {pt_file}")
            return model
        except Exception:
            pass
    return None


def export_to_onnx(model, output_path: Path, input_shape=(1, CHUNK_SAMPLES)):
    """Export PyTorch model to ONNX with dynamic batch and time axes."""
    dummy_input = torch.randn(*input_shape)
    model.eval()

    # Dynamic axes: allow variable batch size and audio length
    dynamic_axes = {
        "audio": {0: "batch_size", 1: "num_samples"},
        "speaker_logits": {0: "batch_size", 1: "num_frames"},
    }

    with torch.no_grad():
        try:
            torch.onnx.export(
                model,
                dummy_input,
                str(output_path),
                input_names=["audio"],
                output_names=["speaker_logits"],
                dynamic_axes=dynamic_axes,
                opset_version=17,
                do_constant_folding=True,
                export_params=True,
            )
            print(f"✅ Exported ONNX FP32 to {output_path}")
            return True
        except Exception as e:
            print(f"❌ torch.onnx.export failed: {e}")

            # Try jit.trace as fallback
            try:
                print("Trying torch.jit.trace fallback...")
                traced = torch.jit.trace(model, dummy_input)
                torch.onnx.export(
                    traced, dummy_input, str(output_path),
                    input_names=["audio"],
                    output_names=["speaker_logits"],
                    dynamic_axes=dynamic_axes,
                    opset_version=17,
                )
                print(f"✅ Exported ONNX FP32 via trace to {output_path}")
                return True
            except Exception as e2:
                print(f"❌ trace fallback also failed: {e2}")
                return False


def validate_onnx(output_path: Path):
    """Check the exported ONNX model."""
    model = onnx.load(str(output_path))
    onnx.checker.check_model(model)

    print(f"\nONNX Model Summary:")
    print(f"  IR version: {model.ir_version}")
    print(f"  Opset: {model.opset_import[0].version}")
    print(f"  Producer: {model.producer_name}")

    # Print I/O shapes
    for inp in model.graph.input:
        shape = [d.dim_value if d.dim_value else "dynamic" for d in inp.type.tensor_type.shape.dim]
        print(f"  Input:  {inp.name} → {shape}")
    for out in model.graph.output:
        shape = [d.dim_value if d.dim_value else "dynamic" for d in out.type.tensor_type.shape.dim]
        print(f"  Output: {out.name} → {shape}")

    # Count ops
    op_types = {}
    for node in model.graph.node:
        op_types[node.op_type] = op_types.get(node.op_type, 0) + 1
    print(f"  Ops: {len(model.graph.node)} total, {len(op_types)} unique types")


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    if not RAW_DIR.exists():
        print("ERROR: raw/ directory not found. Run download_model.py first.")
        sys.exit(1)

    print("=" * 60)
    print("Sortformer FP32 → ONNX Export")
    print("=" * 60)

    # Try to load the model
    model = try_load_torch_model(RAW_DIR)
    if model is None:
        model = try_load_nemo_model(RAW_DIR)

    if model is None:
        print("\n⚠️  Could not load model from raw/ directory.")
        print("The NeMo Sortformer checkpoints may use format-specific loading.")
        print("")
        print("Manual steps to try:")
        print("  1. Open a Python environment with nemo_toolkit installed")
        print("  2. Load model: model = EncDecDiarLabelModel.from_pretrained('nvidia/diar_streaming_sortformer_4spk-v2')")
        print("  3. Export: torch.onnx.export(model, dummy, 'sortformer.onnx', ...)")
        print("")
        print("Creating a placeholder script for manual export...")
        _create_manual_export_script()
        sys.exit(1)

    success = export_to_onnx(model, OUTPUT_PATH)
    if success:
        validate_onnx(OUTPUT_PATH)


def _create_manual_export_script():
    """Create a helper script for manual ONNX export from NeMo."""
    script_path = Path(__file__).resolve().parent / "_manual_export.py"
    script = '''"""
Manual export script — run this in an environment with nemo_toolkit installed.

Usage:
    python _manual_export.py
"""

import torch
import onnx
from pathlib import Path

CHUNK_SAMPLES = 80000  # 5 seconds @ 16kHz
OUTPUT_PATH = Path(__file__).resolve().parent.parent / "models" / "sortformer_fp32" / "sortformer.onnx"

print("Loading Sortformer from HuggingFace via NeMo...")
from nemo.collections.asr.models import EncDecDiarLabelModel

model = EncDecDiarLabelModel.from_pretrained(
    "nvidia/diar_streaming_sortformer_4spk-v2",
    map_location="cpu"
)
model.eval()

dummy = torch.randn(1, CHUNK_SAMPLES)
print(f"Input shape: {dummy.shape}")

with torch.no_grad():
    # Get output shape by running once
    output = model(input_signal=dummy, input_signal_length=torch.tensor([CHUNK_SAMPLES]))
    print(f"Output keys: {output.keys() if isinstance(output, dict) else type(output)}")
    
    if isinstance(output, dict):
        # NeMo models typically return dict with logits
        for k, v in output.items():
            print(f"  {k}: shape={v.shape}")

    class ExportWrapper(torch.nn.Module):
        def forward(self, audio):
            signal_length = torch.full((audio.shape[0],), audio.shape[1], dtype=torch.long)
            result = model(input_signal=audio, input_signal_length=signal_length)
            # Extract logits tensor
            if isinstance(result, dict):
                return result.get("logits", list(result.values())[0])
            return result

    wrapper = ExportWrapper()

    torch.onnx.export(
        wrapper,
        dummy,
        str(OUTPUT_PATH),
        input_names=["audio"],
        output_names=["speaker_logits"],
        dynamic_axes={
            "audio": {0: "batch_size", 1: "num_samples"},
            "speaker_logits": {0: "batch_size", 1: "num_frames"},
        },
        opset_version=17,
        do_constant_folding=True,
    )

print(f"\\nExported to: {OUTPUT_PATH}")
model_check = onnx.load(str(OUTPUT_PATH))
onnx.checker.check_model(model_check)
print("ONNX validation passed!")
'''
    script_path.write_text(script)
    print(f"   Created manual export script: {script_path}")
    print(f"   Run: python {script_path}")


if __name__ == "__main__":
    main()
