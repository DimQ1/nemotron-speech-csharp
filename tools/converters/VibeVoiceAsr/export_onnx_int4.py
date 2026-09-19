#!/usr/bin/env python3
"""Export the VibeVoice streaming checkpoint to ONNX with an INT4 decoder.

The speech tokenizers are exported as one fixed-window FP32 graph because
ONNX Runtime's MatMulNBits quantizer does not quantize their convolutional
operators. The Qwen decoder is split into prompt-prefill, audio-chunk, and
token-step graphs, then quantized with RTN MatMulNBits INT4.
"""

from __future__ import annotations

import argparse
import gc
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Iterable

import numpy as np
import onnx
import torch
from onnx import numpy_helper
from onnxruntime.quantization.matmul_nbits_quantizer import (
    MatMulNBitsQuantizer,
    RTNWeightOnlyQuantConfig,
)
from onnxruntime.quantization.quant_utils import QuantFormat
from transformers import DynamicCache

from vibevoice.modular.modeling_vibevoice_asr import (
    VibeVoiceASRForConditionalGeneration,
)
from vibevoice.processor.vibevoice_asr_processor import VibeVoiceASRProcessor


SAMPLE_RATE = 24_000
FRAME_SAMPLES = 3_200
CHUNK_FRAMES = 22
LOOKAHEAD_FRAMES = 4
WINDOW_SAMPLES = (CHUNK_FRAMES + LOOKAHEAD_FRAMES) * FRAME_SAMPLES
FEATURE_FRAMES = CHUNK_FRAMES + LOOKAHEAD_FRAMES


def _streaming_prompt() -> str:
    return (
        "You are a helpful assistant that transcribes audio input into text output. "
        "Please transcribe the following audios streamingly with these keys: speaker, content\n"
    )


class SpeechFeatureWrapper(torch.nn.Module):
    def __init__(self, model: VibeVoiceASRForConditionalGeneration) -> None:
        super().__init__()
        self.acoustic_tokenizer = model.model.acoustic_tokenizer
        self.semantic_tokenizer = model.model.semantic_tokenizer
        self.acoustic_connector = model.model.acoustic_connector
        self.semantic_connector = model.model.semantic_connector

    def forward(self, audio: torch.Tensor) -> torch.Tensor:
        acoustic_mean = self.acoustic_tokenizer.encode(audio).mean
        semantic_mean = self.semantic_tokenizer.encode(audio).mean
        return self.acoustic_connector(acoustic_mean) + self.semantic_connector(semantic_mean)


def _cache_pairs(flat_cache: Iterable[torch.Tensor]) -> tuple[tuple[torch.Tensor, torch.Tensor], ...]:
    values = tuple(flat_cache)
    return tuple((values[index], values[index + 1]) for index in range(0, len(values), 2))


def _flatten_cache(cache: DynamicCache) -> tuple[torch.Tensor, ...]:
    return tuple(value for layer in cache for value in layer)


class DecoderPrefillWrapper(torch.nn.Module):
    def __init__(self, model: VibeVoiceASRForConditionalGeneration) -> None:
        super().__init__()
        self.language_model = model.model.language_model
        self.lm_head = model.lm_head

    def forward(
        self,
        inputs_embeds: torch.Tensor,
        position_ids: torch.Tensor,
        attention_mask: torch.Tensor,
    ) -> tuple[torch.Tensor, ...]:
        outputs = self.language_model(
            inputs_embeds=inputs_embeds,
            position_ids=position_ids,
            attention_mask=attention_mask,
            use_cache=True,
            return_dict=True,
        )
        logits = self.lm_head(outputs.last_hidden_state[:, -1:, :])
        return (logits, *_flatten_cache(outputs.past_key_values))


class DecoderStepWrapper(torch.nn.Module):
    def __init__(self, model: VibeVoiceASRForConditionalGeneration) -> None:
        super().__init__()
        self.language_model = model.model.language_model
        self.lm_head = model.lm_head

    def forward(
        self,
        inputs_embeds: torch.Tensor,
        position_ids: torch.Tensor,
        attention_mask: torch.Tensor,
        *flat_cache: torch.Tensor,
    ) -> tuple[torch.Tensor, ...]:
        cache = DynamicCache.from_legacy_cache(_cache_pairs(flat_cache))
        outputs = self.language_model(
            inputs_embeds=inputs_embeds,
            position_ids=position_ids,
            attention_mask=attention_mask,
            past_key_values=cache,
            use_cache=True,
            return_dict=True,
        )
        logits = self.lm_head(outputs.last_hidden_state[:, -1:, :])
        return (logits, *_flatten_cache(outputs.past_key_values))


class DecoderAudioChunkWrapper(DecoderStepWrapper):
    """Trace the multi-token audio insertion call separately from token steps."""


def _export(
    module: torch.nn.Module,
    args: tuple[torch.Tensor, ...],
    path: Path,
    input_names: list[str],
    output_names: list[str],
    dynamic_axes: dict[str, dict[int, str]],
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        module,
        args,
        str(path),
        opset_version=18,
        input_names=input_names,
        output_names=output_names,
        dynamic_axes=dynamic_axes,
        dynamo=False,
        do_constant_folding=False,
        external_data=True,
    )


def _causal_mask(
    query_length: int,
    past_length: int,
    dtype: torch.dtype,
    device: torch.device,
) -> torch.Tensor:
    key_positions = torch.arange(
        past_length + query_length,
        dtype=torch.long,
        device=device,
    )
    query_positions = torch.arange(
        past_length,
        past_length + query_length,
        dtype=torch.long,
        device=device,
    )
    allowed = key_positions.unsqueeze(0) <= query_positions.unsqueeze(1)
    mask = torch.zeros(
        (1, 1, query_length, past_length + query_length),
        dtype=dtype,
        device=device,
    )
    return mask.masked_fill(~allowed.unsqueeze(0).unsqueeze(0), torch.finfo(dtype).min)


def _quantize(source: Path, target: Path, block_size: int, accuracy_level: int) -> None:
    target.unlink(missing_ok=True)
    Path(f"{target}.data").unlink(missing_ok=True)
    model = onnx.load(str(source), load_external_data=False)
    folded_dir = _fold_weight_transposes(model, source.parent)
    original_graph = source.read_bytes()
    config_path = source.parent / "config.json"
    hidden_config = source.parent / ".vibevoice-quant-config.json"
    config_hidden = False
    try:
        # The ORT neural-compressor wrapper probes config.json beside the
        # model. VibeVoice is a custom architecture, so hide that one file
        # while quantizing the graph in its original external-data directory.
        if config_path.is_file():
            config_path.replace(hidden_config)
            config_hidden = True

        onnx.save_model(model, str(source))
        quantizer = MatMulNBitsQuantizer(
            model=str(source),
            bits=4,
            block_size=block_size,
            is_symmetric=False,
            accuracy_level=accuracy_level,
            algo_config=RTNWeightOnlyQuantConfig(quant_format=QuantFormat.QOperator),
        )
        quantizer.process()
        _remove_unused_initializers(quantizer.model.model)
        quantizer.model.save_model_to_file(str(target), use_external_data_format=True)
        portable_model = onnx.load(str(target), load_external_data=False)
        if not any(item.domain == "com.microsoft" for item in portable_model.opset_import):
            opset = portable_model.opset_import.add()
            opset.domain = "com.microsoft"
            opset.version = 1
            onnx.save_model(portable_model, str(target))
        onnx.checker.check_model(str(target))
    finally:
        source.write_bytes(original_graph)
        if config_hidden:
            hidden_config.replace(config_path)
        shutil.rmtree(folded_dir, ignore_errors=True)


def _remove_unused_initializers(model: onnx.ModelProto) -> None:
    """Drop original weights replaced by MatMulNBits quantized tensors."""
    used = {input_name for node in model.graph.node for input_name in node.input}
    kept = [initializer for initializer in model.graph.initializer if initializer.name in used]
    del model.graph.initializer[:]
    model.graph.initializer.extend(kept)


def _fold_weight_transposes(model: onnx.ModelProto, base_dir: Path) -> Path:
    """Expose transposed Linear weights as direct MatMul initializers.

    TorchScript ONNX export represents ``nn.Linear`` as ``MatMul(x,
    Transpose(weight))``. MatMulNBits only recognizes the initializer when it
    is the second MatMul input, so fold only constant transposes before RTN.
    """
    stale_folded = [
        item for item in model.graph.initializer if item.name.endswith("__folded_transpose")
    ]
    for item in stale_folded:
        model.graph.initializer.remove(item)

    initializers = {item.name: item for item in model.graph.initializer}
    folded_dir = base_dir / ".vibevoice_folded"
    shutil.rmtree(folded_dir, ignore_errors=True)
    folded_dir.mkdir(parents=True, exist_ok=True)
    remove: list[onnx.NodeProto] = []

    for node in model.graph.node:
        if node.op_type != "Transpose" or not node.input or node.input[0] not in initializers:
            continue

        source = initializers[node.input[0]]
        original_location = [(entry.key, entry.value) for entry in source.external_data]
        original_data_location = source.data_location
        was_external = source.data_location == onnx.TensorProto.EXTERNAL
        source_array = numpy_helper.to_array(source, base_dir=str(base_dir))
        try:
            perm_attr = next((attr for attr in node.attribute if attr.name == "perm"), None)
            permutation = tuple(perm_attr.ints) if perm_attr is not None else tuple(reversed(range(source_array.ndim)))
            folded_name = f"{source.name}__folded_transpose"
            if folded_name not in initializers:
                folded_array = np.ascontiguousarray(source_array.transpose(permutation))
                if was_external:
                    folded_path = folded_dir / f"{folded_name}.bin"
                    folded_array.tofile(folded_path)
                    folded_tensor = onnx.TensorProto()
                    folded_tensor.name = folded_name
                    folded_tensor.data_type = source.data_type
                    folded_tensor.dims.extend(folded_array.shape)
                    folded_tensor.data_location = onnx.TensorProto.EXTERNAL
                    for key, value in (
                        ("location", folded_path.relative_to(base_dir).as_posix()),
                        ("offset", "0"),
                        ("length", str(folded_array.nbytes)),
                    ):
                        entry = folded_tensor.external_data.add()
                        entry.key = key
                        entry.value = value
                else:
                    folded_tensor = numpy_helper.from_array(folded_array, name=folded_name)
                model.graph.initializer.append(folded_tensor)
                initializers[folded_name] = folded_tensor

            if was_external:
                source.ClearField("raw_data")
                source.data_location = original_data_location
                del source.external_data[:]
                for key, value in original_location:
                    entry = source.external_data.add()
                    entry.key = key
                    entry.value = value
        finally:
            del source_array

        for consumer in model.graph.node:
            for index, input_name in enumerate(consumer.input):
                if input_name == node.output[0]:
                    consumer.input[index] = folded_name
        remove.append(node)

    if remove:
        kept = [node for node in model.graph.node if node not in remove]
        del model.graph.node[:]
        model.graph.node.extend(kept)

    return folded_dir


def _copy_external_data(
    model: onnx.ModelProto,
    source_dir: Path,
    target_dir: Path,
) -> None:
    """Copy external tensors referenced by a graph into its output folder."""
    copied: set[str] = set()
    for tensor in model.graph.initializer:
        if tensor.data_location != onnx.TensorProto.EXTERNAL:
            continue
        info = {entry.key: entry.value for entry in tensor.external_data}
        location = info.get("location")
        if not location:
            continue
        source_file = (source_dir / location).resolve()
        target_name = Path(location).name
        target_file = target_dir / target_name
        if target_name not in copied:
            if not source_file.is_file():
                raise FileNotFoundError(f"External tensor data not found: {source_file}")
            if source_file != target_file:
                target_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source_file, target_file)
            copied.add(target_name)

        for entry in tensor.external_data:
            if entry.key == "location":
                entry.value = target_name



def _copy_runtime_files(source: Path, output: Path) -> None:
    names = {
        "config.json",
        "preprocessor_config.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "added_tokens.json",
        "vocab.json",
        "merges.txt",
    }
    for name in names:
        source_file = source / name
        if source_file.exists():
            shutil.copy2(source_file, output / name)


def _quantize_package(output: Path, block_size: int, accuracy_level: int) -> None:
    for name in ("decoder_prefill", "decoder_audio", "decoder_step"):
        subprocess.run(
            [
                sys.executable,
                str(Path(__file__).resolve()),
                "--quantize-one",
                name,
                "--output",
                str(output),
                "--block-size",
                str(block_size),
                "--accuracy-level",
                str(accuracy_level),
            ],
            check=True,
        )


def _quantize_one(output: Path, name: str, block_size: int, accuracy_level: int) -> None:
    source_graph = output / f"{name}.onnx"
    target_graph = output / f"{name}.int4.onnx"
    print(f"Quantizing {source_graph}")
    _quantize(source_graph, target_graph, block_size, accuracy_level)


def _write_manifest(output: Path, model: VibeVoiceASRForConditionalGeneration) -> None:
    decoder_config = model.config.decoder_config
    manifest = {
        "model_type": "vibevoice_asr_onnx_streaming",
        "sample_rate": SAMPLE_RATE,
        "frame_samples": FRAME_SAMPLES,
        "chunk_frames": CHUNK_FRAMES,
        "lookahead_frames": LOOKAHEAD_FRAMES,
        "window_samples": WINDOW_SAMPLES,
        "feature_frames": FEATURE_FRAMES,
        "speech_features": "speech_features.onnx",
        "decoder_prefill": "decoder_prefill.int4.onnx",
        "decoder_audio": "decoder_audio.int4.onnx",
        "decoder_step": "decoder_step.int4.onnx",
        "embed_tokens": "embed_tokens.float32.bin",
        "hidden_size": decoder_config.hidden_size,
        "num_hidden_layers": decoder_config.num_hidden_layers,
        "num_key_value_heads": decoder_config.num_key_value_heads,
        "num_attention_heads": decoder_config.num_attention_heads,
        "head_dim": decoder_config.hidden_size // decoder_config.num_attention_heads,
        "vocab_size": decoder_config.vocab_size,
        "quantization": "RTN MatMulNBits INT4; speech encoder FP32",
    }
    (output / "vibevoice_onnx_config.json").write_text(
        json.dumps(manifest, indent=2) + "\n",
        encoding="utf-8",
    )


def _decoder_names(prefix: str, layer_count: int) -> list[str]:
    names = ["logits"]
    for layer in range(layer_count):
        names.extend((f"{prefix}_key_{layer}", f"{prefix}_value_{layer}"))
    return names


def export(args: argparse.Namespace) -> None:
    source = args.source.resolve()
    output = args.output.resolve()
    if not source.is_dir():
        raise FileNotFoundError(f"Checkpoint directory not found: {source}")
    if output.exists() and any(output.iterdir()) and not args.force:
        raise FileExistsError(f"Output is not empty; pass --force: {output}")
    output.mkdir(parents=True, exist_ok=True)

    torch.backends.cudnn.enabled = False
    device = torch.device(args.device)
    dtype = torch.float32 if args.dtype == "float32" else torch.bfloat16
    model = VibeVoiceASRForConditionalGeneration.from_pretrained(
        str(source), dtype=dtype, attn_implementation="sdpa"
    ).to(device).eval()

    _copy_runtime_files(source, output)
    embeddings = model.get_input_embeddings().weight.detach().cpu().float().numpy()
    embeddings.tofile(output / "embed_tokens.float32.bin")
    del embeddings

    feature_wrapper = SpeechFeatureWrapper(model).eval()
    audio = torch.zeros((1, 1, WINDOW_SAMPLES), dtype=dtype, device=device)
    feature_path = output / "speech_features.onnx"
    print(f"Exporting {feature_path}")
    _export(
        feature_wrapper,
        (audio,),
        feature_path,
        ["audio"],
        ["features"],
        {"audio": {2: "audio_samples"}, "features": {1: "feature_frames"}},
    )

    decoder_config = model.config.decoder_config
    hidden_size = decoder_config.hidden_size
    layer_count = decoder_config.num_hidden_layers
    kv_heads = decoder_config.num_key_value_heads
    head_dim = hidden_size // decoder_config.num_attention_heads
    processor = VibeVoiceASRProcessor.from_pretrained(str(source))
    prefill_length = len(processor.tokenizer.encode(_streaming_prompt(), add_special_tokens=False))
    del processor
    prefill = DecoderPrefillWrapper(model).eval()
    prefill_embeds = torch.zeros((1, prefill_length, hidden_size), dtype=dtype, device=device)
    prefill_positions = torch.arange(prefill_length, dtype=torch.long, device=device).unsqueeze(0)
    prefill_inputs = ["inputs_embeds", "position_ids", "attention_mask"]
    prefill_outputs = _decoder_names("present", layer_count)
    prefill_dynamic = {
        "inputs_embeds": {1: "sequence"},
        "position_ids": {1: "sequence"},
        "attention_mask": {2: "sequence", 3: "sequence"},
    }
    prefill_dynamic.update({name: {2: "sequence"} for name in prefill_outputs[1:]})
    print(f"Exporting {output / 'decoder_prefill.onnx'}")
    _export(
        prefill,
        (
            prefill_embeds,
            prefill_positions,
            _causal_mask(prefill_length, 0, dtype, device),
        ),
        output / "decoder_prefill.onnx",
        prefill_inputs,
        prefill_outputs,
        prefill_dynamic,
    )

    step = DecoderStepWrapper(model).eval()
    step_length = prefill_length
    step_embeds = torch.zeros((1, 1, hidden_size), dtype=dtype, device=device)
    step_positions = torch.tensor([[step_length]], dtype=torch.long, device=device)
    past = tuple(
        torch.zeros((1, kv_heads, step_length, head_dim), dtype=dtype, device=device)
        for _ in range(layer_count * 2)
    )
    step_inputs = ["inputs_embeds", "position_ids", "attention_mask"]
    step_inputs.extend(
        name
        for layer in range(layer_count)
        for name in (f"past_key_{layer}", f"past_value_{layer}")
    )
    step_outputs = _decoder_names("present", layer_count)
    step_dynamic = {
        "inputs_embeds": {1: "input_sequence"},
        "position_ids": {1: "input_sequence"},
        "attention_mask": {2: "query_sequence", 3: "key_sequence"},
    }
    step_dynamic.update({name: {2: "past_sequence"} for name in step_inputs[3:]})
    step_dynamic.update({name: {2: "present_sequence"} for name in step_outputs[1:]})
    print(f"Exporting {output / 'decoder_step.onnx'}")
    _export(
        step,
        (
            step_embeds,
            step_positions,
            _causal_mask(1, step_length, dtype, device),
            *past,
        ),
        output / "decoder_step.onnx",
        step_inputs,
        step_outputs,
        step_dynamic,
    )

    audio = DecoderAudioChunkWrapper(model).eval()
    audio_length = FEATURE_FRAMES + 2
    audio_embeds = torch.zeros((1, audio_length, hidden_size), dtype=dtype, device=device)
    audio_positions = torch.arange(
        step_length,
        step_length + audio_length,
        dtype=torch.long,
        device=device,
    ).unsqueeze(0)
    audio_dynamic = {
        "inputs_embeds": {1: "audio_sequence"},
        "position_ids": {1: "audio_sequence"},
        "attention_mask": {2: "query_sequence", 3: "key_sequence"},
    }
    audio_dynamic.update({name: {2: "past_sequence"} for name in step_inputs[3:]})
    audio_dynamic.update({name: {2: "present_sequence"} for name in step_outputs[1:]})
    print(f"Exporting {output / 'decoder_audio.onnx'}")
    _export(
        audio,
        (
            audio_embeds,
            audio_positions,
            _causal_mask(audio_length, step_length, dtype, device),
            *past,
        ),
        output / "decoder_audio.onnx",
        step_inputs,
        step_outputs,
        audio_dynamic,
    )

    _write_manifest(output, model)

    if not args.no_quantize:
        del feature_wrapper, prefill, step, audio, past
        del audio_embeds, audio_positions, prefill_embeds, prefill_positions
        del step_embeds, step_positions, model
        gc.collect()
        if device.type == "cuda":
            torch.cuda.empty_cache()
        os.execv(
            sys.executable,
            [
                sys.executable,
                str(Path(__file__).resolve()),
                "--quantize-only",
                "--source",
                str(source),
                "--output",
                str(output),
                "--block-size",
                str(args.block_size),
                "--accuracy-level",
                str(args.accuracy_level),
            ],
        )

    print(f"Output: {output}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cpu")
    parser.add_argument("--dtype", choices=("float32", "bfloat16"), default="float32")
    parser.add_argument("--block-size", type=int, choices=(32, 64, 128), default=64)
    parser.add_argument("--accuracy-level", type=int, choices=(0, 1, 2, 3, 4), default=4)
    parser.add_argument("--no-quantize", action="store_true")
    parser.add_argument("--quantize-only", action="store_true")
    parser.add_argument("--quantize-one", choices=("decoder_prefill", "decoder_audio", "decoder_step"))
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()
    if args.device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA was requested but is not available")
    if args.quantize_only:
        if args.output is None:
            raise ValueError("--output is required with --quantize-only")
        _quantize_package(args.output.resolve(), args.block_size, args.accuracy_level)
        print(f"Output: {args.output.resolve()}")
        return
    if args.quantize_one:
        _quantize_one(args.output.resolve(), args.quantize_one, args.block_size, args.accuracy_level)
        return
    if args.source is None:
        raise ValueError("--source is required unless --quantize-only is used")
    export(args)


if __name__ == "__main__":
    main()