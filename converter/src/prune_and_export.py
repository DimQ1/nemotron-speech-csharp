"""Prune Nemotron encoder from 24 to 12 layers, then export to ONNX (INT4).

Layer pruning: keep every other ConformerLayer (0,2,4,...,22).
Halves encoder size (~658 MB -> ~330 MB). With INT4: targeting ~200 MB.

Usage: python src/prune_and_export.py
"""

import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import torch

_SCRIPT_DIR = Path(__file__).resolve().parent
_RECIPE_ROOT = _SCRIPT_DIR.parent
sys.path.insert(0, str(_RECIPE_ROOT))

# Patch Olive for torch-based k-quant
try:
    _PATCH_FILE = _RECIPE_ROOT / "patch_olive_torch.py"
    if _PATCH_FILE.exists():
        sys.path.insert(0, str(_PATCH_FILE.parent))
        import patch_olive_torch
        patch_olive_torch.patch_olive()
        print("[PATCH] Olive k-quant patched to use torch")
except Exception as e:
    print(f"[WARN] Patch failed: {e}")

MODEL_NAME = "nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b"
OUTPUT_DIR = _RECIPE_ROOT / "build" / "onnx_models_pruned"
MODELS_DST = _RECIPE_ROOT.parent / "models-onnx" / "cpu-pruned"
PRUNE_KEEP_EVERY = 2  # Keep every Nth layer (2 = half)

# Updated constants after pruning
ORIG_LAYERS = 24
NEW_LAYERS = ORIG_LAYERS // PRUNE_KEEP_EVERY  # 12
KEPT_INDICES = list(range(0, ORIG_LAYERS, PRUNE_KEEP_EVERY))  # [0,2,4,...,22]


def _resolve(path):
    p = Path(path)
    return p if p.is_absolute() else _SCRIPT_DIR / p


def _run_olive(config_name, output_dir, output_subdir):
    from olive import run as olive_run
    config_path = _SCRIPT_DIR / config_name
    with open(config_path) as f:
        config = json.load(f)
    config["output_dir"] = str(Path(output_dir) / output_subdir)
    with tempfile.NamedTemporaryFile(mode="w", suffix=".json", dir=str(_SCRIPT_DIR), delete=False) as tmp:
        json.dump(config, tmp, indent=4)
        tmp_path = tmp.name
    try:
        olive_run(tmp_path)
    finally:
        Path(tmp_path).unlink(missing_ok=True)


def load_model():
    import nemo.collections.asr as nemo_asr
    nemo_local = r"C:\Users\Dzmitry.Shchamialiou\.cache\huggingface\hub\models--nvidia--NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b\snapshots\c9bd53605b8b385d186177a93a9dddfbe2e67bef\nemotron-3.5-asr-streaming-0.6b.nemo"
    model = nemo_asr.models.ASRModel.restore_from(nemo_local)
    model = model.cpu()
    model.eval()
    return model


def prune_encoder(model):
    """Keep only every PRUNE_KEEP_EVERY-th ConformerLayer."""
    enc = model.encoder
    old_layers = enc.layers

    # Create new layer list with kept layers
    new_layers = torch.nn.ModuleList()
    for i in KEPT_INDICES:
        new_layers.append(old_layers[i])

    enc.layers = new_layers

    # Update encoder config — NeMo uses 'n_layers', not 'num_layers'
    from omegaconf import open_dict
    from omegaconf import ListConfig as OCList
    with open_dict(model.cfg.encoder):
        model.cfg.encoder.n_layers = NEW_LAYERS
        # Also update any layer-specific config lists
        for key in list(model.cfg.encoder.keys()):
            val = model.cfg.encoder[key]
            if isinstance(val, (list, OCList)):
                # Filter list entries that are layer-indexed
                if len(val) == ORIG_LAYERS:
                    new_val = [val[i] for i in KEPT_INDICES]
                    model.cfg.encoder[key] = new_val

    # Also update the N_LAYERS constant in nemotron_model_load
    # We'll create a patched loader

    print(f"  Pruned encoder layers: {ORIG_LAYERS} -> {NEW_LAYERS}")
    return model


def create_pruned_loader():
    """Generate a nemotron_model_load variant with N_LAYERS=12."""
    loader_path = _SCRIPT_DIR / "nemotron_model_load_pruned.py"
    with open(_SCRIPT_DIR / "nemotron_model_load.py") as f:
        code = f.read()

    # Replace N_LAYERS constant
    code = code.replace("N_LAYERS = 24", "N_LAYERS = 12")
    code = code.replace("N_LAYERS]", f"N_LAYERS]  # pruned from 24")

    with open(loader_path, "w") as f:
        f.write(code)
    print(f"  Generated pruned loader: {loader_path}")
    return loader_path


def main():
    print("=" * 70)
    print("Nemotron ASR — Layer Pruning (24->12) + INT4 Export")
    print("=" * 70)

    # 1. Load model
    print("\n── Step 1: Load model ──")
    model = load_model()
    print(f"  Loaded: {MODEL_NAME}")

    # 2. Prune encoder
    print("\n── Step 2: Prune encoder 24 -> 12 layers ──")
    model = prune_encoder(model)

    # 3. Save pruned model
    print("\n── Step 3: Save pruned .nemo ──")
    pruned_nemo = _RECIPE_ROOT / "build" / "nemotron_pruned_12l.nemo"
    pruned_nemo.parent.mkdir(parents=True, exist_ok=True)
    model.save_to(str(pruned_nemo))
    nemo_mb = pruned_nemo.stat().st_size / (1024 * 1024)
    print(f"  Saved: {pruned_nemo} ({nemo_mb:.0f} MB)")

    # 4. Create pruned loader
    print("\n── Step 4: Create pruned model loader ──")
    create_pruned_loader()

    # 5. Olive configs
    print("\n── Step 5: Olive export ──")
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    # Encoder config
    encoder_config = {
        "input_model": {
            "type": "PyTorchModel",
            "model_path": str(pruned_nemo),
            "model_loader": "encoder_model_loader",
            "model_script": "src/nemotron_model_load_pruned.py",
            "io_config": {
                "input_names": ["audio_signal", "length",
                    "cache_last_channel", "cache_last_time",
                    "cache_last_channel_len", "lang_id"],
                "output_names": ["outputs", "encoded_lengths",
                    "cache_last_channel_next", "cache_last_time_next",
                    "cache_last_channel_len_next"]
            },
            "dummy_inputs_func": "encoder_dummy_inputs"
        },
        "systems": {"local_system": {"type": "LocalSystem", "accelerators": [
            {"device": "cpu", "execution_providers": ["CPUExecutionProvider"]}
        ]}},
        "passes": {
            "convert": {"type": "OnnxConversion", "target_opset": 17,
                "dynamic": False, "use_dynamo_exporter": True},
            "quantization": {"type": "OnnxKQuantQuantization",
                "bits": 4, "block_size": 32, "accuracy_level": 4,
                "save_as_external_data": True, "external_data_name": "encoder.onnx.data"}
        },
        "target": "local_system", "no_artifacts": True
    }
    enc_cfg_path = _SCRIPT_DIR / "_enc_pruned.json"
    with open(enc_cfg_path, "w") as f:
        json.dump(encoder_config, f, indent=2)
    print("  [Encoder] INT4...")
    _run_olive("_enc_pruned.json", str(OUTPUT_DIR), "encoder.onnx")
    enc_cfg_path.unlink()

    # Decoder config (unchanged, FP32)
    decoder_config = {
        "input_model": {
            "type": "PyTorchModel", "model_path": str(pruned_nemo),
            "model_loader": "decoder_model_loader",
            "model_script": "src/nemotron_model_load.py",
            "io_config": {
                "input_names": ["targets", "h_in", "c_in"],
                "output_names": ["decoder_output", "h_out", "c_out"],
                "dynamic_axes": {"targets": {"0": "batch", "1": "target_len"},
                    "h_in": {"1": "batch"}, "c_in": {"1": "batch"},
                    "decoder_output": {"0": "batch", "2": "target_len"},
                    "h_out": {"1": "batch"}, "c_out": {"1": "batch"}}
            },
            "dummy_inputs_func": "decoder_dummy_inputs"
        },
        "systems": {"local_system": {"type": "LocalSystem", "accelerators": [
            {"device": "cpu", "execution_providers": ["CPUExecutionProvider"]}
        ]}},
        "passes": {"convert": {"type": "OnnxConversion", "target_opset": 17,
            "save_as_external_data": True, "external_data_name": "decoder.onnx.data"}},
        "target": "local_system", "no_artifacts": True
    }
    dec_cfg_path = _SCRIPT_DIR / "_dec_pruned.json"
    with open(dec_cfg_path, "w") as f:
        json.dump(decoder_config, f, indent=2)
    print("  [Decoder] FP32...")
    _run_olive("_dec_pruned.json", str(OUTPUT_DIR), "decoder.onnx")
    dec_cfg_path.unlink()

    # Joint config (unchanged, FP32)
    joint_config = {
        "input_model": {
            "type": "PyTorchModel", "model_path": str(pruned_nemo),
            "model_loader": "joint_model_loader",
            "model_script": "src/nemotron_model_load.py",
            "io_config": {
                "input_names": ["encoder_output", "decoder_output"],
                "output_names": ["joint_output"],
                "dynamic_axes": {"encoder_output": {"0": "batch", "1": "time"},
                    "decoder_output": {"0": "batch", "1": "target_len"},
                    "joint_output": {"0": "batch", "1": "time", "2": "target_len"}}
            },
            "dummy_inputs_func": "joint_dummy_inputs"
        },
        "systems": {"local_system": {"type": "LocalSystem", "accelerators": [
            {"device": "cpu", "execution_providers": ["CPUExecutionProvider"]}
        ]}},
        "passes": {"convert": {"type": "OnnxConversion", "target_opset": 17,
            "save_as_external_data": True, "external_data_name": "joint.onnx.data"}},
        "target": "local_system", "no_artifacts": True
    }
    jnt_cfg_path = _SCRIPT_DIR / "_jnt_pruned.json"
    with open(jnt_cfg_path, "w") as f:
        json.dump(joint_config, f, indent=2)
    print("  [Joint] FP32...")
    _run_olive("_jnt_pruned.json", str(OUTPUT_DIR), "joint.onnx")
    jnt_cfg_path.unlink()

    # 6. Tokenizer + VAD + Configs
    print("\n── Step 6: Tokenizer + VAD + Configs ──")

    # Tokenizer
    cmd = [sys.executable, str(_RECIPE_ROOT / "scripts" / "export_tokenizer.py"),
           "--model_name", MODEL_NAME, "--output_dir", str(OUTPUT_DIR)]
    subprocess.run(cmd, cwd=str(_SCRIPT_DIR))

    # VAD
    vad_src = _RECIPE_ROOT.parent / "models-onnx" / "cpu" / "silero_vad.onnx"
    if vad_src.exists():
        shutil.copy2(vad_src, OUTPUT_DIR / "silero_vad.onnx")

    # Audio processor config
    from src.nemotron_model_load import _load_nemo_model
    asr_model = _load_nemo_model(MODEL_NAME)
    asr_model.eval()
    pp = asr_model.cfg.get('preprocessor', {})
    sr = pp.get('sample_rate', 16000)
    ws = pp.get('window_size', 0.025)
    wstr = pp.get('window_stride', 0.01)

    audio_config = {
        "model_type": "speech_features",
        "audio_params": {
            "sample_rate": sr, "n_fft": pp.get('n_fft', 512),
            "hop_length": int(wstr * sr) if isinstance(wstr, float) else 160,
            "n_mels": pp.get('features', pp.get('nfilt', 128)),
            "window_length": int(ws * sr) if isinstance(ws, float) else 400,
            "window_type": "hann", "fmin": 0, "fmax": sr // 2,
            "dither": 0.0, "preemphasis": pp.get('preemph', 0.97),
            "log_zero_guard_type": "add", "log_zero_guard_value": 1e-10,
            "normalize": "none", "center": True, "mag_power": 2.0,
        },
    }
    with open(OUTPUT_DIR / "audio_processor_config.json", "w") as f:
        json.dump(audio_config, f, indent=2)

    # GenAI config (with pruned N_LAYERS=12)
    enc = asr_model.encoder
    joint = asr_model.joint
    vocab_size = joint.num_classes_with_blank

    from src.nemotron_model_load import get_att_context_size, D_MODEL, DECODER_HIDDEN, DECODER_LSTM_LAYERS

    genai_config = {
        "model": {
            "type": "nemotron_speech",
            "vocab_size": vocab_size,
            "num_mels": pp.get('features', pp.get('nfilt', 128)),
            "fft_size": pp.get('n_fft', 512),
            "hop_length": pp.get('hop_length', 160),
            "win_length": pp.get('win_length', 400),
            "preemph": pp.get('preemph', 0.97),
            "log_eps": 5.96046448e-08,
            "subsampling_factor": getattr(enc, 'subsampling_factor', 8),
            "left_context": get_att_context_size(1.12)[0],
            "conv_context": 8,
            "pre_encode_cache_size": 9,
            "sample_rate": sr,
            "chunk_samples": int(1.12 * sr),
            "blank_id": vocab_size - 1,
            "max_symbols_per_step": 10,
            "encoder": {
                "filename": "encoder.onnx",
                "hidden_size": D_MODEL,
                "num_hidden_layers": NEW_LAYERS,
                "inputs": {
                    "audio_features": "audio_signal", "input_lengths": "length",
                    "cache_last_channel": "cache_last_channel",
                    "cache_last_time": "cache_last_time",
                    "cache_last_channel_len": "cache_last_channel_len",
                    "lang_id": "lang_id",
                },
                "outputs": {
                    "encoder_outputs": "outputs", "output_lengths": "encoded_lengths",
                    "cache_last_channel_next": "cache_last_channel_next",
                    "cache_last_time_next": "cache_last_time_next",
                    "cache_last_channel_len_next": "cache_last_channel_len_next",
                },
            },
            "decoder": {
                "filename": "decoder.onnx",
                "hidden_size": DECODER_HIDDEN,
                "num_hidden_layers": DECODER_LSTM_LAYERS,
                "inputs": {"targets": "targets", "lstm_hidden_state": "h_in", "lstm_cell_state": "c_in"},
                "outputs": {"outputs": "decoder_output", "lstm_hidden_state": "h_out", "lstm_cell_state": "c_out"},
            },
            "joiner": {
                "filename": "joint.onnx",
                "inputs": {"encoder_outputs": "encoder_output", "decoder_outputs": "decoder_output"},
                "outputs": {"logits": "joint_output"},
            },
            "vad": {"filename": "silero_vad.onnx", "threshold": 0.3,
                "silence_duration_ms": 3360, "prefix_padding_ms": 560},
        },
    }
    with open(OUTPUT_DIR / "genai_config.json", "w") as f:
        json.dump(genai_config, f, indent=2)

    # 7. Copy to models-onnx
    print("\n── Step 7: Copy to models-onnx/cpu-pruned ──")
    MODELS_DST.mkdir(parents=True, exist_ok=True)
    for f in OUTPUT_DIR.glob("*"):
        if f.is_file():
            shutil.copy2(f, MODELS_DST / f.name)

    total = sum(f.stat().st_size for f in MODELS_DST.glob("**/*") if f.is_file())
    print(f"\n{'='*70}")
    print(f"Pruned model size: {total/(1024*1024):.1f} MB")
    print(f"Target:            < 300 MB")
    print(f"Output:            {MODELS_DST}")
    print(f"{'='*70}")


if __name__ == "__main__":
    main()
