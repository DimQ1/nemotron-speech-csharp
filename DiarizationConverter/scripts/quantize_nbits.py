"""
Quantize FP32 ONNX Sortformer model → INT4 / INT8 with real weight compression.

Uses ONNX Runtime's MatMulNBitsQuantizer (ORT ≥ 1.18):
  - INT4:  accuracy_level=4, block_size=32,  QOperator format (MatMulNBits nodes)
  - INT8:  accuracy_level=8, block_size=64,  QOperator format (MatMulNBits nodes)

Unlike the old INT8 QDQ approach (which stored weights in FP32 and added fake Q/DQ nodes),
MatMulNBits actually compresses weights — INT4 ≈ 3–4× reduction, INT8 ≈ 2× reduction.

Opset 21 is required for MatMulNBits operator support in ONNX Runtime CPU EP.

Usage:
    python scripts/quantize_nbits.py          # Both INT4 and INT8
    python scripts/quantize_nbits.py --int4   # INT4 only
    python scripts/quantize_nbits.py --int8   # INT8 only
"""

import argparse
import sys
import time
from pathlib import Path

import onnx
import numpy as np
from onnxruntime.quantization.matmul_nbits_quantizer import MatMulNBitsQuantizer

ROOT = Path(__file__).resolve().parent.parent
FP32_PATH = ROOT / "models" / "sortformer_fp32" / "sortformer.onnx"

OUTPUT_DIRS = {
    4: ROOT / "models" / "sortformer_int4",
    8: ROOT / "models" / "sortformer_int8",
}

# ── Quantization presets ──────────────────────────────────────────────
PRESETS = {
    4: {
        "label": "INT4",
        "accuracy_level": 4,
        "block_size": 32,
        "is_symmetric": True,
        "description": "4-bit weights, block_size=32, symmetric",
    },
    8: {
        "label": "INT8",
        "accuracy_level": 8,
        "block_size": 64,
        "is_symmetric": True,
        "description": "8-bit weights, block_size=64, symmetric",
    },
}


def quantize_model(
    fp32_path: Path,
    output_path: Path,
    accuracy_level: int,
    block_size: int,
    is_symmetric: bool,
    label: str,
) -> bool:
    """
    Quantize FP32 model → N-bit using MatMulNBitsQuantizer.

    Returns True on success.
    """
    print(f"\n{'─' * 60}")
    print(f"  {label} Quantization: accuracy_level={accuracy_level}, "
          f"block_size={block_size}, symmetric={is_symmetric}")
    print(f"{'─' * 60}")

    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Load the FP32 model
    print(f"  Loading FP32 model: {fp32_path.name}")
    model = onnx.load(str(fp32_path))
    opset = model.opset_import[0].version
    print(f"  Source opset: {opset}")

    if opset < 21:
        print(f"  ⚠️  WARNING: Model opset is {opset}. MatMulNBits requires opset ≥ 21.")
        print(f"     The quantizer will upgrade opset automatically, but verify ONNX Runtime compatibility.")
        print(f"     ONNX Runtime ≥ 1.17 required for opset 21 on CPU EP.")

    # Count quantizable nodes before
    matmul_before = sum(1 for n in model.graph.node if n.op_type == "MatMul")

    print(f"  Quantizable MatMul nodes: {matmul_before}")
    print(f"  Quantizing...")

    t_start = time.perf_counter()

    try:
        quantizer = MatMulNBitsQuantizer(
            model=model,
            accuracy_level=accuracy_level,
            block_size=block_size,
            is_symmetric=is_symmetric,
        )

        # Process: replaces MatMul with MatMulNBits, packs weights (in-place)
        quantizer.process()

        elapsed = time.perf_counter() - t_start
        print(f"  Quantization complete in {elapsed:.1f}s")

    except Exception as e:
        print(f"\n  ❌ {label} quantization failed: {e}")
        return False

    # ── Post-quantization stats (model is modified in-place) ──────────
    matmul_nbits = sum(1 for n in model.graph.node
                       if n.op_type == "MatMulNBits")
    matmul_remain = sum(1 for n in model.graph.node
                        if n.op_type == "MatMul")

    print(f"  MatMulNBits nodes created: {matmul_nbits}")
    print(f"  MatMul nodes remaining:    {matmul_remain}")

    # Save with external data for large models
    print(f"  Saving quantized model...")
    onnx.save(
        model,
        str(output_path),
        save_as_external_data=True,
        all_tensors_to_one_file=True,
        location=f"{output_path.stem}.data",
        size_threshold=1024,
        convert_attribute=False,
    )

    # Size report
    onnx_size = output_path.stat().st_size / (1024 * 1024)
    data_path = output_path.with_suffix(".data")
    data_size = data_path.stat().st_size / (1024 * 1024) if data_path.exists() else 0
    total_size = onnx_size + data_size

    fp32_size = fp32_path.stat().st_size / (1024 * 1024)
    reduction = (1 - total_size / fp32_size) * 100

    print(f"\n  📦 {label} Model Size:")
    print(f"     {output_path.name}:  {onnx_size:.1f} MB")
    if data_size > 0:
        print(f"     {data_path.name}:    {data_size:.1f} MB")
    print(f"     Total:               {total_size:.1f} MB")
    print(f"     FP32 baseline:       {fp32_size:.1f} MB")
    print(f"     Reduction:           {reduction:.1f}% ({fp32_size / total_size:.1f}×)")

    return True


def verify_model(model_path: Path, label: str):
    """Run ONNX Runtime inference to verify quantized model works."""
    print(f"\n  🔍 ORT inference test ({label})...")

    import onnxruntime as ort

    try:
        sess = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])

        n_mels = 128
        for dur in [1.0, 3.0, 5.0]:
            t_mel = int(dur / 0.01)
            inp = np.random.randn(1, n_mels, t_mel).astype(np.float32)
            out = sess.run(None, {"processed_signal": inp})

            has_nan = np.any(np.isnan(out[0]))
            has_inf = np.any(np.isinf(out[0]))
            status = "OK" if not (has_nan or has_inf) else f"{'NaN' if has_nan else ''}{'Inf' if has_inf else ''}"
            print(f"     {dur:.1f}s ({t_mel} frames) → {out[0].shape} [{status}]")

        # Provider info
        providers = sess.get_providers()
        print(f"     EP: {', '.join(providers)}")
        return True

    except Exception as e:
        print(f"     ❌ ORT inference failed: {e}")
        return False


def verify_accuracy(fp32_path: Path, int4_path: Path, int8_path: Path):
    """
    Compare FP32 vs INT4 vs INT8 outputs on random inputs.
    Reports cosine similarity and max absolute error.
    """
    print(f"\n{'=' * 60}")
    print(f"Accuracy Comparison: FP32 vs INT4 vs INT8")
    print(f"{'=' * 60}")

    import onnxruntime as ort

    n_mels = 128
    test_durations = [1.0, 3.0, 5.0, 8.0]

    try:
        sess_fp32 = ort.InferenceSession(str(fp32_path), providers=["CPUExecutionProvider"])
    except Exception as e:
        print(f"  Cannot load FP32: {e}")
        return

    sessions = {}
    for label, path in [("INT4", int4_path), ("INT8", int8_path)]:
        try:
            sessions[label] = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        except Exception as e:
            print(f"  Cannot load {label}: {e}")

    for dur in test_durations:
        t_mel = int(dur / 0.01)
        inp = np.random.seed(42) or np.random.randn(1, n_mels, t_mel).astype(np.float32)

        # Run FP32 baseline
        fp32_out = sess_fp32.run(None, {"processed_signal": inp})[0]

        for label, sess in sessions.items():
            try:
                q_out = sess.run(None, {"processed_signal": inp})[0]

                # Cosine similarity
                fp32_flat = fp32_out.flatten()
                q_flat = q_out.flatten()
                cos_sim = np.dot(fp32_flat, q_flat) / (
                    np.linalg.norm(fp32_flat) * np.linalg.norm(q_flat) + 1e-10
                )

                # Max absolute error
                max_err = np.max(np.abs(fp32_out - q_out))

                print(f"  {dur:.1f}s {label}: cos_sim={cos_sim:.4f}, max|Δ|={max_err:.4f}")
            except Exception as e:
                print(f"  {dur:.1f}s {label}: inference failed: {e}")


def main():
    parser = argparse.ArgumentParser(
        description="Quantize Sortformer FP32 → INT4/INT8 via MatMulNBits"
    )
    parser.add_argument("--int4", dest="do_int4", action="store_true",
                        help="Quantize INT4 only")
    parser.add_argument("--int8", dest="do_int8", action="store_true",
                        help="Quantize INT8 only")
    parser.add_argument("--fp32", type=str, default=str(FP32_PATH),
                        help=f"Path to FP32 model (default: {FP32_PATH})")
    args = parser.parse_args()

    # Default: do both if neither specified
    if not args.do_int4 and not args.do_int8:
        args.do_int4 = True
        args.do_int8 = True

    fp32_path = Path(args.fp32)

    if not fp32_path.exists():
        print(f"ERROR: FP32 model not found at {fp32_path}")
        print("Run export_fp32_opset21.py first.")
        sys.exit(1)

    print("=" * 60)
    print("Sortformer MatMulNBits Quantization")
    print("=" * 60)
    print(f"FP32 source: {fp32_path}")

    results = {}

    for bits in [4, 8]:
        if (bits == 4 and not args.do_int4) or (bits == 8 and not args.do_int8):
            continue

        preset = PRESETS[bits]
        output_path = OUTPUT_DIRS[bits] / "sortformer.onnx"

        # Clear old data file if exists
        old_data = output_path.with_suffix(".data")
        if old_data.exists():
            old_data.unlink()

        ok = quantize_model(
            fp32_path=fp32_path,
            output_path=output_path,
            accuracy_level=preset["accuracy_level"],
            block_size=preset["block_size"],
            is_symmetric=preset["is_symmetric"],
            label=preset["label"],
        )

        if ok:
            verify_model(output_path, preset["label"])

        results[preset["label"]] = {"success": ok, "path": output_path}

    # Accuracy comparison if both models succeeded
    if all(r["success"] for r in results.values()):
        verify_accuracy(
            fp32_path,
            results.get("INT4", {}).get("path", None),
            results.get("INT8", {}).get("path", None),
        )

    # Final summary
    print(f"\n{'=' * 60}")
    print(f"Quantization Summary")
    print(f"{'=' * 60}")
    for label, r in results.items():
        status = "✅" if r["success"] else "❌"
        if r["success"]:
            onnx_size = r["path"].stat().st_size / (1024 * 1024)
            data_path = r["path"].with_suffix(".data")
            data_size = data_path.stat().st_size / (1024 * 1024) if data_path.exists() else 0
            total = onnx_size + data_size
            reduction = (1 - total / (fp32_path.stat().st_size / (1024 * 1024))) * 100
            print(f"  {status} {label}: {total:.1f} MB ({reduction:.1f}% smaller)")
        else:
            print(f"  {status} {label}: FAILED")

    if not any(r["success"] for r in results.values()):
        sys.exit(1)


if __name__ == "__main__":
    main()
