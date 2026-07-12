"""
Quantize FP32 ONNX model → INT8 (dynamic quantization).

Uses ONNX Runtime's quantize_dynamic — weights-only quantization.
No calibration data required. Suitable for CPU inference.
"""

import sys
from pathlib import Path

import onnx
from onnxruntime.quantization import quantize_dynamic, QuantType

FP32_PATH = Path(__file__).resolve().parent.parent / "models" / "sortformer_fp32" / "sortformer.onnx"
OUTPUT_DIR = Path(__file__).resolve().parent.parent / "models" / "sortformer_int8"
OUTPUT_PATH = OUTPUT_DIR / "sortformer.onnx"


def main():
    if not FP32_PATH.exists():
        print(f"ERROR: FP32 model not found at {FP32_PATH}")
        print("Run export_fp32.py first.")
        sys.exit(1)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("=" * 60)
    print("Sortformer FP32 → INT8 Dynamic Quantization")
    print("=" * 60)

    # Dynamic quantization: weights → INT8, activations stay FP32
    quantize_dynamic(
        model_input=str(FP32_PATH),
        model_output=str(OUTPUT_PATH),
        weight_type=QuantType.QInt8,
        extra_options={
            "ActivationSymmetric": True,
            "WeightSymmetric": True,
        },
    )

    # Validate
    model = onnx.load(str(OUTPUT_PATH))
    onnx.checker.check_model(model)

    # Size comparison
    fp32_size = FP32_PATH.stat().st_size / (1024 * 1024)
    int8_size = OUTPUT_PATH.stat().st_size / (1024 * 1024)
    reduction = (1 - int8_size / fp32_size) * 100

    print(f"\n✅ Quantized INT8 model: {OUTPUT_PATH}")
    print(f"   FP32 size: {fp32_size:.1f} MB")
    print(f"   INT8 size: {int8_size:.1f} MB")
    print(f"   Reduction: {reduction:.1f}%")


if __name__ == "__main__":
    main()
