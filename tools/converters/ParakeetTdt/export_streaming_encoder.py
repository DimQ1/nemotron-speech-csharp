"""Export a cache-aware streaming encoder for nvidia/parakeet-tdt-0.6b-v3.

Unlike the onnx-asr full-length encoder, this export wraps the FastConformer
encoder's ``forward_for_export()`` to expose channel/time caches, so audio is
encoded incrementally (chunk by chunk) with a sliding attention context. This
is the encoder required for fully incremental (low-latency) streaming, in
contrast to the segment-buffered approach in `ParakeetTdtRecognizer`.

Produces: `encoder-streaming.onnx` with inputs
  audio_signal [B, static_mel, 128], length [B],
  cache_last_channel [B, 24, cache_size, 1024],
  cache_last_time [B, 24, 1024, 8], cache_last_channel_len [B]
and outputs
  encoded [B, T, 1024], encoded_len, cache_ch_next, cache_tm_next, cache_len_next.

Note: parakeet-tdt-0.6b-v3 auto-detects language, so unlike Nemotron there is
no ``lang_id`` / ``prompt_kernel`` input.

Run in a NeMo environment (see `.github/workflows/convert-parakeet-tdt.yml`):
    python export_streaming_encoder.py --model nvidia/parakeet-tdt-0.6b-v3 --out build/onnx_models
"""

import argparse
import os

import torch
import torch.nn as nn

MEL_FEATURES = 128
N_LAYERS = 24
D_MODEL = 1024
CONV_CONTEXT = 8          # conv_kernel_size(9) - 1
SUBSAMPLING_FACTOR = 8
CHUNK_SIZE = 0.56         # seconds
LEFT_CHUNKS = 10
PRE_ENCODE_CACHE = 9

MODEL_NAME = "nvidia/parakeet-tdt-0.6b-v3"


class StreamingEncoderWrapper(nn.Module):
    """Cache-aware FastConformer encoder (no language prompt)."""

    def __init__(self, enc):
        super().__init__()
        self.enc = enc

    def forward(self, audio_signal, length,
                cache_last_channel, cache_last_time, cache_last_channel_len):
        audio_signal = audio_signal.transpose(1, 2)  # [B,T,mel] -> [B,mel,T]
        encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next = \
            self.enc.forward_for_export(
                audio_signal=audio_signal,
                length=length,
                cache_last_channel=cache_last_channel,
                cache_last_time=cache_last_time,
                cache_last_channel_len=cache_last_channel_len,
            )
        encoded = encoded.transpose(1, 2)  # [B,D,T] -> [B,T,D]
        return encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=MODEL_NAME)
    ap.add_argument("--out", default="build/onnx_models")
    args = ap.parse_args()

    import nemo.collections.asr as nemo_asr
    from huggingface_hub import hf_hub_download, list_repo_files

    files = list_repo_files(args.model)
    nemo_files = [f for f in files if f.endswith(".nemo")]
    if len(nemo_files) != 1:
        raise RuntimeError(f"Expected one .nemo in {args.model}, got {nemo_files}")
    nemo_path = hf_hub_download(repo_id=args.model, filename=nemo_files[0])
    model = nemo_asr.models.ASRModel.restore_from(nemo_path).cpu().eval()
    encoder = model.encoder

    right_context = {0.08: 0, 0.16: 1, 0.56: 6, 1.12: 13}[CHUNK_SIZE]
    chunk_encoded_frames = int(CHUNK_SIZE * 100) // SUBSAMPLING_FACTOR
    left_context = LEFT_CHUNKS * chunk_encoded_frames
    if hasattr(encoder, "set_default_att_context_size"):
        encoder.set_default_att_context_size([left_context, right_context])

    wrapper = StreamingEncoderWrapper(encoder).eval()

    static_mel_frames = int(CHUNK_SIZE * 100) + PRE_ENCODE_CACHE  # 65
    cache_size = LEFT_CHUNKS * chunk_encoded_frames                # 70
    batch = 1

    dummy = (
        torch.randn(batch, static_mel_frames, MEL_FEATURES),
        torch.tensor([static_mel_frames], dtype=torch.int64),
        torch.zeros(batch, N_LAYERS, cache_size, D_MODEL),
        torch.zeros(batch, N_LAYERS, D_MODEL, CONV_CONTEXT),
        torch.zeros(batch, dtype=torch.int64),
    )
    names = (
        "audio_signal", "length", "cache_last_channel",
        "cache_last_time", "cache_last_channel_len",
    )

    os.makedirs(args.out, exist_ok=True)
    out_path = os.path.join(args.out, "encoder-streaming.onnx")
    torch.onnx.export(
        wrapper, dummy, out_path,
        input_names=list(names),
        output_names=["encoded", "encoded_len",
                      "cache_ch_next", "cache_tm_next", "cache_len_next"],
        dynamic_axes={n: {0: "batch"} for n in names},
        opset_version=17,
    )
    print(f"Saved {out_path}")


if __name__ == "__main__":
    main()
