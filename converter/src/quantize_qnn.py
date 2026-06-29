"""Quantize QNN-exported ONNX models using ONNX Runtime static QDQ quantization (CPU-only).

Uses quantize_static with QDQ format + dummy calibration data.
QDQ (QuantizeLinear/DequantizeLinear) is supported by all ORT GenAI EPs.

Encoder: INT8 QDQ (~2.5 GB -> ~700 MB)
Decoder: INT8 QDQ (~57 MB -> ~15 MB)
Joint:   INT8 QDQ (~36 MB -> ~10 MB)

Output: models-onnx/qnn-int8/

Usage:
    cd converter
    python src/quantize_qnn.py
"""

import json
import shutil
from pathlib import Path

import numpy as np
import onnx
from onnxruntime.quantization import QuantFormat, QuantType, quantize_static
from onnxruntime.quantization.calibrate import CalibrationDataReader

_SCRIPT_DIR = Path(__file__).resolve().parent
_RECIPE_ROOT = _SCRIPT_DIR.parent
_PROJECT_ROOT = _RECIPE_ROOT.parent

SRC_DIR = _PROJECT_ROOT / "models-onnx" / "qnn"
DST_DIR = _PROJECT_ROOT / "models-onnx" / "qnn-int8"


class DummyCalibrationDataReader(CalibrationDataReader):
    """Provides dummy calibration data for static quantization.
    
    For each model input, generates random data matching input shapes and types.
    """

    # ONNX element types -> numpy dtypes
    # https://github.com/onnx/onnx/blob/main/onnx/onnx.proto
    ONNX_TYPE_TO_NUMPY = {
        1: np.float32,   # FLOAT
        6: np.int32,     # INT32
        7: np.int64,     # INT64
        9: np.bool_,     # BOOL
    }

    def __init__(self, model_path: Path):
        model = onnx.load(str(model_path))
        self.input_names = [inp.name for inp in model.graph.input]
        self.input_shapes = {}
        self.input_types = {}
        for inp in model.graph.input:
            shape = []
            for dim in inp.type.tensor_type.shape.dim:
                d = dim.dim_value if dim.dim_value > 0 else 1
                shape.append(d)
            self.input_shapes[inp.name] = shape
            self.input_types[inp.name] = inp.type.tensor_type.elem_type

        self.batches = 8
        self.current_batch = 0
        self.data = []
        for _ in range(self.batches):
            batch = {}
            for name in self.input_names:
                shape = self.input_shapes[name]
                dtype_int = self.input_types[name]
                np_dtype = self.ONNX_TYPE_TO_NUMPY.get(dtype_int, np.float32)
                if np_dtype in (np.int64, np.int32, np.bool_):
                    batch[name] = np.zeros(shape, dtype=np_dtype)
                else:
                    batch[name] = np.random.randn(*shape).astype(np_dtype)
            self.data.append(batch)

    def get_next(self):
        if self.current_batch >= self.batches:
            return None
        batch = self.data[self.current_batch]
        self.current_batch += 1
        return batch

    def rewind(self):
        self.current_batch = 0


def quantize_model(src_path: Path, dst_path: Path, label: str) -> bool:
    """Static INT8 QDQ quantization of an ONNX model."""
    print(f"\n-- {label}: INT8 static QDQ quantization --")
    print(f"    Source: {src_path}")

    if not src_path.exists():
        print(f"    ERROR: Source not found")
        return False

    src_size_mb = src_path.stat().st_size / (1024 * 1024)
    data_path = src_path.with_suffix(src_path.suffix + ".data")
    if data_path.exists():
        src_size_mb += data_path.stat().st_size / (1024 * 1024)
    print(f"    Size before: {src_size_mb:.1f} MB")

    use_external = src_size_mb > 2000
    calib_reader = DummyCalibrationDataReader(src_path)

    quantize_static(
        model_input=str(src_path),
        model_output=str(dst_path),
        calibration_data_reader=calib_reader,
        quant_format=QuantFormat.QDQ,
        weight_type=QuantType.QInt8,
        activation_type=QuantType.QInt8,
        use_external_data_format=use_external,
        extra_options={"ActivationSymmetric": True, "WeightSymmetric": True},
    )

    dst_size_mb = dst_path.stat().st_size / (1024 * 1024)
    ratio = src_size_mb / dst_size_mb if dst_size_mb > 0 else 0
    print(f"    Size after:  {dst_size_mb:.1f} MB ({ratio:.1f}x smaller)")
    return True


def main():
    print("=" * 70)
    print("Nemotron QNN - INT8 Static QDQ Quantization (CPU, onnxruntime)")
    print("=" * 70)
    print(f"  Source: {SRC_DIR}")
    print(f"  Dest:   {DST_DIR}")

    DST_DIR.mkdir(parents=True, exist_ok=True)

    quantize_model(SRC_DIR / "encoder.onnx", DST_DIR / "encoder.onnx", "Encoder")
    quantize_model(SRC_DIR / "decoder.onnx", DST_DIR / "decoder.onnx", "Decoder")
    quantize_model(SRC_DIR / "joint.onnx",   DST_DIR / "joint.onnx",   "Joint")

    print("\n-- Copying configs, tokenizer, VAD --")
    for name in [
        "genai_config.json", "audio_processor_config.json",
        "model_config.json", "tokenizer.json", "tokenizer_config.json",
        "vocab.txt", "silero_vad.onnx",
    ]:
        src = SRC_DIR / name
        dst = DST_DIR / name
        if src.exists():
            shutil.copy2(src, dst)
            print(f"  [OK] {name}")

    mc_path = DST_DIR / "model_config.json"
    if mc_path.exists():
        with open(mc_path) as f:
            mc = json.load(f)
        mc["config"]["model_path"] = str(DST_DIR.resolve())
        with open(mc_path, "w") as f:
            json.dump(mc, f, indent=4)

    print(f"\n{'=' * 70}")
    total_mb = sum(f.stat().st_size for f in DST_DIR.iterdir() if f.is_file()) / (1024 * 1024)
    print(f"  Done! INT8 QDQ models -> {DST_DIR}")
    print(f"  Total size: {total_mb:.1f} MB")
    for f in sorted(DST_DIR.iterdir()):
        if f.is_file():
            print(f"    {f.name} ({f.stat().st_size / (1024 * 1024):.1f} MB)")
    print(f"{'=' * 70}")


if __name__ == "__main__":
    main()
