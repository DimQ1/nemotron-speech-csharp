# -------------------------------------------------------------------------
# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License.
# --------------------------------------------------------------------------
"""Model loaders and dummy input generators for Nemotron Speech Streaming components.

Used by Olive's OnnxConversion pass via the ``model_script`` / ``model_loader``
mechanism. Each component (encoder, decoder, joint) has its own loader and
dummy inputs function, referenced from separate Olive JSON configs.

Streaming defaults (chunk_size=0.56s, left_chunks=10) are defined as constants
below and are the single source of truth for the export shapes.
"""

import torch
import torch.nn as nn
import torch.nn.functional as F

# ---------------------------------------------------------------------------
# Shared streaming constants
# ---------------------------------------------------------------------------
# CHUNK_SIZE is hardcoded because it determines the static ONNX input shapes
# at export time. The NeMo model supports multiple chunk sizes (0.08, 0.16,
# 0.56, 1.12s) at runtime, but once exported to ONNX with static shapes the
# encoder is locked to a single chunk size. 0.56s is the recommended default
# per NVIDIA's documentation (best latency/accuracy trade-off). The value is
# not available from a HuggingFace config — it lives inside the .nemo archive
# as encoder.att_context_size and requires loading the full model to read.
#
# It can be overridden per-export via the NEMOTRON_CHUNK_SIZE environment
# variable so that a single recipe can produce encoders tuned for different
# accuracy/latency windows (0.08 / 0.16 / 0.56 / 1.12 seconds).
import os as _os


def _chunk_size() -> float:
    """Read the streaming chunk size (seconds) from the environment.

    Read lazily (not at import time) so that --chunk-size applied after the
    module is first imported still takes effect.
    """
    return float(_os.environ.get("NEMOTRON_CHUNK_SIZE", "0.56"))


# Backwards-compatible module attribute (default value at import).
CHUNK_SIZE = _chunk_size()
MEL_FEATURES = 128
SUBSAMPLING_FACTOR = 8

# Model architecture constants (0.6B multilingual model)
N_LAYERS = 24
D_MODEL = 1024
CONV_CONTEXT = 8  # conv_kernel_size(9) - 1
DECODER_HIDDEN = 640
DECODER_LSTM_LAYERS = 2
NUM_PROMPTS = 128  # one-hot language-ID size

# Streaming config — single source of truth.
# chunk_encoded_frames = int(CHUNK_SIZE * 100) // SUBSAMPLING_FACTOR = 7
# last_channel_cache_size = LEFT_CHUNKS * chunk_encoded_frames = 70
LEFT_CHUNKS = 10

MODEL_NAME = "nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b"


def get_att_context_size(chunk_size: float = None, left_chunks: int = LEFT_CHUNKS):
    """Compute attention context size for the streaming encoder.

    left_context = left_chunks * chunk_encoded_frames;
    right_context is indexed by chunk_size.
    """
    if chunk_size is None:
        chunk_size = _chunk_size()
    right_context = {0.08: 0, 0.16: 1, 0.56: 6, 1.12: 13}.get(chunk_size, 13)
    chunk_encoded_frames = int(chunk_size * 100) // SUBSAMPLING_FACTOR
    left_context = left_chunks * chunk_encoded_frames
    return [left_context, right_context]


def _get_streaming_shapes():
    """Compute static streaming tensor shapes from the shared constants."""
    chunk_size = _chunk_size()
    pre_encode_cache = 9
    chunk_mel_frames = int(chunk_size * 100)  # 56 for 0.56s
    static_mel_frames = chunk_mel_frames + pre_encode_cache  # 65
    chunk_encoded_frames = chunk_mel_frames // SUBSAMPLING_FACTOR  # 7
    last_channel_cache_size = LEFT_CHUNKS * chunk_encoded_frames  # 70

    return {
        "last_channel_cache_size": last_channel_cache_size,
        "static_mel_frames": static_mel_frames,
        "chunk_encoded_frames": chunk_encoded_frames,
    }


def _load_nemo_model(model_name=MODEL_NAME):
    """Load the NeMo ASR model (shared across loaders).

    For HF repo IDs we download the ``.nemo`` archive via ``hf_hub_download``
    and feed it to ``restore_from``. This avoids a bug in NeMo 2.6.2's
    ``from_pretrained`` HF-cache integration where the cache directory is
    incorrectly treated as already-extracted (because the repo also ships
    README/safety markdown files alongside the archive), causing
    ``model_config.yaml`` lookup to fail.
    """
    import nemo.collections.asr as nemo_asr

    if model_name.endswith(".nemo"):
        nemo_path = model_name
    else:
        from huggingface_hub import hf_hub_download, list_repo_files

        # Find the .nemo file in the repo (filename is not standardised).
        files = list_repo_files(model_name)
        nemo_files = [f for f in files if f.endswith(".nemo")]
        if not nemo_files:
            raise RuntimeError(
                f"No .nemo archive found in HuggingFace repo {model_name!r}"
            )
        if len(nemo_files) > 1:
            raise RuntimeError(
                f"Multiple .nemo archives found in {model_name!r}: {nemo_files}"
            )
        nemo_path = hf_hub_download(repo_id=model_name, filename=nemo_files[0])

    asr_model = nemo_asr.models.ASRModel.restore_from(nemo_path)
    asr_model = asr_model.cpu()
    asr_model.eval()
    return asr_model


# ---------------------------------------------------------------------------
# Encoder
# ---------------------------------------------------------------------------

class StreamingEncoderWrapper(nn.Module):
    """Wrap the NeMo CacheAware encoder + prompt_kernel for streaming ONNX export.

    Takes a `lang_id` int64 input of shape [B]. The one-hot prompt tensor of
    shape [B, T_out, NUM_PROMPTS] is built inside the graph, then concatenated
    to the encoded features along the channel dim and projected back to
    D_MODEL via `prompt_kernel` (Linear -> ReLU -> Linear).
    """

    def __init__(self, enc, prompt_kernel):
        super().__init__()
        self.enc = enc
        self.prompt_kernel = prompt_kernel

    def forward(self, audio_signal, length,
                cache_last_channel, cache_last_time, cache_last_channel_len,
                lang_id):
        audio_signal = audio_signal.transpose(1, 2)  # [B, T, mel] -> [B, mel, T]
        encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next = \
            self.enc.forward_for_export(
                audio_signal=audio_signal,
                length=length,
                cache_last_channel=cache_last_channel,
                cache_last_time=cache_last_time,
                cache_last_channel_len=cache_last_channel_len,
            )
        encoded = encoded.transpose(1, 2)  # [B, D, T] -> [B, T, D]
        # Build one-hot prompt [B, T, NUM_PROMPTS] from lang_id [B].
        onehot = F.one_hot(lang_id, num_classes=NUM_PROMPTS).to(encoded.dtype)  # [B, 128]
        prompt = onehot.unsqueeze(1).expand(-1, encoded.shape[1], -1)            # [B, T, 128]
        concat = torch.cat([encoded, prompt], dim=-1)
        encoded = self.prompt_kernel(concat).to(encoded.dtype)
        return encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next


def encoder_model_loader(model_name):
    """Load the NeMo model and return the streaming encoder wrapper."""
    asr_model = _load_nemo_model(model_name)
    encoder = asr_model.encoder
    encoder.eval()
    prompt_kernel = asr_model.prompt_kernel
    prompt_kernel.eval()

    att_context_size = get_att_context_size()
    if hasattr(encoder, "set_default_att_context_size"):
        encoder.set_default_att_context_size(att_context_size)

    wrapper = StreamingEncoderWrapper(encoder, prompt_kernel)
    wrapper.eval()
    return wrapper


def encoder_dummy_inputs(model):
    """Generate dummy inputs for ONNX export of the streaming encoder."""
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
        torch.zeros(batch, dtype=torch.int64),  # lang_id [B]
    )


# ---------------------------------------------------------------------------
# Decoder (stateful LSTM)
# ---------------------------------------------------------------------------

class StatefulDecoderWrapper(nn.Module):
    """Wrap the NeMo decoder to expose LSTM states as explicit I/O."""

    def __init__(self, dec):
        super().__init__()
        self.decoder = dec
        self.decoder._rnnt_export = True

    def forward(self, targets, h_in, c_in):
        g, states = self.decoder.predict(
            y=targets, state=(h_in, c_in), add_sos=False
        )
        h_out, c_out = states
        g = g.transpose(1, 2)  # [B, 1, D] -> [B, D, 1]
        return g, h_out, c_out


def decoder_model_loader(model_name):
    """Load the NeMo model and return the stateful decoder wrapper."""
    asr_model = _load_nemo_model(model_name)
    decoder = asr_model.decoder
    decoder.eval()

    wrapper = StatefulDecoderWrapper(decoder)
    wrapper.eval()
    return wrapper


def decoder_dummy_inputs(model):
    """Generate dummy inputs for ONNX export of the stateful decoder."""
    batch = 1
    return (
        torch.zeros(batch, 1, dtype=torch.int64),
        torch.zeros(DECODER_LSTM_LAYERS, batch, DECODER_HIDDEN, dtype=torch.float32),
        torch.zeros(DECODER_LSTM_LAYERS, batch, DECODER_HIDDEN, dtype=torch.float32),
    )


# ---------------------------------------------------------------------------
# Joint network
# ---------------------------------------------------------------------------

class JointWrapper(nn.Module):
    """Wrap the NeMo RNNTJoint so torch.onnx.export can trace it."""

    def __init__(self, j):
        super().__init__()
        self.joint = j

    def forward(self, encoder_output, decoder_output):
        return self.joint.joint(encoder_output, decoder_output)


def joint_model_loader(model_name):
    """Load the NeMo model and return the joint network wrapper."""
    asr_model = _load_nemo_model(model_name)
    joint = asr_model.joint
    joint.eval()

    wrapper = JointWrapper(joint)
    wrapper.eval()
    return wrapper


def joint_dummy_inputs(model):
    """Generate dummy inputs for ONNX export of the joint network."""
    batch = 1
    return (
        torch.randn(batch, 1, D_MODEL),
        torch.randn(batch, 1, DECODER_HIDDEN),
    )


# ---------------------------------------------------------------------------
# Russian-only encoder (lang_id=11 baked in, no lang_id input)
# ---------------------------------------------------------------------------

# Russian language ID in the NeMo prompt table
RU_LANG_ID = 11


class StreamingEncoderWrapperRU(nn.Module):
    """Streaming encoder with Russian language prompt baked in.

    Identical to StreamingEncoderWrapper but hardcodes lang_id=11 (Russian)
    and removes the lang_id input from the ONNX graph.
    """

    def __init__(self, enc, prompt_kernel):
        super().__init__()
        self.enc = enc
        self.prompt_kernel = prompt_kernel
        # Pre-build Russian one-hot prompt: [1, NUM_PROMPTS]
        self.register_buffer(
            "ru_onehot",
            F.one_hot(torch.tensor([RU_LANG_ID]), num_classes=NUM_PROMPTS).float(),
            persistent=False,
        )

    def forward(self, audio_signal, length,
                cache_last_channel, cache_last_time, cache_last_channel_len):
        audio_signal = audio_signal.transpose(1, 2)  # [B, T, mel] -> [B, mel, T]
        encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next = \
            self.enc.forward_for_export(
                audio_signal=audio_signal,
                length=length,
                cache_last_channel=cache_last_channel,
                cache_last_time=cache_last_time,
                cache_last_channel_len=cache_last_channel_len,
            )
        encoded = encoded.transpose(1, 2)  # [B, D, T] -> [B, T, D]
        # Expand Russian prompt to batch and time dimensions
        batch = encoded.shape[0]
        prompt = self.ru_onehot.expand(batch, encoded.shape[1], -1)  # [B, T, 128]
        concat = torch.cat([encoded, prompt], dim=-1)
        encoded = self.prompt_kernel(concat).to(encoded.dtype)
        return encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next


def encoder_model_loader_ru(model_name):
    """Load the NeMo model and return the Russian-only streaming encoder."""
    asr_model = _load_nemo_model(model_name)
    encoder = asr_model.encoder
    encoder.eval()
    prompt_kernel = asr_model.prompt_kernel
    prompt_kernel.eval()

    att_context_size = get_att_context_size()
    if hasattr(encoder, "set_default_att_context_size"):
        encoder.set_default_att_context_size(att_context_size)

    wrapper = StreamingEncoderWrapperRU(encoder, prompt_kernel)
    wrapper.eval()
    return wrapper


def encoder_dummy_inputs_ru(model):
    """Dummy inputs for Russian-only encoder (no lang_id)."""
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
    )
