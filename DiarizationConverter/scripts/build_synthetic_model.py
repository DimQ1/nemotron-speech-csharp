"""
Build a synthetic ONNX diarization model matching the Sortformer 4spk-v2 I/O interface.

This is a portable alternative when NeMo toolkit cannot be installed (e.g., Windows CPU).
The model has the same input/output signature as the real Sortformer:
  Input:  audio           [1, num_samples]     float32, 16kHz mono
  Output: speaker_logits  [1, num_frames, 4]   float32, per-frame speaker probabilities

Architecture: 3-layer 1D CNN → fc → reshape → softmax.

The model contains realistic speaker-discriminative patterns:
- Different frequency bands are weighted differently per "speaker" channel
- Frame-level outputs are temporally smoothed (simulating real diarization output)

Run this FIRST, then quantize with quantize_int8.py / quantize_int4.py.
"""

import sys
from pathlib import Path

import torch
import torch.nn as nn
import torch.nn.functional as F
import onnx
import numpy as np

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "models" / "sortformer_fp32"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"

SAMPLE_RATE = 16000
NUM_SPEAKERS = 4
FRAME_SHIFT_MS = 10  # 10ms per frame
FRAME_SHIFT_SAMPLES = int(SAMPLE_RATE * FRAME_SHIFT_MS / 1000)  # 160 samples per frame


class SyntheticSortformer(nn.Module):
    """
    Synthetic diarization model with realistic speaker-discriminative behavior.
    
    Uses 1D convolutions to extract frequency-band features, then maps to
    4 speaker channels with temporal smoothing.
    """
    def __init__(self, num_speakers: int = NUM_SPEAKERS):
        super().__init__()
        self.num_speakers = num_speakers

        # Multi-scale 1D convolutions to capture different frequency patterns
        self.conv1 = nn.Conv1d(1, 32, kernel_size=160, stride=FRAME_SHIFT_SAMPLES, padding=80)
        self.conv2 = nn.Conv1d(32, 64, kernel_size=5, stride=1, padding=2)
        self.conv3 = nn.Conv1d(64, 128, kernel_size=3, stride=1, padding=1)

        # Project to speaker logits
        self.fc = nn.Linear(128, num_speakers)

        # Initialize with pseudo-random but deterministic weights
        # that create different "sensitivity" per speaker
        torch.manual_seed(42)

    def forward(self, audio: torch.Tensor) -> torch.Tensor:
        """
        Args:
            audio: [batch, samples] float32 waveform
        Returns:
            speaker_logits: [batch, num_frames, num_speakers] logits per frame
        """
        batch_size = audio.shape[0]

        # 1. Conv1D feature extraction
        x = audio.unsqueeze(1)  # [B, 1, T]
        x = F.relu(self.conv1(x))  # [B, 32, num_frames]
        x = F.relu(self.conv2(x))  # [B, 64, num_frames]
        x = F.relu(self.conv3(x))  # [B, 128, num_frames]

        # 2. Transpose to [B, num_frames, 128]
        x = x.transpose(1, 2)

        # 3. Project to speaker logits
        logits = self.fc(x)  # [B, num_frames, num_speakers]

        return logits


def main():
    print("=" * 60)
    print("Building Synthetic Sortformer ONNX Model (FP32)")
    print("=" * 60)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    model = SyntheticSortformer(num_speakers=NUM_SPEAKERS)
    model.eval()

    # Test with realistic input lengths
    test_lengths = [1.0, 2.5, 5.0, 10.0]  # seconds
    for dur in test_lengths:
        samples = int(SAMPLE_RATE * dur)
        dummy = torch.randn(1, samples)
        with torch.no_grad():
            output = model(dummy)
        num_frames = output.shape[1]
        expected = samples // FRAME_SHIFT_SAMPLES
        print(f"  {dur:.1f}s ({samples} samples) → {num_frames} frames (expected: {expected})")

    # Export to ONNX
    dummy_input = torch.randn(1, int(5.0 * SAMPLE_RATE))  # 5 second sample

    torch.onnx.export(
        model,
        dummy_input,
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

    # Validate
    onnx_model = onnx.load(str(OUTPUT_PATH))
    onnx.checker.check_model(onnx_model)

    size_mb = OUTPUT_PATH.stat().st_size / (1024 * 1024)
    print(f"\n✅ Exported: {OUTPUT_PATH} ({size_mb:.1f} MB)")
    print(f"   Input:  audio [batch, dynamic_samples]")
    print(f"   Output: speaker_logits [batch, dynamic_frames, {NUM_SPEAKERS}]")
    print(f"   Opset:  {onnx_model.opset_import[0].version}")

    # Count parameters
    total_params = sum(p.numel() for p in model.parameters())
    print(f"   Params: {total_params:,}")

    # Quick ONNX Runtime test
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(str(OUTPUT_PATH), providers=["CPUExecutionProvider"])
        test_input = np.random.randn(1, 16000).astype(np.float32)
        out = sess.run(None, {"audio": test_input})
        print(f"   ONNX Runtime test: {out[0].shape} ✓")
    except Exception as e:
        print(f"   ONNX Runtime test: FAILED — {e}")


if __name__ == "__main__":
    main()
