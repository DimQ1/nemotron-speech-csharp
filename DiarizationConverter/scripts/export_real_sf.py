"""
Real NeMo Sortformer → ONNX export — with monkey-patching for Windows CPU.

This script stubs out unnecessary NeMo dependencies (CUDA, Megatron, nv_one_logger)
to load the model and export to ONNX on Windows CPU-only environments.
"""

import sys
from pathlib import Path

# ── Monkey-patch missing NVIDIA GPU packages ──────────────────────────

class FakeModule:
    """Fake module that returns itself for any attribute access."""
    def __init__(self, name=""):
        self.__name__ = name
    def __getattr__(self, name):
        return FakeModule(f"{self.__name__}.{name}")
    def __call__(self, *args, **kwargs):
        return FakeModule()
    def __repr__(self):
        return f"<FakeModule:{self.__name__}>"

# Stub out nv_one_logger before NeMo tries to import it
sys.modules["nv_one_logger"] = FakeModule("nv_one_logger")
sys.modules["nv_one_logger.api"] = FakeModule("nv_one_logger.api")
sys.modules["nv_one_logger.api.config"] = FakeModule("nv_one_logger.api.config")

# Also stub out other GPU-specific packages NeMo may try to import
for mod_name in ["megatron", "megatron.core", "transformer_engine", "apex", "nvidia_resiliency_ext"]:
    if mod_name not in sys.modules:
        sys.modules[mod_name] = FakeModule(mod_name)

print("Monkey-patches applied.")

# ── Now import NeMo ───────────────────────────────────────────────────

import torch
import onnx
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
CHECKPOINT_DIR = ROOT / "raw" / "extracted"
OUTPUT_DIR = ROOT / "models" / "sortformer_fp32"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"

SAMPLE_RATE = 16000
NUM_SPEAKERS = 4


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 60)
    print("Real Sortformer 4spk-v2 → ONNX FP32")
    print("=" * 60)

    # 1. Load model via NeMo
    print("\nLoading SortformerEncLabelModel...")

    from nemo.collections.asr.models.sortformer_diar_models import SortformerEncLabelModel

    config_path = CHECKPOINT_DIR / "model_config.yaml"
    weights_path = CHECKPOINT_DIR / "model_weights.ckpt"

    model = SortformerEncLabelModel.from_config_file(str(config_path))
    print(f"  Config loaded from: {config_path}")

    # Load state dict
    checkpoint = torch.load(str(weights_path), map_location="cpu", weights_only=False)
    if "state_dict" in checkpoint:
        state_dict = checkpoint["state_dict"]
    else:
        state_dict = checkpoint

    # Clean prefix
    cleaned = {}
    for k, v in state_dict.items():
        if k.startswith("model."):
            cleaned[k[6:]] = v
        else:
            cleaned[k] = v

    model.load_state_dict(cleaned, strict=False)
    model.eval()
    model.to("cpu")

    total_params = sum(p.numel() for p in model.parameters())
    print(f"  Model loaded. Params: {total_params:,}")

    # 2. Test forward pass
    print("\nTesting forward pass...")
    for dur in [1.0, 2.5, 5.0, 10.0]:
        samples = int(SAMPLE_RATE * dur)
        audio = torch.randn(1, samples)
        sig_len = torch.tensor([samples], dtype=torch.long)

        with torch.no_grad():
            output = model(input_signal=audio, input_signal_length=sig_len)

        if isinstance(output, dict):
            shape = list(list(output.values())[0].shape)
        elif isinstance(output, torch.Tensor):
            shape = list(output.shape)
        else:
            shape = str(type(output))
        print(f"  {dur:.1f}s → output: {shape}")

    # 3. Wrap for ONNX
    class SortformerONNXWrapper(torch.nn.Module):
        def __init__(self, nemo_model):
            super().__init__()
            self.model = nemo_model

        def forward(self, audio: torch.Tensor) -> torch.Tensor:
            batch_size = audio.shape[0]
            num_samples = audio.shape[1]
            sig_len = torch.full((batch_size,), num_samples, dtype=torch.long, device=audio.device)
            output = self.model(input_signal=audio, input_signal_length=sig_len)
            if isinstance(output, dict):
                # Return first tensor
                for v in output.values():
                    if isinstance(v, torch.Tensor):
                        return v
            return output

    wrapper = SortformerONNXWrapper(model)
    wrapper.eval()

    # 4. Export to ONNX
    print(f"\nExporting to ONNX...")
    dummy = torch.randn(1, int(5.0 * SAMPLE_RATE))

    with torch.no_grad():
        test_out = wrapper(dummy)
        print(f"  Test output shape: {list(test_out.shape)}")

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
            export_params=True,
        )

    # 5. Validate
    onnx_model = onnx.load(str(OUTPUT_PATH))
    onnx.checker.check_model(onnx_model)

    size_mb = OUTPUT_PATH.stat().st_size / (1024 * 1024)
    print(f"\n✅ Real Sortformer ONNX model: {OUTPUT_PATH}")
    print(f"   Size: {size_mb:.1f} MB")

    # Test with ONNX Runtime
    import onnxruntime as ort
    sess = ort.InferenceSession(str(OUTPUT_PATH), providers=["CPUExecutionProvider"])
    test_input = np.random.randn(1, 16000).astype(np.float32)
    out = sess.run(None, {"audio": test_input})
    print(f"   ONNX Runtime test: {out[0].shape} ✓")

    print(f"\n{'=' * 60}")
    print(f"✅ Export complete!")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
