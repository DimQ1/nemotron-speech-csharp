"""Russian-only CPU int4 optimization pipeline for Nemotron Speech Streaming.

Exports encoder (int4 k-quant), decoder (fp32), joint (fp32) with Russian
language prompt (lang_id=11) baked into the encoder graph.

Usage:
    python src/optimize_ru.py
    python src/optimize_ru.py --output_dir build/onnx_models_cpu_ru
"""

import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

# Patch Olive to use torch instead of cupy for GPU k-quant
try:
    _PATCH_DIR = Path(__file__).resolve().parent
    _PATCH_FILE = _PATCH_DIR.parent / "patch_olive_torch.py"
    if _PATCH_FILE.exists():
        sys.path.insert(0, str(_PATCH_FILE.parent))
        import patch_olive_torch
        patch_olive_torch.patch_olive()
        print("[PATCH] Olive k-quant patched to use torch (no CUDA required)")
except Exception as e:
    print(f"[WARN] Could not patch Olive: {e}")

_SCRIPT_DIR = Path(__file__).resolve().parent
_RECIPE_ROOT = _SCRIPT_DIR.parent
if str(_RECIPE_ROOT) not in sys.path:
    sys.path.insert(0, str(_RECIPE_ROOT))

_TOKENIZER_SCRIPT = _RECIPE_ROOT / "scripts" / "export_tokenizer.py"
MODEL_NAME = "nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b"
RU_LANG_ID = 11


def _resolve(path: str) -> Path:
    p = Path(path)
    return p if p.is_absolute() else _SCRIPT_DIR / p


def _run_olive(config_name: str, output_dir: str, output_subdir: str):
    """Run an Olive pipeline, overriding output_dir."""
    from olive import run as olive_run

    config_path = _SCRIPT_DIR / config_name
    with open(config_path) as f:
        config = json.load(f)

    config["output_dir"] = str(_resolve(output_dir) / output_subdir)

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", dir=str(_SCRIPT_DIR), delete=False
    ) as tmp:
        json.dump(config, tmp, indent=4)
        tmp_path = tmp.name

    try:
        olive_run(tmp_path)
    finally:
        Path(tmp_path).unlink(missing_ok=True)


def run_pipelines(output_dir: str):
    """Run all Olive pipelines for Russian-only CPU int4."""
    print("=== Stage 1: Encoder (CPU, INT4 k-quant, Russian-only) ===")
    _run_olive("nemotron_encoder_int4_cpu_ru.json", output_dir, "encoder.onnx")
    print()

    print("=== Stage 2: Decoder (CPU, FP32) ===")
    _run_olive("nemotron_decoder_fp32_cpu.json", output_dir, "decoder.onnx")
    print()

    print("=== Stage 3: Joint (CPU, FP32) ===")
    _run_olive("nemotron_joint_fp32_cpu.json", output_dir, "joint.onnx")
    print()


def run_tokenizer(output_dir: str):
    """Export tokenizer files."""
    print("=== Stage 4: Tokenizer ===")
    cmd = [
        sys.executable,
        str(_TOKENIZER_SCRIPT),
        "--model_name", MODEL_NAME,
        "--output_dir", str(_resolve(output_dir)),
    ]
    result = subprocess.run(cmd, cwd=str(_SCRIPT_DIR))
    if result.returncode != 0:
        raise RuntimeError(f"Tokenizer export failed (exit code {result.returncode})")
    print()


def download_vad(output_dir: str):
    """Download Silero VAD ONNX model."""
    from huggingface_hub import hf_hub_download
    print("=== Stage 5: Silero VAD ===")
    dst = _resolve(output_dir)
    dst.mkdir(parents=True, exist_ok=True)
    path = hf_hub_download(
        repo_id="onnx-community/silero-vad",
        filename="onnx/model.onnx",
        revision="e71cae966052b992a7eca6b17738916ce0eca4ec",
    )
    shutil.copy2(path, dst / "silero_vad.onnx")
    size_mb = (dst / "silero_vad.onnx").stat().st_size / (1024 * 1024)
    print(f"  [OK] silero_vad.onnx ({size_mb:.1f} MB)")
    print()


def generate_configs(output_dir: str):
    """Generate genai_config.json and audio_processor_config.json (Russian-only, no lang_id)."""
    print("=== Stage 6: Config files ===")
    from src.nemotron_model_load import (
        _load_nemo_model, get_att_context_size,
        D_MODEL, N_LAYERS, DECODER_HIDDEN, DECODER_LSTM_LAYERS,
    )

    asr_model = _load_nemo_model(MODEL_NAME)
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

    # Use same chunk_size as nemotron_model_load.py (CHUNK_SIZE = 1.12)
    chunk_size = 1.12
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

    # Russian-only: no lang_id in encoder inputs
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
            "language": "ru",
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
    print(f"  [OK] genai_config.json (Russian-only, no lang_id)")

    # Audio processor config
    window_size = preprocessor_cfg.get('window_size', 0.025)
    window_stride = preprocessor_cfg.get('window_stride', 0.01)
    if isinstance(window_size, float) and window_size < 1.0:
        window_length_samples = int(window_size * sample_rate)
    else:
        window_length_samples = int(window_size) if isinstance(window_size, (int, float)) else 400
    if isinstance(window_stride, float) and window_stride < 1.0:
        hop_length_samples = int(window_stride * sample_rate)
    else:
        hop_length_samples = int(window_stride) if isinstance(window_stride, (int, float)) else 160

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


def main():
    import argparse
    parser = argparse.ArgumentParser(description="Russian-only CPU int4 Nemotron export")
    parser.add_argument("--output_dir", default="build/onnx_models_cpu_ru",
                        help="Output directory for optimized models")
    args = parser.parse_args()

    output_dir = args.output_dir
    print(f"Output directory: {_resolve(output_dir)}")
    print(f"Model: {MODEL_NAME}")
    print(f"Language: Russian (lang_id={RU_LANG_ID})")
    print(f"Encoder: INT4 k-quant, CPU")
    print(f"Decoder/Joint: FP32, CPU")
    print()

    run_pipelines(output_dir)
    run_tokenizer(output_dir)
    download_vad(output_dir)
    generate_configs(output_dir)

    print("=" * 60)
    print("Russian-only CPU int4 export complete!")
    print(f"Output: {_resolve(output_dir)}")
    print("=" * 60)


if __name__ == "__main__":
    main()
