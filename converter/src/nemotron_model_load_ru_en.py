
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

NEW_NUM_PROMPTS = 3
KEEP_LANG_IDS = [0, 1, 11]
# Map old lang_id -> new lang_id index
LANG_ID_MAP = {old: new for new, old in enumerate(KEEP_LANG_IDS)}
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
        encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next = \
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
