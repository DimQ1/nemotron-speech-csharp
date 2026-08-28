"""End-to-end optimization pipeline for Nemotron Speech Streaming.

All model components (encoder, decoder, joint) are exported and optimized
using Olive's declarative pass system:

  - Encoder: OnnxConversion → OnnxKQuantQuantization
    - Decoder: OnnxConversion (FP32), or OnnxConversion → OnnxFloatToFloat16
    - Joint:   OnnxConversion (FP32), or OnnxConversion → OnnxFloatToFloat16

After the Olive pipelines, tokenizer and config files are generated and
Silero VAD is downloaded.

Usage:
    # Full pipeline
    python src/optimize.py

    # Or use Olive CLI directly for individual components:
    python -m olive run --config src/nemotron_encoder_int4_cpu.json
    python -m olive run --config src/nemotron_decoder_fp32_cpu.json
    python -m olive run --config src/nemotron_joint_fp32_cpu.json

    # TensorRT RTX FP16 I/O artifact
    python src/optimize.py --encoder-precision fp16 --execution-provider cuda
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

# Patch Olive to use torch instead of cupy for GPU k-quant (needed for sm_120/RTX 5070)
try:
    _PATCH_DIR = Path(__file__).resolve().parent
    _PATCH_FILE = _PATCH_DIR.parent / "patch_olive_torch.py"
    if _PATCH_FILE.exists():
        sys.path.insert(0, str(_PATCH_FILE.parent))
        import patch_olive_torch
        patch_olive_torch.patch_olive()
except Exception:
    pass

# Patch Olive OnnxConversion for torch >= 2.12 (torch.onnx.export dropped the
# 'fallback' kwarg that Olive 0.8 still passes with dynamo=True).
_SCRIPT_DIR_EARLY = Path(__file__).resolve().parent
try:
    if str(_SCRIPT_DIR_EARLY) not in sys.path:
        sys.path.insert(0, str(_SCRIPT_DIR_EARLY))
    from src import patch_olive_torch_export
    patch_olive_torch_export.patch_olive_torch_export()
except Exception:
    pass

# Ensure the recipe root is on sys.path so `from src.nemotron_model_load import ...` works
# regardless of where the script is invoked from.
_SCRIPT_DIR = Path(__file__).resolve().parent
_RECIPE_ROOT = _SCRIPT_DIR.parent
if str(_RECIPE_ROOT) not in sys.path:
    sys.path.insert(0, str(_RECIPE_ROOT))

_TOKENIZER_SCRIPT = _RECIPE_ROOT / "scripts" / "export_tokenizer.py"

DEFAULT_OUTPUT_DIR = "build/onnx_models_int4"
DEFAULT_FP16_OUTPUT_DIR = "build/onnx_models_fp16_cuda"

# Local source checkpoint (preferred over re-downloading the 2.2 GB .nemo).
_PROJECT_ROOT = _RECIPE_ROOT.parents[2]
_LOCAL_NEMO = (
    _PROJECT_ROOT / "models" / "asr" / "nemotron-3.5" / "source"
    / "nemotron-3.5-asr-streaming-0.6b.nemo"
)


def _default_model_name() -> str:
    """Return the local .nemo checkpoint if present, else the HF repo id."""
    if _LOCAL_NEMO.is_file():
        return str(_LOCAL_NEMO)
    from src.nemotron_model_load import MODEL_NAME

    return MODEL_NAME


def _resolve(path: str) -> Path:
    """Resolve a path relative to the src/ directory."""
    p = Path(path)
    return p if p.is_absolute() else _SCRIPT_DIR / p


def _to_new_run_format(config: dict) -> dict:
    """Convert a legacy Olive config to the installed RunConfig schema.

    Legacy configs in this recipe use the pre-0.7 layout:
      input_model / systems / passes{NAME:{type, ...}} / target / output_dir / no_artifacts

        Current Olive expects:
            input_model / systems / passes{NAME:[{type, config:{...}}]} /
            engine{target, output_dir, no_artifacts, ...}

    Pass-specific top-level keys (everything except 'type') move under 'config'.
    """
    new: dict = {}

    if "input_model" in config:
        new["input_model"] = config["input_model"]
    if "systems" in config:
        new["systems"] = config["systems"]

    # engine: target / output_dir (+ packaging to drop artifacts)
    engine: dict = {}
    if "target" in config:
        engine["target"] = config["target"]
    if "output_dir" in config:
        engine["output_dir"] = config["output_dir"]
    if "no_artifacts" in config:
        engine["no_artifacts"] = config["no_artifacts"]
    new["engine"] = engine

    # passes: split 'type' from the rest of the pass params
    legacy_passes = config.get("passes", {})
    new_passes: dict = {}
    for name, p in legacy_passes.items():
        p = dict(p)
        ptype = p.pop("type", None)
        host = p.pop("host", None)
        entry: dict = {"type": ptype, "config": p}
        if host is not None:
            entry["host"] = host
        new_passes[name] = [entry]
    new["passes"] = new_passes

    # Unique workflow id keyed by chunk size, opset, left context, and pass
    # flow so that Olive does not reuse cached artifacts across different
    # export pipelines (e.g. a left_context change must re-export the encoder).
    if "workflow_id" not in new:
        try:
            from src import nemotron_model_load as _nml
            _cs = str(_nml._chunk_size()).replace(".", "p")
            _lc = str(getattr(_nml, "LEFT_CONTEXT", "default"))
        except Exception:
            _cs = "default"
            _lc = "default"
        _op = ""
        for p in legacy_passes.values():
            if p.get("type") == "OnnxConversion":
                _op = f"_ops{p.get('target_opset', '')}"
                break
        _flow = "-".join(
            str(p.get("type", "unknown")).lower()
            for p in legacy_passes.values()
        )
        new["workflow_id"] = f"nemotron_{_cs}{_op}_lc{_lc}_{_flow}"

    return new


def _run_olive_pipeline(config_name: str, output_dir: str, output_subdir: str, model_path: str = None, target_opset: int = None):
    """Run an Olive pipeline from a JSON config, overriding output_dir."""
    # Olive >= 0.7 moved the programmatic entry point to olive.workflows.run
    try:
        from olive.workflows import run as olive_run
    except ImportError:  # older Olive exposes olive.run at the top level
        from olive import run as olive_run

    config_path = _SCRIPT_DIR / config_name
    with open(config_path) as f:
        config = json.load(f)

    model_script = config.get("input_model", {}).get("model_script")
    if model_script:
        model_script_path = Path(model_script)
        if not model_script_path.is_absolute():
            candidates = (_RECIPE_ROOT / model_script_path, _SCRIPT_DIR / model_script_path)
            model_script_path = next(
                (candidate for candidate in candidates if candidate.is_file()),
                model_script_path,
            )
        config["input_model"]["model_script"] = str(model_script_path.resolve())

    output_dir_abs = str(_resolve(output_dir) / output_subdir)
    if model_path is not None:
        config["input_model"]["model_path"] = model_path
    if target_opset is not None:
        # Override the opset in every OnnxConversion pass.
        for p in config.get("passes", {}).values():
            if p.get("type") == "OnnxConversion":
                p["target_opset"] = target_opset

    # If a previous flatten() already replaced the component dir with a file,
    # remove it so Olive can recreate the output directory.
    if os.path.isfile(output_dir_abs):
        os.remove(output_dir_abs)

    # Detect the config schema version and adapt.
    is_new_format = "engine" in config or "pass_flows" in config
    if is_new_format:
        run_config = config
        run_config.setdefault("engine", {})["output_dir"] = output_dir_abs
    else:
        config["output_dir"] = output_dir_abs
        run_config = _to_new_run_format(config)

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", dir=str(_SCRIPT_DIR), delete=False
    ) as tmp:
        json.dump(run_config, tmp, indent=4)
        tmp_path = tmp.name

    try:
        olive_run(tmp_path)
    finally:
        Path(tmp_path).unlink(missing_ok=True)


def run_olive_pipelines(output_dir: str, model_path: str = None, encoder_precision: str = "int4", execution_provider: str = "cpu", target_opset: int = None):
    """Run all Olive pipelines for the selected encoder precision.

    Args:
        output_dir: Output directory for optimized models.
        model_path: Path to the .nemo model file or HF repo name.
        encoder_precision: "fp32", "int4", "int8", or "fp16". FP16 is CUDA-only.
        execution_provider: "cpu", "dml", or "cuda".
        target_opset: ONNX target opset (e.g. 21 or 24). None keeps the config default.
    """
    if encoder_precision == "fp16" and execution_provider != "cuda":
        raise ValueError("FP16 conversion is only supported with --execution-provider cuda.")

    ep_suffix = {"cpu": "_cpu", "dml": "_dml", "cuda": "_cuda"}.get(execution_provider, "_cpu")
    ep_label = {"cpu": "CPU", "dml": "DML", "cuda": "CUDA"}.get(execution_provider, "CPU")

    if encoder_precision == "fp16":
        encoder_config = "nemotron_encoder_fp16_cuda.json"
        print(f"=== Stage 1: Olive Encoder ({ep_label}, OnnxConversion -> FP16 I/O) ===")
    elif encoder_precision == "int8":
        encoder_config = f"nemotron_encoder_int8{ep_suffix}.json"
        print(f"=== Stage 1: Olive Encoder ({ep_label}, OnnxConversion -> INT8 k-quant) ===")
    elif encoder_precision == "fp32":
        encoder_config = f"nemotron_encoder_fp32{ep_suffix}.json"
        print(f"=== Stage 1: Olive Encoder ({ep_label}, OnnxConversion, FP32) ===")
    else:
        encoder_config = f"nemotron_encoder_int4{ep_suffix}.json"
        print(f"=== Stage 1: Olive Encoder ({ep_label}, OnnxConversion -> INT4 quant) ===")
    _run_olive_pipeline(encoder_config, output_dir, "encoder.onnx", model_path, target_opset)
    print()

    if encoder_precision == "fp16":
        decoder_config = "nemotron_decoder_fp16_cuda.json"
        decoder_label = "FP16 I/O"
    else:
        decoder_config = f"nemotron_decoder_fp32{ep_suffix}.json"
        decoder_label = "FP32"
    print(f"=== Stage 2: Olive Decoder ({ep_label}, OnnxConversion -> {decoder_label}) ===")
    _run_olive_pipeline(decoder_config, output_dir, "decoder.onnx", model_path, target_opset)
    print()

    if encoder_precision == "fp16":
        joint_config = "nemotron_joint_fp16_cuda.json"
        joint_label = "FP16 I/O"
    else:
        joint_config = f"nemotron_joint_fp32{ep_suffix}.json"
        joint_label = "FP32"
    print(f"=== Stage 3: Olive Joint ({ep_label}, OnnxConversion -> {joint_label}) ===")
    _run_olive_pipeline(joint_config, output_dir, "joint.onnx", model_path, target_opset)
    print()

    from src.flatten_output import flatten

    flatten(_resolve(output_dir))


def validate_fp16_io(output_dir: str):
    """Require every floating-point model input/output to use FLOAT16."""
    import onnx
    from onnx import TensorProto

    floating_types = {
        TensorProto.FLOAT,
        TensorProto.FLOAT16,
        TensorProto.DOUBLE,
        TensorProto.BFLOAT16,
    }
    model_dir = _resolve(output_dir)
    for component in ("encoder", "decoder", "joint"):
        model_path = model_dir / f"{component}.onnx"
        if not model_path.is_file():
            raise FileNotFoundError(f"FP16 artifact is missing: {model_path}")

        model = onnx.load(str(model_path), load_external_data=False)
        floating_io = []
        for value_info in (*model.graph.input, *model.graph.output):
            tensor_type = value_info.type.tensor_type
            if tensor_type.elem_type in floating_types:
                floating_io.append((value_info.name, tensor_type.elem_type))

        mismatches = [name for name, elem_type in floating_io if elem_type != TensorProto.FLOAT16]
        if not floating_io:
            raise RuntimeError(f"{component}.onnx has no floating-point I/O to validate")
        if mismatches:
            names = ", ".join(mismatches)
            raise RuntimeError(f"{component}.onnx has non-FP16 floating I/O: {names}")

        print(f"  [OK] {component}.onnx floating I/O is FLOAT16")


def run_tokenizer_export(model_name: str, output_dir: str):
    """Export tokenizer files to the output directory."""
    print("=== Stage 4: Exporting tokenizer ===")
    cmd = [
        sys.executable,
        str(_TOKENIZER_SCRIPT),
        "--model_name", model_name,
        "--output_dir", str(_resolve(output_dir)),
    ]
    result = subprocess.run(cmd, cwd=str(_SCRIPT_DIR))
    if result.returncode != 0:
        raise RuntimeError(f"Tokenizer export failed (exit code {result.returncode})")
    print()


def generate_configs(model_name: str, output_dir: str, chunk_size: float):
    """Generate genai_config.json and audio_processor_config.json.

    Loads the NeMo model to extract architecture parameters, then writes
    the config files needed by onnxruntime-genai for inference.
    """
    print("=== Stage 5: Generating config files ===")
    from src.nemotron_model_load import _load_nemo_model, get_att_context_size, D_MODEL, N_LAYERS, DECODER_HIDDEN, DECODER_LSTM_LAYERS

    asr_model = _load_nemo_model(model_name)
    asr_model.eval()

    dst = _resolve(output_dir)
    dst.mkdir(parents=True, exist_ok=True)

    encoder = asr_model.encoder
    joint = asr_model.joint

    vocab_size = joint.num_classes_with_blank
    blank_id = vocab_size - 1

    preprocessor_cfg = asr_model.cfg.get('preprocessor', {})
    sample_rate = preprocessor_cfg.get('sample_rate', 16000)
    n_mels = preprocessor_cfg.get('features', preprocessor_cfg.get('nfilt', 128))
    n_fft = preprocessor_cfg.get('n_fft', 512)
    hop_length = preprocessor_cfg.get('hop_length', 160)
    win_length = preprocessor_cfg.get('win_length', 400)
    preemph = preprocessor_cfg.get('preemph', 0.97)

    subsampling_factor = getattr(encoder, 'subsampling_factor', 8)
    att_context_size = get_att_context_size(chunk_size)
    left_context = att_context_size[0]

    conv_context = 8
    if hasattr(encoder, 'layers') and len(encoder.layers) > 0:
        layer = encoder.layers[0]
        if hasattr(layer, 'conv') and hasattr(layer.conv, 'conv'):
            conv = layer.conv.conv
            if hasattr(conv, 'kernel_size'):
                ks = conv.kernel_size[0] if isinstance(conv.kernel_size, tuple) else conv.kernel_size
                conv_context = ks - 1

    pre_encode_cache_size = getattr(encoder, 'pre_encode_cache_size', 9)
    if isinstance(pre_encode_cache_size, (list, tuple)):
        pre_encode_cache_size = pre_encode_cache_size[-1]

    chunk_samples = int(chunk_size * sample_rate)
    max_symbols = asr_model.cfg.get('decoding', {}).get('greedy', {}).get('max_symbols', 10)

    genai_config = {
        "model": {
            "type": "nemotron_speech",
            "vocab_size": vocab_size,
            "num_mels": n_mels,
            "fft_size": n_fft,
            "hop_length": hop_length,
            "win_length": win_length,
            "preemph": preemph,
            "log_eps": 5.96046448e-08,
            "subsampling_factor": subsampling_factor,
            "left_context": left_context,
            "conv_context": conv_context,
            "pre_encode_cache_size": pre_encode_cache_size,
            "sample_rate": sample_rate,
            "chunk_samples": chunk_samples,
            "blank_id": blank_id,
            "max_symbols_per_step": max_symbols,
            "encoder": {
                "filename": "encoder.onnx",
                "hidden_size": D_MODEL,
                "num_hidden_layers": len(encoder.layers) if hasattr(encoder, 'layers') else N_LAYERS,
                "inputs": {
                    "audio_features": "audio_signal",
                    "input_lengths": "length",
                    "cache_last_channel": "cache_last_channel",
                    "cache_last_time": "cache_last_time",
                    "cache_last_channel_len": "cache_last_channel_len",
                    "lang_id": "lang_id",
                },
                "outputs": {
                    "encoder_outputs": "outputs",
                    "output_lengths": "encoded_lengths",
                    "cache_last_channel_next": "cache_last_channel_next",
                    "cache_last_time_next": "cache_last_time_next",
                    "cache_last_channel_len_next": "cache_last_channel_len_next",
                },
            },
            "decoder": {
                "filename": "decoder.onnx",
                "hidden_size": DECODER_HIDDEN,
                "num_hidden_layers": DECODER_LSTM_LAYERS,
                "inputs": {
                    "targets": "targets",
                    "lstm_hidden_state": "h_in",
                    "lstm_cell_state": "c_in",
                },
                "outputs": {
                    "outputs": "decoder_output",
                    "lstm_hidden_state": "h_out",
                    "lstm_cell_state": "c_out",
                },
            },
            "joiner": {
                "filename": "joint.onnx",
                "inputs": {
                    "encoder_outputs": "encoder_output",
                    "decoder_outputs": "decoder_output",
                },
                "outputs": {
                    "logits": "joint_output",
                },
            },
            "vad": {
                "filename": "silero_vad.onnx",
                "threshold": 0.3,
                "silence_duration_ms": 3360,
                "prefix_padding_ms": 560,
            },
        },
    }

    with open(dst / "genai_config.json", "w") as f:
        json.dump(genai_config, f, indent=2)
    print(f"  [OK] genai_config.json")

    # Audio processor config
    window_size = preprocessor_cfg.get('window_size', preprocessor_cfg.get('n_window_size', 0.025))
    window_stride = preprocessor_cfg.get('window_stride', preprocessor_cfg.get('n_window_stride', 0.01))
    if isinstance(window_size, float) and window_size < 1.0:
        window_length_samples = int(window_size * sample_rate)
    elif isinstance(window_size, int):
        window_length_samples = window_size
    else:
        window_length_samples = 400
    if isinstance(window_stride, float) and window_stride < 1.0:
        hop_length_samples = int(window_stride * sample_rate)
    elif isinstance(window_stride, int):
        hop_length_samples = window_stride
    else:
        hop_length_samples = 160

    audio_config = {
        "model_type": "speech_features",
        "audio_params": {
            "sample_rate": sample_rate,
            "n_fft": n_fft,
            "hop_length": hop_length_samples,
            "n_mels": n_mels,
            "window_length": window_length_samples,
            "window_type": "hann",
            "fmin": 0,
            "fmax": sample_rate // 2,
            "dither": preprocessor_cfg.get('dither', 0.0),
            "preemphasis": preemph,
            "log_zero_guard_type": "add",
            "log_zero_guard_value": 1e-10,
            "normalize": preprocessor_cfg.get('normalize', 'none'),
            "center": True,
            "mag_power": 2.0,
        },
    }

    with open(dst / "audio_processor_config.json", "w") as f:
        json.dump(audio_config, f, indent=2)
    print(f"  [OK] audio_processor_config.json")
    print()


def download_silero_vad(output_dir: str):
    """Download the Silero VAD ONNX model from onnx-community/silero-vad."""
    from huggingface_hub import hf_hub_download

    print("=== Stage 6: Downloading Silero VAD ===")
    dst_dir = _resolve(output_dir)
    dst_dir.mkdir(parents=True, exist_ok=True)
    dst = dst_dir / "silero_vad.onnx"

    cached = hf_hub_download(
        repo_id="onnx-community/silero-vad",
        filename="onnx/model.onnx",
        revision="e71cae966052b992a7eca6b17738916ce0eca4ec",
    )
    shutil.copy2(cached, str(dst))
    size_mb = dst.stat().st_size / (1024 * 1024)
    print(f"  Saved Silero VAD model to: {dst} ({size_mb:.1f} MB)")
    print()


def main():
    from src.nemotron_model_load import MODEL_NAME
    from src import nemotron_model_load

    parser = argparse.ArgumentParser(
        description="Optimize Nemotron Speech Streaming for CPU, CUDA, or DirectML inference"
    )
    parser.add_argument(
        "--model-name",
        default=_default_model_name(),
        help="HuggingFace model name or path to a local .nemo file. "
             "Defaults to the local source checkpoint if present, else the HF repo.",
    )
    parser.add_argument(
        "--output-dir",
        default=None,
        help="Output directory for optimized models (default: precision-specific build directory)",
    )
    parser.add_argument(
        "--encoder-precision",
        choices=["int4", "int8", "fp32", "fp16"],
        default="int4",
        help="Encoder precision: fp32, int4/int8 k-quant, or fp16 homogeneous I/O. Default: int4.",
    )
    parser.add_argument(
        "--execution-provider",
        choices=["cpu", "dml", "cuda"],
        default="cpu",
        help="Execution provider: cpu, dml (DirectML), or cuda. Default: cpu.",
    )
    parser.add_argument(
        "--target-opset",
        type=int,
        default=None,
        help="ONNX target opset for all OnnxConversion passes (e.g. 21 or 24). "
             "Default keeps the value from the JSON config (21). NOTE: opset 24 "
             "requires the dynamo exporter for every component; the legacy "
             "TorchScript exporter used by decoder/joint cannot target opset 24 "
             "in the current torch build.",
    )
    parser.add_argument(
        "--chunk-size",
        type=float,
        choices=[0.08, 0.16, 0.56, 1.12],
        default=1.12,
        help="Streaming chunk size in seconds (0.08/0.16/0.56/1.12). Larger windows "
             "give more encoder context (better WER) at higher latency. Default: "
             "1.12 (max accuracy).",
    )
    args = parser.parse_args()

    # Apply chunk size BEFORE any model_load import reads the constant.
    # nemotron_model_load reads NEMOTRON_CHUNK_SIZE at import time, and Olive
    # imports the script fresh (by path) inside each conversion pass, so the
    # env var is the single reliable way to propagate the value everywhere.
    os.environ["NEMOTRON_CHUNK_SIZE"] = str(args.chunk_size)

    # Validate model name — the Olive configs and model_load.py constants are
    # specific to the 0.6B model architecture.
    if not args.model_name.endswith(".nemo") and args.model_name != MODEL_NAME:
        raise ValueError(
            f"This recipe only supports '{MODEL_NAME}' (or a .nemo file with the same architecture). "
            f"Got: '{args.model_name}'"
        )

    output_dir = args.output_dir
    if output_dir is None:
        output_dir = DEFAULT_FP16_OUTPUT_DIR if args.encoder_precision == "fp16" else DEFAULT_OUTPUT_DIR

    # Stages 1-3: Run Olive pipelines for encoder, decoder, joint
    run_olive_pipelines(
        output_dir=output_dir,
        model_path=args.model_name,
        encoder_precision=args.encoder_precision,
        execution_provider=args.execution_provider,
        target_opset=args.target_opset,
    )

    # Stage 4: Export tokenizer
    run_tokenizer_export(model_name=args.model_name, output_dir=output_dir)

    # Stage 5: Generate config files (chunk_size matches the export shapes).
    # Read the effective value lazily so that --chunk-size / NEMOTRON_CHUNK_SIZE
    # (applied before model load) is honored.
    generate_configs(
        model_name=args.model_name,
        output_dir=output_dir,
        chunk_size=nemotron_model_load._chunk_size(),
    )

    # Stage 6: Download Silero VAD
    if args.encoder_precision == "fp16":
        validate_fp16_io(output_dir)

    vad_dest = _resolve(output_dir) / "silero_vad.onnx"
    try:
        download_silero_vad(output_dir=output_dir)
    except Exception as exc:
        print(
            f"  Warning: Silero VAD download failed ({exc}).\n"
            f"  Download manually from https://huggingface.co/onnx-community/silero-vad\n"
            f"  and place silero_vad.onnx at: {vad_dest}"
        )

    # Summary
    output_path = _resolve(output_dir)
    if output_path.exists():
        files = sorted(f for f in output_path.iterdir() if f.is_file())
        total_mb = sum(f.stat().st_size for f in files) / (1024 * 1024)
        print(f"=== Done! Optimized models -> {output_path} ===")
        print(f"    Total size: {total_mb:.1f} MB")
        enc_label = {
            "fp32": "",
            "fp16": "FP16 homogeneous I/O (Olive)",
            "int4": "INT4 k-quant (Olive)",
            "int8": "INT8 k-quant (Olive)",
        }.get(args.encoder_precision, "")
        ep_label = {"cpu": "CPU", "dml": "DML", "cuda": "CUDA"}.get(args.execution_provider, "CPU")
        for f in files:
            tag = f" ← {enc_label}" if f.name.startswith("encoder") and enc_label else ""
            print(f"    {f.name} ({f.stat().st_size / (1024 * 1024):.1f} MB){tag}")


if __name__ == "__main__":
    main()
