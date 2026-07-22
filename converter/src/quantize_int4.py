"""Quantize an exported FP32 encoder.onnx to INT4 (MatMulNBits k-quant).

Uses onnxruntime's MatMulNBitsQuantizer (available in ORT 1.23.x), which is
the same weight-only INT4 scheme (MatMulNBits contrib op) that the shipped
DimQ1 int4 models use.

Usage:
    python src/quantize_int4.py <fp32_encoder.onnx> <out_dir>
"""

import sys
from pathlib import Path

import onnx
from onnxruntime.quantization.matmul_nbits_quantizer import MatMulNBitsQuantizer


def quantize(fp32_encoder: Path, out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)

    # Load FP32 model (external data resolved from the same folder).
    model = onnx.load(str(fp32_encoder))

    quant = MatMulNBitsQuantizer(
        model,
        block_size=32,
        is_symmetric=True,
        accuracy_level=4,  # int8 internal accumulation — matches Olive k-quant
        nodes_to_exclude=None,
    )
    quant.process()

    out_model = out_dir / "encoder.onnx"
    # Save with external data so large weights land in encoder.onnx.data
    onnx.save_model(
        quant.model.model,
        str(out_model),
        save_as_external_data=True,
        all_tensors_to_one_file=True,
        location="encoder.onnx.data",
    )
    size_mb = (out_dir / "encoder.onnx.data").stat().st_size / 1e6
    print(f"[ok] INT4 encoder -> {out_model} (+data {size_mb:.1f} MB)")


if __name__ == "__main__":
    quantize(Path(sys.argv[1]), Path(sys.argv[2]))
