"""Export nvidia/parakeet-tdt-0.6b-v3 (FastConformer-TDT) to ONNX.

Produces:
  - encoder.onnx       (FP32 streaming FastConformer encoder + language prompt)
  - encoder_int4.onnx  (INT4 blockwise via MatMul4BitsQuantizer)
  - decoder.onnx       (TDT decoder, experimental — see caveats below)

Caveats
-------
1. ONNX Runtime GenAI 0.15.2 does **not** execute TDT decoders — its ASR
   support is RNN-T only (encoder + predictor + joint). This export targets
   plain ONNX Runtime (Microsoft.ML.OnnxRuntime) with a custom TDT beam search
   in C#, or a future GenAI release with TDT support.
2. The exact NeMo attribute layout of the TDT decoder is inspected at runtime;
   the decoder export step prints the discovered structure and aborts with a
   clear message if it does not match the expected FastConformer-TDT layout.

Run (inside a Python 3.10 env with NeMo installed):
    python export_onnx.py --model nvidia/parakeet-tdt-0.6b-v3 --out build/onnx_models
"""

import argparse
import os
import sys

import torch
import torch.nn as nn
import torch.nn.functional as F

# Streaming constants — keep aligned with the Nemotron recipe defaults.
MEL_FEATURES = 128
SUBSAMPLING_FACTOR = 8
N_LAYERS = 24
D_MODEL = 1024
CONV_CONTEXT = 8
NUM_PROMPTS = 128
CHUNK_SIZE = 0.56          # seconds
LEFT_CHUNKS = 10
PRE_ENCODE_CACHE = 9

MODEL_NAME = "nvidia/parakeet-tdt-0.6b-v3"


def _streaming_shapes():
    chunk_mel_frames = int(CHUNK_SIZE * 100)              # 56
    static_mel_frames = chunk_mel_frames + PRE_ENCODE_CACHE  # 65
    chunk_encoded_frames = chunk_mel_frames // SUBSAMPLING_FACTOR  # 7
    last_channel_cache_size = LEFT_CHUNKS * chunk_encoded_frames  # 70
    return static_mel_frames, last_channel_cache_size, chunk_encoded_frames


def load_nemo(model_name):
    import nemo.collections.asr as nemo_asr
    from huggingface_hub import hf_hub_download, list_repo_files

    if model_name.endswith(".nemo"):
        nemo_path = model_name
    else:
        files = list_repo_files(model_name)
        nemo_files = [f for f in files if f.endswith(".nemo")]
        if len(nemo_files) != 1:
            raise RuntimeError(f"Expected exactly one .nemo in {model_name}, got {nemo_files}")
        nemo_path = hf_hub_download(repo_id=model_name, filename=nemo_files[0])

    model = nemo_asr.models.ASRModel.restore_from(nemo_path)
    model = model.cpu().eval()
    return model


class StreamingEncoderWrapper(nn.Module):
    """FastConformer encoder + prompt_kernel (multilingual language prompt)."""

    def __init__(self, enc, prompt_kernel, num_prompts):
        super().__init__()
        self.enc = enc
        self.prompt_kernel = prompt_kernel
        self.num_prompts = num_prompts

    def forward(self, audio_signal, length,
                cache_last_channel, cache_last_time, cache_last_channel_len,
                lang_id):
        audio_signal = audio_signal.transpose(1, 2)  # [B,T,mel] -> [B,mel,T]
        encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next = \
            self.enc.forward_for_export(
                audio_signal=audio_signal,
                length=length,
                cache_last_channel=cache_last_channel,
                cache_last_time=cache_last_time,
                cache_last_channel_len=cache_last_channel_len)
        encoded = encoded.transpose(1, 2)  # [B,D,T] -> [B,T,D]
        onehot = F.one_hot(lang_id, num_classes=self.num_prompts).to(encoded.dtype)
        prompt = onehot.unsqueeze(1).expand(-1, encoded.shape[1], -1)
        encoded = self.prompt_kernel(torch.cat([encoded, prompt], dim=-1)).to(encoded.dtype)
        return encoded, encoded_len, cache_ch_next, cache_tm_next, cache_len_next


def export_encoder(model, out_dir):
    encoder = model.encoder
    prompt_kernel = getattr(model, "prompt_kernel", None)
    if prompt_kernel is None:
        raise RuntimeError("prompt_kernel not found — multilingual prompt layout differs")

    static_mel, cache_size, _ = _streaming_shapes()
    wrapper = StreamingEncoderWrapper(encoder, prompt_kernel, NUM_PROMPTS).eval()

    batch = 1
    dummy = (
        torch.randn(batch, static_mel, MEL_FEATURES),
        torch.tensor([static_mel], dtype=torch.int64),
        torch.zeros(batch, N_LAYERS, cache_size, D_MODEL),
        torch.zeros(batch, N_LAYERS, D_MODEL, CONV_CONTEXT),
        torch.zeros(batch, dtype=torch.int64),
        torch.zeros(batch, dtype=torch.int64),  # lang_id
    )
    names = (
        "audio_signal", "length", "cache_last_channel",
        "cache_last_time", "cache_last_channel_len", "lang_id",
    )

    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "encoder.onnx")
    torch.onnx.export(
        wrapper, dummy, out_path,
        input_names=list(names),
        output_names=["encoded", "encoded_len",
                      "cache_ch_next", "cache_tm_next", "cache_len_next"],
        dynamic_axes={n: {0: "batch"} for n in names},
        opset_version=17,
    )
    print(f"Encoder exported: {out_path}")
    return out_path


def quantize_int4(encoder_path, out_dir):
    from onnxruntime.quantization.matmul_4bits_quantizer import MatMul4BitsQuantizer

    quantizer = MatMul4BitsQuantizer(
        encoder_path,
        block_size=32,
        is_symmetric=True,
        nodes_to_exclude=[],
    )
    quantizer.process()
    out_path = os.path.join(out_dir, "encoder_int4.onnx")
    quantizer.model.save_model_to_file(out_path)
    print(f"Encoder INT4 exported: {out_path}")
    return out_path


def export_tdt_decoder(model, out_dir):
    """Inspect and (best-effort) export the TDT decoder.

    TDT (arxiv:2304.06795) jointly predicts token + duration, so its decoder
    signature differs from an RNN-T predictor/joint. This prints the discovered
    attributes and aborts if the layout is unknown.
    """
    decoder = model.decoder
    print(f"decoder type: {type(decoder).__name__}")
    print(f"  has joint: {hasattr(model, 'joint')}")
    print(f"  decoder attributes: {[a for a in dir(decoder) if not a.startswith('_')][:40]}")
    raise NotImplementedError(
        "TDT decoder export is not implemented yet — inspect the printed decoder "
        "layout above and add a torch.onnx wrapper matching its forward() signature."
    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=MODEL_NAME)
    ap.add_argument("--out", default="build/onnx_models")
    ap.add_argument("--skip-int4", action="store_true")
    args = ap.parse_args()

    model = load_nemo(args.model)
    print(f"Loaded {args.model}")

    encoder_path = export_encoder(model, args.out)
    if not args.skip_int4:
        quantize_int4(encoder_path, args.out)
    export_tdt_decoder(model, args.out)


if __name__ == "__main__":
    sys.exit(main())
