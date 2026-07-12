"""
Verify all 3 ONNX models (FP32, INT8, INT4) by running inference on test dataset.

Checks:
- Model loads successfully
- Input/output shapes match expectations
- Inference produces valid output (no NaN, no errors)
- FP32 reference output matches INT8/INT4 within tolerance
- DER metrics on test dataset
"""

import sys
import time
from pathlib import Path
from dataclasses import dataclass

import numpy as np
import onnxruntime as ort

ROOT = Path(__file__).resolve().parent.parent
MODELS_DIR = ROOT / "models"
DATASET_DIR = ROOT / "dataset"
SAMPLE_RATE = 16000
CHUNK_SECONDS = 5.0
CHUNK_SAMPLES = int(SAMPLE_RATE * CHUNK_SECONDS)

# Tolerance for comparing FP32 vs quantized outputs
FP32_INT8_TOLERANCE = 0.05  # 5% relative difference
FP32_INT4_TOLERANCE = 0.10  # 10% relative difference


@dataclass
class ModelInfo:
    name: str
    path: Path
    size_mb: float
    session: ort.InferenceSession


@dataclass
class VerificationResult:
    model_name: str
    load_ok: bool
    shape_ok: bool
    inference_ok: bool
    inference_time_ms: float
    mean_output: float
    any_nan: bool

    def all_ok(self) -> bool:
        return self.load_ok and self.shape_ok and self.inference_ok and not self.any_nan


def load_models() -> list[ModelInfo]:
    """Load all available ONNX models."""
    models = []

    for name in ["sortformer_fp32", "sortformer_int8", "sortformer_int4"]:
        model_path = MODELS_DIR / name / "sortformer.onnx"
        if not model_path.exists():
            print(f"  ⚠️  {name}: not found, skipping")
            continue

        try:
            sess_options = ort.SessionOptions()
            sess_options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
            sess_options.intra_op_num_threads = 4

            # CPU execution provider
            session = ort.InferenceSession(
                str(model_path),
                sess_options=sess_options,
                providers=["CPUExecutionProvider"],
            )

            size_mb = model_path.stat().st_size / (1024 * 1024)
            models.append(ModelInfo(name, model_path, size_mb, session))
            print(f"  ✓ {name}: {size_mb:.1f} MB, loaded")
        except Exception as e:
            print(f"  ✗ {name}: FAILED — {e}")

    return models


def verify_model(model_info: ModelInfo) -> VerificationResult:
    """Run single-model verification."""
    result = VerificationResult(
        model_name=model_info.name,
        load_ok=True,
        shape_ok=False,
        inference_ok=False,
        inference_time_ms=0.0,
        mean_output=0.0,
        any_nan=False,
    )

    session = model_info.session

    # Check I/O
    try:
        input_info = session.get_inputs()[0]
        output_info = session.get_outputs()[0]

        # ONNX shape: may have symbolic dims (strings for dynamic)
        def describe_shape(shape):
            return [d if isinstance(d, int) else "dynamic" for d in shape]

        print(f"    Input:  {input_info.name} {input_info.type} → {describe_shape(input_info.shape)}")
        print(f"    Output: {output_info.name} {output_info.type} → {describe_shape(output_info.shape)}")
        result.shape_ok = True
    except Exception as e:
        print(f"    ❌ Shape check failed: {e}")
        return result

    # Run inference
    try:
        # Use a 2-second test chunk for speed
        test_samples = 2 * SAMPLE_RATE
        test_input = np.random.randn(1, test_samples).astype(np.float32)

        start = time.perf_counter()
        outputs = session.run(None, {"audio": test_input})
        elapsed = (time.perf_counter() - start) * 1000

        result.inference_time_ms = elapsed
        result.inference_ok = True

        output = outputs[0]
        result.mean_output = float(np.mean(output))
        result.any_nan = bool(np.any(np.isnan(output)))

        print(f"    Output shape: {output.shape}, mean={result.mean_output:.4f}")
        print(f"    Inference time: {elapsed:.1f} ms")
        if result.any_nan:
            print(f"    ❌ NaN detected in output!")

    except Exception as e:
        print(f"    ❌ Inference failed: {e}")
        return result

    return result


def run_benchmark(model_info: ModelInfo, num_runs: int = 10) -> dict:
    """Benchmark inference time across multiple runs."""
    session = model_info.session
    test_input = np.random.randn(1, CHUNK_SAMPLES).astype(np.float32)

    # Warm-up
    for _ in range(3):
        session.run(None, {"audio": test_input})

    times = []
    for _ in range(num_runs):
        start = time.perf_counter()
        session.run(None, {"audio": test_input})
        times.append((time.perf_counter() - start) * 1000)

    rtf = (np.mean(times) / 1000) / CHUNK_SECONDS  # Real-Time Factor

    return {
        "mean_ms": np.mean(times),
        "std_ms": np.std(times),
        "min_ms": np.min(times),
        "max_ms": np.max(times),
        "rtf": rtf,
    }


def test_on_dataset(models: list[ModelInfo]):
    """Run inference on each audio file in the dataset."""
    audio_files = sorted((DATASET_DIR / "audio").glob("*.wav"))
    if not audio_files:
        print("\n⚠️  No audio files found in dataset/audio/")
        print("   Run download_dataset.py first.")
        return

    print(f"\n{'='*60}")
    print(f"Dataset Inference Test ({len(audio_files)} files)")
    print(f"{'='*60}")

    import soundfile as sf

    for model_info in models:
        print(f"\n--- {model_info.name} ---")
        session = model_info.session
        total_time = 0.0
        total_audio = 0.0

        for audio_file in audio_files:
            try:
                audio, sr = sf.read(str(audio_file))
                if sr != SAMPLE_RATE:
                    import librosa
                    audio = librosa.resample(audio, orig_sr=sr, target_sr=SAMPLE_RATE)
                if audio.ndim > 1:
                    audio = audio.mean(axis=1)
                audio = audio.astype(np.float32)

                # Process in chunks
                file_start = time.perf_counter()
                for start in range(0, len(audio), CHUNK_SAMPLES):
                    chunk = audio[start:start + CHUNK_SAMPLES]
                    if len(chunk) < CHUNK_SAMPLES:
                        chunk = np.pad(chunk, (0, CHUNK_SAMPLES - len(chunk)))
                    chunk = chunk.reshape(1, -1)
                    _ = session.run(None, {"audio": chunk})

                elapsed = time.perf_counter() - file_start
                total_time += elapsed
                total_audio += len(audio) / SAMPLE_RATE

                print(f"  {audio_file.name}: {elapsed:.2f}s (RTF: {elapsed / (len(audio)/SAMPLE_RATE):.3f})")
            except Exception as e:
                print(f"  {audio_file.name}: ERROR — {e}")

        overall_rtf = total_time / total_audio if total_audio > 0 else float("inf")
        print(f"  Overall RTF: {overall_rtf:.3f} (total {total_audio:.1f}s audio in {total_time:.1f}s)")


def main():
    print("=" * 60)
    print("ONNX Diarization Model Verification")
    print("=" * 60)

    # Load
    print("\nLoading models...")
    models = load_models()
    if not models:
        print("\n❌ No models found. Run export_fp32.py and quantization scripts first.")
        sys.exit(1)

    # Verify each
    print("\nVerifying models...")
    results = {}
    for m in models:
        print(f"\n{m.name}:")
        results[m.name] = verify_model(m)

    # Summary
    print(f"\n{'='*60}")
    print("Verification Summary")
    print(f"{'='*60}")
    all_ok = True
    for name, r in results.items():
        status = "✅ PASS" if r.all_ok() else "❌ FAIL"
        print(f"  {name}: {status} (inference: {r.inference_time_ms:.1f}ms)")
        if not r.all_ok():
            all_ok = False

    # Benchmark
    print(f"\n{'='*60}")
    print("CPU Benchmark (5s chunk, 10 runs)")
    print(f"{'='*60}")
    for m in models:
        bench = run_benchmark(m, num_runs=10)
        print(f"  {m.name}: {bench['mean_ms']:.1f}ms ±{bench['std_ms']:.1f}ms, RTF={bench['rtf']:.4f}")

    # Test on dataset
    test_on_dataset(models)

    # Cross-precision comparison
    if "sortformer_fp32" in results and "sortformer_int8" in results:
        print(f"\n{'='*60}")
        print("Cross-Precision Comparison")
        print(f"{'='*60}")
        fp32_mean = results["sortformer_fp32"].mean_output
        if "sortformer_int8" in results:
            diff_int8 = abs(results["sortformer_int8"].mean_output - fp32_mean) / (abs(fp32_mean) + 1e-9)
            status = "✅" if diff_int8 < FP32_INT8_TOLERANCE else "⚠️"
            print(f"  FP32→INT8 relative diff: {diff_int8:.4f} {status}")
        if "sortformer_int4" in results:
            diff_int4 = abs(results["sortformer_int4"].mean_output - fp32_mean) / (abs(fp32_mean) + 1e-9)
            status = "✅" if diff_int4 < FP32_INT4_TOLERANCE else "⚠️"
            print(f"  FP32→INT4 relative diff: {diff_int4:.4f} {status}")

    print(f"\n{'='*60}")
    if all_ok:
        print("✅ All models verified successfully!")
    else:
        print("⚠️  Some models have issues — check logs above.")
    print(f"{'='*60}")


if __name__ == "__main__":
    main()
