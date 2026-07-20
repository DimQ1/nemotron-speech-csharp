"""Export Nemotron Speech to ONNX with GENAI support for Qualcomm QNN.

QNN-specific requirements:
  - Opset 17 (QNN best compatibility; opset 21 may not be fully supported)
  - FP32 export (QNN handles its own INT8 quantization via qnn-context-binary-generator)
  - Static shapes preferred (QNN struggles with dynamic axes)
  - torch.export / dynamo exporter disabled (standard torch.onnx.export is more QNN-friendly)

Output: modules/asr/qnn/ with encoder.onnx, decoder.onnx, joint.onnx,
        genai_config.json, audio_processor_config.json, tokenizer, Silero VAD.

Usage:
    cd converter
    python src/convert_qnn.py
"""

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

# Ensure the recipe root is on sys.path so `from src.nemotron_model_load import ...` works
_SCRIPT_DIR = Path(__file__).resolve().parent
_RECIPE_ROOT = _SCRIPT_DIR.parent
if str(_RECIPE_ROOT) not in sys.path:
    sys.path.insert(0, str(_RECIPE_ROOT))

_TOKENIZER_SCRIPT = _RECIPE_ROOT / "scripts" / "export_tokenizer.py"

# --------------- defaults ---------------
DEFAULT_NEMO = str(_RECIPE_ROOT.parent / "models-original" / "nemotron-3.5-asr-streaming-0.6b.nemo")
DEFAULT_OUTPUT = str(_RECIPE_ROOT.parent / "modules" / "asr" / "qnn")


def _resolve(path: str) -> Path:
    p = Path(path)
    return p if p.is_absolute() else _SCRIPT_DIR / p


# ---------------------------------------------------------------------------
# Stage 1–3: Olive ONNX export (FP32, opset 17, no dynamo)
# ---------------------------------------------------------------------------

def _run_olive_qnn(config_name: str, output_dir: str, output_subdir: str,
                   model_path: str, opset: int = 17):
    """Run an Olive OnnxConversion pipeline with QNN-friendly settings."""
    from olive import run as olive_run

    config_path = _SCRIPT_DIR / config_name
    with open(config_path) as f:
        config = json.load(f)

    config["output_dir"] = str(_resolve(output_dir) / output_subdir)

    # Override model_path to point to the local .nemo file
    config["input_model"]["model_path"] = model_path

    # Fix model_script path to absolute (Olive requires it).
    # Configs use "src/nemotron_model_load.py", but _SCRIPT_DIR is already src/.
    ms = config["input_model"].get("model_script")
    if ms:
        config["input_model"]["model_script"] = str(_SCRIPT_DIR / Path(ms).name)

    # QNN-friendly overrides
    passes = config.get("passes", {})
    if "convert" in passes:
        passes["convert"]["target_opset"] = opset
        passes["convert"]["dynamic"] = False
        passes["convert"]["use_dynamo_exporter"] = False
        passes["convert"]["save_as_external_data"] = True
        # External data name based on output
        ext_name = output_subdir.replace(".onnx", ".onnx.data")
        passes["convert"]["external_data_name"] = ext_name

    # Remove quantization pass — QNN handles its own INT8
    passes.pop("quantization", None)

    # Patch io_config to remove dynamic_axes for QNN
    io_cfg = config["input_model"].get("io_config", {})
    io_cfg.pop("dynamic_axes", None)

    # Use CPU EP for export
    config["systems"]["local_system"]["accelerators"] = [
        {"device": "cpu", "execution_providers": ["CPUExecutionProvider"]}
    ]

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", dir=str(_SCRIPT_DIR), delete=False
    ) as tmp:
        json.dump(config, tmp, indent=4)
        tmp_path = tmp.name

    try:
        olive_run(tmp_path)
    finally:
        Path(tmp_path).unlink(missing_ok=True)


def run_qnn_export(model_path: str, output_dir: str):
    """Export encoder, decoder, joint to ONNX (FP32, opset 17)."""

    print("=" * 70)
    print("Nemotron → ONNX (QNN-ready, FP32, opset 17)")
    print("=" * 70)

    print(f"\n── Stage 1: Encoder (FP32) ──")
    _run_olive_qnn(
        "nemotron_encoder_int4_cpu.json",
        output_dir, "encoder.onnx", model_path,
    )

    print(f"\n── Stage 2: Decoder (FP32) ──")
    _run_olive_qnn(
        "nemotron_decoder_fp32_cpu.json",
        output_dir, "decoder.onnx", model_path,
    )

    print(f"\n── Stage 3: Joint (FP32) ──")
    _run_olive_qnn(
        "nemotron_joint_fp32_cpu.json",
        output_dir, "joint.onnx", model_path,
    )

    # Merge external data for small models only.
    # Encoder >2 GB ⇒ must keep external data (protobuf 2 GB limit).
    print("\n── Merging external data (small models only) ──")
    import onnx
    dst = _resolve(output_dir)
    for onnx_name in ["decoder.onnx", "joint.onnx"]:
        onnx_path = dst / onnx_name
        data_path = dst / (onnx_name + ".data")
        if onnx_path.exists() and data_path.exists():
            model = onnx.load(str(onnx_path))
            onnx.save(model, str(onnx_path), save_as_external_data=False)
            data_path.unlink()
            mb = onnx_path.stat().st_size / (1024 * 1024)
            print(f"  [OK] {onnx_name} ({mb:.0f} MB, self-contained)")
        elif onnx_path.exists():
            mb = onnx_path.stat().st_size / (1024 * 1024)
            print(f"  [OK] {onnx_name} ({mb:.0f} MB)")

    # Encoder: keep external data (too large for protobuf 2 GB limit)
    enc_path = dst / "encoder.onnx"
    enc_data = dst / "encoder.onnx.data"
    if enc_path.exists():
        mb = enc_path.stat().st_size / (1024 * 1024)
        if enc_data.exists():
            data_mb = enc_data.stat().st_size / (1024 * 1024)
            print(f"  [OK] encoder.onnx ({mb:.0f} MB + {data_mb:.0f} MB external data)")
        else:
            print(f"  [OK] encoder.onnx ({mb:.0f} MB)")


# ---------------------------------------------------------------------------
# Stage 4: Tokenizer
# ---------------------------------------------------------------------------

def run_tokenizer_export(model_name: str, output_dir: str):
    print("\n── Stage 4: Exporting tokenizer ──")
    cmd = [
        sys.executable,
        str(_TOKENIZER_SCRIPT),
        "--model_name", model_name,
        "--output_dir", str(_resolve(output_dir)),
    ]
    result = subprocess.run(cmd, cwd=str(_SCRIPT_DIR))
    if result.returncode != 0:
        raise RuntimeError(f"Tokenizer export failed (exit code {result.returncode})")
    print("  [OK] Tokenizer exported")


# ---------------------------------------------------------------------------
# Stage 5: GENAI + audio_processor configs
# ---------------------------------------------------------------------------

def generate_configs(model_name: str, output_dir: str, chunk_size: float):
    """Generate genai_config.json and audio_processor_config.json."""
    print("\n── Stage 5: Generating config files ──")
    from src.nemotron_model_load import (
        _load_nemo_model, get_att_context_size,
        D_MODEL, N_LAYERS, DECODER_HIDDEN, DECODER_LSTM_LAYERS,
    )

    asr_model = _load_nemo_model(model_name)
    asr_model.eval()

    dst = _resolve(output_dir)
    dst.mkdir(parents=True, exist_ok=True)

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

    encoder = asr_model.encoder
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
                "num_hidden_layers": N_LAYERS,
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


# ---------------------------------------------------------------------------
# Stage 6: Silero VAD
# ---------------------------------------------------------------------------

def download_silero_vad(output_dir: str):
    print("\n── Stage 6: Downloading Silero VAD ──")
    from huggingface_hub import hf_hub_download

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
    print(f"  [OK] silero_vad.onnx ({size_mb:.1f} MB)")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    from src.nemotron_model_load import CHUNK_SIZE

    parser = argparse.ArgumentParser(
        description="Export Nemotron Speech to ONNX for Qualcomm QNN"
    )
    parser.add_argument(
        "--model-path",
        default=DEFAULT_NEMO,
        help=f"Path to .nemo file (default: {DEFAULT_NEMO})",
    )
    parser.add_argument(
        "--output-dir",
        default=DEFAULT_OUTPUT,
        help=f"Output directory (default: {DEFAULT_OUTPUT})",
    )
    parser.add_argument(
        "--opset",
        type=int,
        default=17,
        help="ONNX opset version for QNN compatibility (default: 17)",
    )
    args = parser.parse_args()

    model_path = args.model_path
    if not Path(model_path).exists():
        print(f"ERROR: Model file not found: {model_path}")
        print("  Download from HuggingFace or place .nemo in models-original/")
        sys.exit(1)

    # Stages 1-3: Olive ONNX export (FP32, opset 17, no dynamo)
    run_qnn_export(model_path=model_path, output_dir=args.output_dir)

    # Stage 4: Tokenizer
    run_tokenizer_export(model_name=model_path, output_dir=args.output_dir)

    # Stage 5: GENAI + audio configs
    generate_configs(
        model_name=model_path,
        output_dir=args.output_dir,
        chunk_size=CHUNK_SIZE,
    )

    # Stage 6: Silero VAD
    vad_dest = _resolve(args.output_dir) / "silero_vad.onnx"
    try:
        download_silero_vad(output_dir=args.output_dir)
    except Exception as exc:
        print(
            f"  Warning: Silero VAD download failed ({exc}).\n"
            f"  Download manually from https://huggingface.co/onnx-community/silero-vad\n"
            f"  and place silero_vad.onnx at: {vad_dest}"
        )

    # Generate model_config.json
    model_config = {
        "type": "onnxmodel",
        "config": {
            "model_path": str(Path(args.output_dir).resolve()),
            "onnx_file_name": "joint.onnx",
            "inference_settings": None,
            "use_ort_extensions": False,
            "external_initializers_file_name": None,
            "constant_inputs_file_name": None,
            "model_attributes": None,
        },
    }
    model_cfg_path = _resolve(args.output_dir) / "model_config.json"
    with open(model_cfg_path, "w") as f:
        json.dump(model_config, f, indent=4)
    print("  [OK] model_config.json")

    # Summary
    output_path = _resolve(args.output_dir)
    if output_path.exists():
        files = sorted(f for f in output_path.iterdir() if f.is_file())
        total_mb = sum(f.stat().st_size for f in files) / (1024 * 1024)
        print(f"\n{'=' * 70}")
        print(f"  Done! QNN-ready ONNX models → {output_path}")
        print(f"  Total size: {total_mb:.1f} MB")
        print(f"  Opset: {args.opset} | Quantization: FP32 (QNN will quantize)")
        for f in files:
            print(f"    {f.name} ({f.stat().st_size / (1024 * 1024):.1f} MB)")
        print(f"{'=' * 70}")
        print(f"\n  Next: use qnn-onnx-converter to quantize for Snapdragon NPU:")
        print(f"    qnn-onnx-converter -i {output_path}/encoder.onnx -o encoder_qnn.cpp")
        print(f"    qnn-onnx-converter -i {output_path}/decoder.onnx -o decoder_qnn.cpp")
        print(f"    qnn-onnx-converter -i {output_path}/joint.onnx -o joint_qnn.cpp")


if __name__ == "__main__":
    main()
