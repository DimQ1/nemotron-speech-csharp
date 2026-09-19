#!/usr/bin/env python3
"""Build a Qwen3-ASR INT4 decoder package with ONNX Runtime RTN.

The Qwen3-ASR export is split into an audio encoder and two decoder graphs.
This tool quantizes both decoder graphs to MatMulNBits INT4 and keeps the
encoder in FP32 for CPU performance.  The output encoder is named
``encoder.int4.onnx`` for compatibility with the C# provider, but the report
and generated config explicitly identify its FP32 storage.

Usage:
    python quantize_int4.py \
        --input output/qwen3-asr-1.7b \
        --output output/qwen3-asr-1.7b-int4 \
        --block-size 64 --accuracy-level 4

The input directory must contain the FP32 ``encoder.onnx``,
``decoder_init.onnx`` and ``decoder_step.onnx`` export plus its tokenizer and
``embed_tokens.bin``.  The output is compatible with SpeechLib's Qwen3
provider and uses one shared ``decoder_weights.int4.data`` file.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import mmap
import os
import shutil
from pathlib import Path

import onnx
from onnxruntime.quantization.matmul_nbits_quantizer import (
    MatMulNBitsQuantizer,
    RTNWeightOnlyQuantConfig,
)
from onnxruntime.quantization.quant_utils import QuantFormat


DECODER_NAMES = ("decoder_init", "decoder_step")
REQUIRED_INPUTS = ("encoder.onnx", "decoder_init.onnx", "decoder_step.onnx")
GENERATED_FILES = {
    "decoder_init.int4.onnx",
    "decoder_init.int4.onnx.data",
    "decoder_step.int4.onnx",
    "decoder_step.int4.onnx.data",
    "decoder_weights.int4.data",
    "encoder.int4.onnx",
    "encoder.int4.onnx.data",
}


def total_size(path: Path) -> int:
    """Return the model size including a sibling external-data file."""
    size = path.stat().st_size
    data_path = Path(str(path) + ".data")
    if data_path.exists():
        size += data_path.stat().st_size
    return size


def quantize_decoder(source: Path, target: Path, block_size: int, accuracy_level: int) -> None:
    """Quantize one decoder graph with round-to-nearest MatMulNBits."""
    print(f"  Quantizing {source.name} -> {target.name}")
    model = onnx.load(str(source), load_external_data=True)
    quantizer = MatMulNBitsQuantizer(
        model=model,
        bits=4,
        block_size=block_size,
        is_symmetric=False,
        accuracy_level=accuracy_level,
        algo_config=RTNWeightOnlyQuantConfig(quant_format=QuantFormat.QOperator),
    )
    quantizer.process()
    quantizer.model.save_model_to_file(str(target), use_external_data_format=True)
    print(f"    {total_size(target) / 1e9:.3f} GB")


def external_info(tensor: onnx.TensorProto) -> dict[str, str]:
    return {entry.key: entry.value for entry in tensor.external_data}


def set_external(tensor: onnx.TensorProto, location: str, offset: int, length: int) -> None:
    del tensor.external_data[:]
    tensor.data_location = onnx.TensorProto.EXTERNAL
    for key, value in (
        ("location", location),
        ("offset", str(offset)),
        ("length", str(length)),
    ):
        entry = tensor.external_data.add()
        entry.key = key
        entry.value = value


def inline_tensor(tensor: onnx.TensorProto, data: bytes) -> None:
    del tensor.external_data[:]
    tensor.data_location = onnx.TensorProto.DEFAULT
    tensor.raw_data = data


def hash_region(mapped: mmap.mmap, offset: int, length: int) -> str:
    return hashlib.sha256(mapped[offset : offset + length]).hexdigest()


def share_decoder_weights(output_dir: Path) -> None:
    """Deduplicate the split decoder data files into one shared file."""
    init_path = output_dir / "decoder_init.int4.onnx"
    step_path = output_dir / "decoder_step.int4.onnx"
    init_data = Path(str(init_path) + ".data")
    step_data = Path(str(step_path) + ".data")
    shared_data = output_dir / "decoder_weights.int4.data"

    if shared_data.exists() and not init_data.exists() and not step_data.exists():
        print(f"  Shared decoder data already exists: {shared_data.name}")
        return
    if not init_data.exists() or not step_data.exists():
        raise FileNotFoundError(
            "Both decoder external-data files are required before sharing: "
            f"{init_data.name}, {step_data.name}"
        )

    init_model = onnx.load(str(init_path), load_external_data=False)
    step_model = onnx.load(str(step_path), load_external_data=False)

    with init_data.open("rb") as init_file, mmap.mmap(init_file.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
        index: dict[str, tuple[int, int]] = {}
        for tensor in init_model.graph.initializer:
            if not tensor.external_data:
                continue
            info = external_info(tensor)
            offset = int(info.get("offset", "0"))
            length = int(info["length"])
            index[hash_region(mapped, offset, length)] = (offset, length)

    matched = 0
    inlined = 0
    with step_data.open("rb") as step_file, mmap.mmap(step_file.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
        for tensor in step_model.graph.initializer:
            if not tensor.external_data:
                continue
            info = external_info(tensor)
            offset = int(info.get("offset", "0"))
            length = int(info["length"])
            match = index.get(hash_region(mapped, offset, length))
            if match is None:
                inline_tensor(tensor, mapped[offset : offset + length])
                inlined += 1
            else:
                set_external(tensor, shared_data.name, match[0], match[1])
                matched += 1

    for tensor in init_model.graph.initializer:
        if not tensor.external_data:
            continue
        for entry in tensor.external_data:
            if entry.key == "location":
                entry.value = shared_data.name

    os.replace(init_data, shared_data)
    init_path.write_bytes(init_model.SerializeToString())
    step_path.write_bytes(step_model.SerializeToString())
    step_data.unlink()
    print(f"  Shared decoder weights: {shared_data.name} ({matched} matched, {inlined} inlined)")


def copy_support_files(input_dir: Path, output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    for source in input_dir.iterdir():
        if not source.is_file():
            continue
        if source.name in REQUIRED_INPUTS or source.name in GENERATED_FILES or source.name in {
            "decoder_init.onnx.data",
            "decoder_step.onnx.data",
            "encoder.onnx.data",
            "decoder_weights.data",
        }:
            continue
        shutil.copy2(source, output_dir / source.name)

    encoder_source = input_dir / "encoder.onnx"
    encoder_target = output_dir / "encoder.int4.onnx"
    encoder_data_source = Path(f"{encoder_source}.data")
    if not encoder_data_source.exists():
        shutil.copy2(encoder_source, encoder_target)
        return

    encoder_model = onnx.load(str(encoder_source), load_external_data=False)
    for tensor in encoder_model.graph.initializer:
        for entry in tensor.external_data:
            if entry.key == "location":
                entry.value = f"{encoder_target.name}.data"
    onnx.save_model(encoder_model, str(encoder_target))
    shutil.copy2(encoder_data_source, Path(f"{encoder_target}.data"))


def write_config(input_dir: Path, output_dir: Path, block_size: int, accuracy_level: int) -> None:
    config_path = input_dir / "config.json"
    if not config_path.exists():
        return

    config = json.loads(config_path.read_text(encoding="utf-8"))
    quantization = config.get("quantization", {})
    if not isinstance(quantization, dict):
        quantization = {}
    tag = f"int4_rtn_block{block_size}_al{accuracy_level}"
    quantization["decoder_init"] = tag
    quantization["decoder_step"] = tag
    config["quantization"] = quantization
    config["int4_encoder_storage"] = "fp32"
    (output_dir / "config.json").write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")


def validate_input(input_dir: Path) -> None:
    missing = [name for name in REQUIRED_INPUTS if not (input_dir / name).exists()]
    if missing:
        raise FileNotFoundError(f"Missing FP32 export files in {input_dir}: {', '.join(missing)}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Create a Qwen3-ASR INT4 RTN decoder package")
    parser.add_argument("--input", required=True, type=Path, help="FP32 Qwen3 ONNX export directory")
    parser.add_argument("--output", required=True, type=Path, help="Output INT4 package directory")
    parser.add_argument("--block-size", type=int, default=64, choices=(32, 64, 128))
    parser.add_argument("--accuracy-level", type=int, default=4, choices=(0, 1, 2, 3, 4))
    args = parser.parse_args()

    input_dir = args.input.resolve()
    output_dir = args.output.resolve()
    validate_input(input_dir)
    if input_dir == output_dir:
        raise ValueError("Use a separate output directory; the FP32 source must be preserved.")

    print(f"Input : {input_dir}")
    print(f"Output: {output_dir}")
    copy_support_files(input_dir, output_dir)
    quantize_decoder(
        input_dir / "decoder_init.onnx",
        output_dir / "decoder_init.int4.onnx",
        args.block_size,
        args.accuracy_level,
    )
    quantize_decoder(
        input_dir / "decoder_step.onnx",
        output_dir / "decoder_step.int4.onnx",
        args.block_size,
        args.accuracy_level,
    )
    share_decoder_weights(output_dir)
    write_config(input_dir, output_dir, args.block_size, args.accuracy_level)
    print("Done: decoder graphs are INT4; encoder.int4.onnx is an FP32 CPU encoder.")


if __name__ == "__main__":
    main()