"""Trim Nemotron ASR model to RU+EN vocabulary, re-export with INT4 quantization.

Pipeline:
1. Load NeMo model
2. Filter SentencePiece tokenizer to RU+EN tokens
3. change_vocabulary() — rebuilds decoder/joint with reduced output layer
4. Trim language prompt table to RU+EN only (lang_ids: 0=en, 1=en-GB, 11=ru)
5. Save trimmed .nemo
6. Export encoder (INT4), decoder (FP32), joint (FP32) via Olive
7. Generate configs

Target: < 300 MB total
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import sentencepiece as spm
import torch

# ── Paths ────────────────────────────────────────────────────────────────
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
OUTPUT_DIR = _RECIPE_ROOT / "build" / "onnx_models_ru_en"
MODELS_DST = _RECIPE_ROOT.parent / "models-onnx" / "cpu-ru-en"

# Language IDs to keep: 0=en, 1=en-GB, 11=ru
KEEP_LANG_IDS = [0, 1, 11]

# ── Helpers ──────────────────────────────────────────────────────────────

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


def _load_model(model_name=MODEL_NAME):
    """Load NeMo model, preferring cached .nemo file to avoid network."""
    from src.nemotron_model_load import _load_nemo_model
    # Try local .nemo path directly
    import nemo.collections.asr as nemo_asr
    nemo_local = r"C:\Users\Dzmitry.Shchamialiou\.cache\huggingface\hub\models--nvidia--NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b\snapshots\c9bd53605b8b385d186177a93a9dddfbe2e67bef\nemotron-3.5-asr-streaming-0.6b.nemo"
    if Path(nemo_local).exists():
        model = nemo_asr.models.ASRModel.restore_from(nemo_local)
        model = model.cpu()
        model.eval()
        return model
    return _load_nemo_model(model_name)


# ── Stage 1: Build RU+EN SentencePiece tokenizer ─────────────────────────

def _is_ru_en_token(token: str) -> bool:
    """Check if a SentencePiece token consists only of RU/EN chars + punctuation."""
    ru = set("абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ")
    en = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
    punct = set(" .,!?-':;\"()[]{}№%$€£¥&*#@/\\|<>+=~^_")
    digits = set("0123456789")
    allowed = ru | en | punct | digits
    # Strip SentencePiece prefix
    clean = token.replace("▁", "").replace("<", "").replace(">", "")
    return all(c in allowed for c in clean)


def build_ru_en_tokenizer(original_model_path: str, output_dir: Path):
    """Build a filtered SentencePiece tokenizer keeping only RU+EN tokens."""
    from sp_model_utils import parse_sp_model_raw, build_sp_model

    # Load and parse original SentencePiece model
    with open(original_model_path, "rb") as f:
        data = f.read()

    pieces = parse_sp_model_raw(data)
    original_count = len(pieces)
    kept = []
    specials = []

    for piece, score, ptype in pieces:
        # Always keep special tokens (control=3, unknown=2, user_defined=4, byte=5, unused=6)
        if ptype in [2, 3, 4, 5, 6]:
            specials.append((piece, score, ptype))
        elif _is_ru_en_token(piece):
            kept.append((piece, score, ptype))

    new_pieces = specials + kept
    new_data = build_sp_model(new_pieces)

    output_dir.mkdir(parents=True, exist_ok=True)
    model_path = output_dir / "tokenizer.model"
    vocab_path = output_dir / "vocab.txt"

    with open(model_path, "wb") as f:
        f.write(new_data)

    # Write vocab.txt
    with open(vocab_path, "w", encoding="utf-8") as f:
        for piece, score, _ in new_pieces:
            f.write(f"{piece}\t{score}\n")

    print(f"  Tokens: {original_count} -> {len(new_pieces)} "
          f"({len(specials)} special + {len(kept)} RU/EN, "
          f"{(1 - len(new_pieces)/original_count)*100:.0f}% reduction)")

    return str(output_dir)


# ── Stage 2: Trim language prompts ────────────────────────────────────────

def trim_language_prompts(model, keep_ids):
    """Trim the prompt table and kernel to only the specified language IDs."""
    import torch.nn as nn

    NUM_PROMPTS = 128
    D_MODEL = 1024

    # Get the old prompt kernel
    old_kernel = model.prompt_kernel  # Sequential(Linear->ReLU->Linear)

    # Get the prompt table embedding weights
    # The prompt table is [NUM_PROMPTS, D_MODEL] — each language has an embedding
    # We take only the rows for keep_ids
    # Actually, the prompt table is in the model's prompt_kernel or a separate table

    # In Nemotron, the prompt is a one-hot that gets concatenated with encoder output
    # and projected via prompt_kernel (Linear(D_MODEL + NUM_PROMPTS, D_MODEL) -> ReLU -> Linear(D_MODEL, D_MODEL))
    # We need to reduce the first Linear's input from D_MODEL + 128 to D_MODEL + len(keep_ids)

    new_num_prompts = len(keep_ids)

    # Extract the weight and bias from the first Linear layer
    old_linear = old_kernel[0]  # nn.Linear
    old_weight = old_linear.weight.data  # [D_MODEL, D_MODEL + NUM_PROMPTS]
    old_bias = old_linear.bias.data      # [D_MODEL]

    # The prompt part of the weight is the last NUM_PROMPTS columns
    encoder_weight = old_weight[:, :D_MODEL]       # [D_MODEL, D_MODEL]
    prompt_weight = old_weight[:, D_MODEL:]         # [D_MODEL, NUM_PROMPTS]

    # Keep only the columns for our languages
    keep_tensor = torch.tensor(keep_ids, dtype=torch.long)
    new_prompt_weight = prompt_weight[:, keep_tensor]  # [D_MODEL, new_num_prompts]
    new_weight = torch.cat([encoder_weight, new_prompt_weight], dim=1)  # [D_MODEL, D_MODEL + new_num_prompts]

    # Create new prompt kernel
    new_linear = nn.Linear(D_MODEL + new_num_prompts, D_MODEL)
    new_linear.weight.data = new_weight
    new_linear.bias.data = old_bias.clone()
    new_linear.requires_grad_(False)

    # Keep the ReLU and second Linear unchanged
    new_kernel = nn.Sequential(
        new_linear,
        old_kernel[1],  # ReLU (can be reused)
        old_kernel[2],  # Second Linear (unchanged)
    )
    new_kernel.eval()

    model.prompt_kernel = new_kernel

    # Update model config so save/restore works correctly
    from omegaconf import open_dict
    with open_dict(model.cfg):
        if hasattr(model.cfg, 'prompt'):
            model.cfg.prompt.num_prompts = new_num_prompts
        # Also update the encoder's prompt dimension in the config
        if hasattr(model.cfg, 'encoder'):
            pass  # encoder doesn't store prompt info directly

    print(f"  Language prompts: {NUM_PROMPTS} -> {new_num_prompts}")
    print(f"  Prompt kernel input: {D_MODEL + NUM_PROMPTS} -> {D_MODEL + new_num_prompts}")
    print(f"  Prompt weight reduction: {(1 - new_num_prompts/NUM_PROMPTS)*100:.0f}%")

    return model, new_num_prompts


# ── Stage 3: Create RU+EN model loader ────────────────────────────────────

def create_ru_en_loader_script(new_num_prompts: int, keep_lang_ids: list):
    """Generate a model loader for Olive export with trimmed language prompts."""
    loader_code = f'''
# Auto-generated RU+EN model loader
import torch
import torch.nn as nn
import torch.nn.functional as F

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))

from src.nemotron_model_load import (
    _load_nemo_model, get_att_context_size, _get_streaming_shapes,
    D_MODEL, N_LAYERS, DECODER_HIDDEN, DECODER_LSTM_LAYERS,
    MEL_FEATURES, MODEL_NAME, CHUNK_SIZE, SUBSAMPLING_FACTOR,
    CONV_CONTEXT, LEFT_CHUNKS,
)

NEW_NUM_PROMPTS = {new_num_prompts}
KEEP_LANG_IDS = {keep_lang_ids}
# Map old lang_id -> new lang_id index
LANG_ID_MAP = {{old: new for new, old in enumerate(KEEP_LANG_IDS)}}
DEFAULT_LANG = 0  # new index for English


class StreamingEncoderWrapperRUEN(nn.Module):
    """Encoder wrapper with trimmed RU+EN language prompts."""

    def __init__(self, enc, prompt_kernel):
        super().__init__()
        self.enc = enc
        self.prompt_kernel = prompt_kernel

    def forward(self, audio_signal, length,
                cache_last_channel, cache_last_time, cache_last_channel_len,
                lang_id):
        audio_signal = audio_signal.transpose(1, 2)
        encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next = \\
            self.enc.forward_for_export(
                audio_signal=audio_signal,
                length=length,
                cache_last_channel=cache_last_channel,
                cache_last_time=cache_last_time,
                cache_last_channel_len=cache_last_channel_len,
            )
        encoded = encoded.transpose(1, 2)

        # Map old lang_id -> new index via lookup table
        lookup = torch.tensor(
            [LANG_ID_MAP.get(i, 0) for i in range(128)],
            dtype=torch.long, device=lang_id.device
        )
        mapped_lang = lookup[lang_id.clamp(0, 127)]

        onehot = F.one_hot(mapped_lang, num_classes=NEW_NUM_PROMPTS).to(encoded.dtype)
        prompt = onehot.unsqueeze(1).expand(-1, encoded.shape[1], -1)
        concat = torch.cat([encoded, prompt], dim=-1)
        encoded = self.prompt_kernel(concat).to(encoded.dtype)
        return encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next


def encoder_model_loader_ru_en(model_name):
    """Load trimmed RU+EN model for encoder export."""
    # Load the trimmed .nemo
    import nemo.collections.asr as nemo_asr
    asr_model = nemo_asr.models.ASRModel.restore_from(model_name)
    asr_model.eval()

    encoder = asr_model.encoder
    prompt_kernel = asr_model.prompt_kernel

    att_context_size = get_att_context_size()
    if hasattr(encoder, "set_default_att_context_size"):
        encoder.set_default_att_context_size(att_context_size)

    wrapper = StreamingEncoderWrapperRUEN(encoder, prompt_kernel)
    wrapper.eval()
    return wrapper


def encoder_dummy_inputs_ru_en(model):
    shapes = _get_streaming_shapes()
    static_mel_frames = shapes["static_mel_frames"]
    last_channel_cache_size = shapes["last_channel_cache_size"]
    batch = 1
    return (
        torch.randn(batch, static_mel_frames, MEL_FEATURES),
        torch.tensor([static_mel_frames], dtype=torch.int64),
        torch.zeros(batch, N_LAYERS, last_channel_cache_size, D_MODEL),
        torch.zeros(batch, N_LAYERS, D_MODEL, CONV_CONTEXT),
        torch.zeros(batch, dtype=torch.int64),
        torch.zeros(batch, dtype=torch.int64),  # lang_id
    )
'''
    loader_path = _SCRIPT_DIR / "nemotron_model_load_ru_en.py"
    with open(loader_path, "w") as f:
        f.write(loader_code)
    print(f"  Generated loader: {loader_path}")
    return loader_path


# ── Main pipeline ──────────────────────────────────────────────────────────

def main():
    print("=" * 70)
    print("Nemotron ASR — RU+EN vocabulary trimming + INT4 export")
    print("=" * 70)

    # 1. Load model
    print("\n── Step 1: Load original model ──")
    model = _load_model()
    print(f"  Model loaded: {MODEL_NAME}")

    # 2. Build RU+EN tokenizer
    print("\n── Step 2: Build RU+EN tokenizer ──")
    trimmed_tok_dir = _RECIPE_ROOT / "build" / "tokenizer_ru_en"
    trimmed_tok_dir.mkdir(parents=True, exist_ok=True)

    # Extract tokenizer.model from .nemo archive
    import tarfile
    nemo_local = r"C:\Users\Dzmitry.Shchamialiou\.cache\huggingface\hub\models--nvidia--NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b\snapshots\c9bd53605b8b385d186177a93a9dddfbe2e67bef\nemotron-3.5-asr-streaming-0.6b.nemo"

    sp_model_path = None
    with tarfile.open(nemo_local, 'r:*') as tar:
        for m in tar.getmembers():
            if 'tokenizer.model' in m.name:
                tar.extract(m, trimmed_tok_dir)
                sp_model_path = str(trimmed_tok_dir / m.name)
                print(f"  Extracted tokenizer: {sp_model_path}")
                break

    if not sp_model_path:
        raise RuntimeError("Could not find tokenizer.model in .nemo archive")

    new_tok_dir = build_ru_en_tokenizer(sp_model_path, trimmed_tok_dir)

    # 3. Change vocabulary
    print("\n── Step 3: change_vocabulary() ──")
    model.change_vocabulary(
        new_tokenizer_dir=new_tok_dir,
        new_tokenizer_type="bpe",
    )
    new_vocab_size = model.tokenizer.vocab_size
    print(f"  New vocab size: {new_vocab_size}")

    # 4. Language prompts: keep original 128 (saves <1 MB, not worth the config complexity)
    print("\n── Step 4: Language prompts ──")
    new_num_prompts = 128  # Keep full prompt table (negligible size)
    print(f"  Keeping all {new_num_prompts} language prompts (table size <1 MB)")
    # Note: decoder/joint output layers already reduced via change_vocabulary()

    # 5. Save trimmed model
    print("\n── Step 5: Save trimmed .nemo ──")
    trimmed_nemo = _RECIPE_ROOT / "build" / "nemotron_ru_en_trimmed.nemo"
    trimmed_nemo.parent.mkdir(parents=True, exist_ok=True)
    model.save_to(str(trimmed_nemo))
    print(f"  Saved: {trimmed_nemo}")
    nemo_size_mb = trimmed_nemo.stat().st_size / (1024 * 1024)
    print(f"  Size: {nemo_size_mb:.1f} MB")

    # 6. Use standard encoder loader (prompt table unchanged — 128 langs)
    print("\n── Step 6: Encoder config ──")
    encoder_config = {
        "input_model": {
            "type": "PyTorchModel",
            "model_path": str(trimmed_nemo),
            "model_loader": "encoder_model_loader",
            "model_script": "src/nemotron_model_load.py",
            "io_config": {
                "input_names": [
                    "audio_signal", "length",
                    "cache_last_channel", "cache_last_time",
                    "cache_last_channel_len",
                    "lang_id"
                ],
                "output_names": [
                    "outputs", "encoded_lengths",
                    "cache_last_channel_next", "cache_last_time_next",
                    "cache_last_channel_len_next"
                ]
            },
            "dummy_inputs_func": "encoder_dummy_inputs"
        },
        "systems": {
            "local_system": {
                "type": "LocalSystem",
                "accelerators": [
                    {"device": "cpu", "execution_providers": ["CPUExecutionProvider"]}
                ]
            }
        },
        "passes": {
            "convert": {
                "type": "OnnxConversion",
                "target_opset": 17,
                "dynamic": False,
                "use_dynamo_exporter": True
            },
            "quantization": {
                "type": "OnnxKQuantQuantization",
                "bits": 4,
                "block_size": 32,
                "accuracy_level": 4,
                "save_as_external_data": True,
                "external_data_name": "encoder.onnx.data"
            }
        },
        "target": "local_system",
        "no_artifacts": True
    }
    encoder_config_path = _SCRIPT_DIR / "nemotron_encoder_int4_cpu_ru_en.json"
    with open(encoder_config_path, "w") as f:
        json.dump(encoder_config, f, indent=2)
    print(f"  Encoder config: {encoder_config_path}")

    # 8. Run Olive pipelines
    print("\n── Step 8: Olive export ──")
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print("  [Encoder] INT4 k-quant...")
    _run_olive("nemotron_encoder_int4_cpu_ru_en.json", str(OUTPUT_DIR), "encoder.onnx")

    # Use existing decoder/joint CPU configs but point to trimmed model
    print("  [Decoder] FP32...")
    decoder_config = {
        "input_model": {
            "type": "PyTorchModel",
            "model_path": str(trimmed_nemo),
            "model_loader": "decoder_model_loader",
            "model_script": "src/nemotron_model_load.py",
            "io_config": {
                "input_names": ["targets", "h_in", "c_in"],
                "output_names": ["decoder_output", "h_out", "c_out"],
                "dynamic_axes": {
                    "targets": {"0": "batch", "1": "target_len"},
                    "h_in": {"1": "batch"}, "c_in": {"1": "batch"},
                    "decoder_output": {"0": "batch", "2": "target_len"},
                    "h_out": {"1": "batch"}, "c_out": {"1": "batch"}
                }
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
    decoder_config_path = _SCRIPT_DIR / "_tmp_decoder_ru_en.json"
    with open(decoder_config_path, "w") as f:
        json.dump(decoder_config, f, indent=2)
    _run_olive("_tmp_decoder_ru_en.json", str(OUTPUT_DIR), "decoder.onnx")
    decoder_config_path.unlink()

    print("  [Joint] FP32...")
    joint_config = {
        "input_model": {
            "type": "PyTorchModel",
            "model_path": str(trimmed_nemo),
            "model_loader": "joint_model_loader",
            "model_script": "src/nemotron_model_load.py",
            "io_config": {
                "input_names": ["encoder_output", "decoder_output"],
                "output_names": ["joint_output"],
                "dynamic_axes": {
                    "encoder_output": {"0": "batch", "1": "time"},
                    "decoder_output": {"0": "batch", "1": "target_len"},
                    "joint_output": {"0": "batch", "1": "time", "2": "target_len"}
                }
            },
            "dummy_inputs_func": "joint_dummy_inputs"
        },
        "systems": {"local_system": {"type": "LocalSystem", "accelerators": [
            {"device": "cpu", "execution_providers": ["CPUExecutionProvider"]}
        ]}},
        "passes": {"convert": {"type": "OnnxConversion", "target_opset": 17,
            "save_as_external_data": True, "external_data_name": "joint.onnx.data"},
            "quantization": {
                "type": "OnnxKQuantQuantization",
                "bits": 4, "block_size": 16, "accuracy_level": 4,
                "save_as_external_data": True,
                "external_data_name": "joint.onnx.data"
            }
        },
        "target": "local_system", "no_artifacts": True
    }
    joint_config_path = _SCRIPT_DIR / "_tmp_joint_ru_en.json"
    with open(joint_config_path, "w") as f:
        json.dump(joint_config, f, indent=2)
    _run_olive("_tmp_joint_ru_en.json", str(OUTPUT_DIR), "joint.onnx")
    joint_config_path.unlink()

    # 9. Tokenizer & VAD
    print("\n── Step 9: Tokenizer + VAD + Configs ──")

    # Copy trimmed tokenizer
    tok_dst = OUTPUT_DIR / "tokenizer"
    tok_dst.mkdir(exist_ok=True)
    for f in Path(new_tok_dir).glob("*"):
        shutil.copy2(f, tok_dst / f.name)

    # Export tokenizer files using the export script
    cmd = [sys.executable, str(_RECIPE_ROOT / "scripts" / "export_tokenizer.py"),
           "--model_name", str(trimmed_nemo),
           "--output_dir", str(OUTPUT_DIR)]
    subprocess.run(cmd, cwd=str(_SCRIPT_DIR))

    # Copy VAD
    vad_src = _RECIPE_ROOT.parent / "models-onnx" / "cpu" / "silero_vad.onnx"
    if vad_src.exists():
        shutil.copy2(vad_src, OUTPUT_DIR / "silero_vad.onnx")

    # 10. Generate genai_config.json
    print("\n── Step 10: Generate genai_config.json ──")
    # Reload trimmed model
    import nemo.collections.asr as nemo_asr
    asr_model = nemo_asr.models.ASRModel.restore_from(str(trimmed_nemo))
    asr_model = asr_model.cpu()
    asr_model.eval()

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

    from src.nemotron_model_load import (
        get_att_context_size, D_MODEL, N_LAYERS, DECODER_HIDDEN, DECODER_LSTM_LAYERS
    )
    chunk_size = 1.12
    att_context_size = get_att_context_size(chunk_size)
    left_context = att_context_size[0]
    chunk_samples = int(chunk_size * sample_rate)

    genai_config = {
        "model": {
            "type": "nemotron_speech",
            "vocab_size": vocab_size,
            "num_mels": n_mels,
            "fft_size": n_fft,
            "hop_length": hop_length,
            "win_length": win_length,
            "preemph": preprocessor_cfg.get('preemph', 0.97),
            "log_eps": 5.96046448e-08,
            "subsampling_factor": getattr(encoder, 'subsampling_factor', 8),
            "left_context": left_context,
            "conv_context": 8,
            "pre_encode_cache_size": 9,
            "sample_rate": sample_rate,
            "chunk_samples": chunk_samples,
            "blank_id": blank_id,
            "max_symbols_per_step": 10,
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
                "inputs": {"targets": "targets", "lstm_hidden_state": "h_in", "lstm_cell_state": "c_in"},
                "outputs": {"outputs": "decoder_output", "lstm_hidden_state": "h_out", "lstm_cell_state": "c_out"},
            },
            "joiner": {
                "filename": "joint.onnx",
                "inputs": {"encoder_outputs": "encoder_output", "decoder_outputs": "decoder_output"},
                "outputs": {"logits": "joint_output"},
            },
            "vad": {
                "filename": "silero_vad.onnx",
                "threshold": 0.3,
                "silence_duration_ms": 3360,
                "prefix_padding_ms": 560,
            },
        },
    }

    with open(OUTPUT_DIR / "genai_config.json", "w") as f:
        json.dump(genai_config, f, indent=2)

    # Audio processor config
    window_size = preprocessor_cfg.get('window_size', 0.025)
    window_stride = preprocessor_cfg.get('window_stride', 0.01)
    audio_config = {
        "model_type": "speech_features",
        "audio_params": {
            "sample_rate": sample_rate, "n_fft": n_fft,
            "hop_length": int(window_stride * sample_rate) if isinstance(window_stride, float) else 160,
            "n_mels": n_mels,
            "window_length": int(window_size * sample_rate) if isinstance(window_size, float) else 400,
            "window_type": "hann", "fmin": 0, "fmax": sample_rate // 2,
            "dither": 0.0, "preemphasis": preprocessor_cfg.get('preemph', 0.97),
            "log_zero_guard_type": "add", "log_zero_guard_value": 1e-10,
            "normalize": "none", "center": True, "mag_power": 2.0,
        },
    }
    with open(OUTPUT_DIR / "audio_processor_config.json", "w") as f:
        json.dump(audio_config, f, indent=2)

    # 11. Copy to models-onnx
    print("\n── Step 11: Copy to models-onnx/cpu-ru-en ──")
    MODELS_DST.mkdir(parents=True, exist_ok=True)
    for f in OUTPUT_DIR.glob("*"):
        if f.is_file():
            shutil.copy2(f, MODELS_DST / f.name)

    # Size summary
    total = sum(f.stat().st_size for f in MODELS_DST.glob("**/*") if f.is_file())
    print(f"\n{'='*70}")
    print(f"Model size: {total / (1024*1024):.1f} MB")
    print(f"Target:     < 300 MB")
    print(f"Output:     {MODELS_DST}")
    print(f"{'='*70}")


if __name__ == "__main__":
    main()
