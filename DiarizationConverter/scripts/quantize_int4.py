"""
Quantize FP32 ONNX model → INT4 (static quantization with calibration).

Uses ONNX Runtime's quantize_static — needs calibration data from dataset.
INT4 provides the smallest model size but may have reduced accuracy.

⚠️ INT4 support requires ONNX Runtime >= 1.17 and opset >= 19.
"""

import sys
from pathlib import Path

import numpy as np
import onnx
from onnxruntime.quantization import quantize_static, QuantType, CalibrationDataReader

FP32_PATH = Path(__file__).resolve().parent.parent / "models" / "sortformer_fp32" / "sortformer.onnx"
DATASET_DIR = Path(__file__).resolve().parent.parent / "dataset" / "audio"
OUTPUT_DIR = Path(__file__).resolve().parent.parent / "models" / "sortformer_int4"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"

SAMPLE_RATE = 16000
CHUNK_SECONDS = 5.0
CHUNK_SAMPLES = int(SAMPLE_RATE * CHUNK_SECONDS)
CALIBRATION_COUNT = 10  # Number of audio chunks for calibration


class DiarizationCalibrationReader(CalibrationDataReader):
    """Provides audio chunks from the test dataset for calibration."""

    def __init__(self, audio_dir: Path, num_samples: int, max_files: int):
        self.num_samples = num_samples
        self.audio_files = sorted(audio_dir.glob("*.wav"))[:max_files]
        if not self.audio_files:
            print(f"⚠️  No WAV files found in {audio_dir}")
            print("   Using random noise for calibration.")

        self.index = 0
        self.iter_next_called = False

    def get_next(self):
        if self.iter_next_called:
            return None
        self.iter_next_called = True

        if self.index >= len(self.audio_files) and self.audio_files:
            return None

        try:
            import soundfile as sf
            audio, sr = sf.read(str(self.audio_files[self.index]))
            self.index += 1

            # Resample to 16kHz if needed
            if sr != SAMPLE_RATE:
                import librosa
                audio = librosa.resample(audio, orig_sr=sr, target_sr=SAMPLE_RATE)

            # Ensure mono
            if audio.ndim > 1:
                audio = audio.mean(axis=1)

            # Pad or trim to fixed length
            audio = audio[: self.num_samples]
            if len(audio) < self.num_samples:
                audio = np.pad(audio, (0, self.num_samples - len(audio)))

            return {"audio": audio.astype(np.float32).reshape(1, -1)}
        except Exception as e:
            print(f"Warning: failed to load {self.audio_files[self.index - 1]}: {e}")
            return {"audio": np.random.randn(1, self.num_samples).astype(np.float32)}


def main():
    if not FP32_PATH.exists():
        print(f"ERROR: FP32 model not found at {FP32_PATH}")
        print("Run export_fp32.py first.")
        sys.exit(1)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 60)
    print("Sortformer FP32 → INT4 Static Quantization")
    print("=" * 60)

    calib_reader = DiarizationCalibrationReader(
        DATASET_DIR,
        num_samples=CHUNK_SAMPLES,
        max_files=CALIBRATION_COUNT,
    )

    try:
        quantize_static(
            model_input=str(FP32_PATH),
            model_output=str(OUTPUT_PATH),
            calibration_data_reader=calib_reader,
            weight_type=QuantType.QInt4,
            activation_type=QuantType.QInt8,  # Activations stay INT8
            extra_options={
                "CalibMovingAverage": True,
                "CalibNumBins": 128,
            },
        )

        # Validate
        model = onnx.load(str(OUTPUT_PATH))
        onnx.checker.check_model(model)

        fp32_size = FP32_PATH.stat().st_size / (1024 * 1024)
        int4_size = OUTPUT_PATH.stat().st_size / (1024 * 1024)
        reduction = (1 - int4_size / fp32_size) * 100

        print(f"\n✅ Quantized INT4 model: {OUTPUT_PATH}")
        print(f"   FP32 size: {fp32_size:.1f} MB")
        print(f"   INT4 size: {int4_size:.1f} MB")
        print(f"   Reduction: {reduction:.1f}%")

    except Exception as e:
        print(f"\n❌ INT4 quantization failed: {e}")
        print("\nINT4 quantization requires:")
        print("  - ONNX Runtime >= 1.17.0")
        print("  - Model opset >= 19 (may need re-export)")
        print("  - All ops support INT4 execution")
        print("\nFalling back: use INT8 model instead.")
        sys.exit(1)


if __name__ == "__main__":
    main()
