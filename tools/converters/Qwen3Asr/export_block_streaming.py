#!/usr/bin/env python3
"""Create a fixed-block streaming Qwen3 encoder package.

The published Qwen3 encoder is already partitioned into independent local
attention regions by the ONNX exporter. This tool specializes that graph to a
fixed mel block instead of inventing a fake recurrent cache. For example:

    200 mel frames (2 seconds) -> 26 audio features
    800 mel frames (8 seconds) -> 104 audio features

The C# provider feeds each block once, retains only bounded audio features, and
re-runs the decoder over the configured feature window. Smaller blocks reduce
first-result latency but expose less bidirectional context to each encoder run,
so every profile must be measured for WER before deployment. The default profile
uses 200 mel frames (2 seconds) and 26 audio features.

The input package must contain ``encoder.int4.onnx`` (the graph is FP32 under
the compatibility name). Decoder/tokenizer files stay in ``--input`` and are
referenced by the generated ``streaming_config.json``.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort


MEL_BINS = 128
DEFAULT_BLOCK_FRAMES = 200
DEFAULT_BLOCK_SECONDS = 2.0
DEFAULT_WINDOW_SECONDS = 16.0
CONV_WINDOW = 100
TOKENS_PER_WINDOW = 13


def set_dimension(dimension, value: int) -> None:
    dimension.ClearField("dim_param")
    dimension.dim_value = value


def get_encoder_output_length(mel_frames: int) -> int:
    """Match the Qwen3 convolutional downsampling length formula."""
    leave = mel_frames % CONV_WINDOW
    t = (leave + 1) // 2
    t = (t + 1) // 2
    t = (t + 1) // 2
    return t + (mel_frames // CONV_WINDOW) * TOKENS_PER_WINDOW


def specialize_encoder(
    source: Path,
    target: Path,
    block_frames: int,
    expected_features: int,
) -> tuple[str, str]:
    model = onnx.load(str(source), load_external_data=False)
    if not model.graph.input:
        raise ValueError(f"Encoder graph has no inputs: {source}")
    if not model.graph.output:
        raise ValueError(f"Encoder graph has no outputs: {source}")

    mel_input = next((item for item in model.graph.input if item.name == "mel"), model.graph.input[0])
    audio_output = next((item for item in model.graph.output if item.name == "audio_features"), model.graph.output[0])

    mel_shape = mel_input.type.tensor_type.shape.dim
    if len(mel_shape) != 3:
        raise ValueError(f"Expected [batch, mel, time] input, got {mel_input.name}")
    set_dimension(mel_shape[0], 1)
    set_dimension(mel_shape[1], MEL_BINS)
    set_dimension(mel_shape[2], block_frames)

    output_shape = audio_output.type.tensor_type.shape.dim
    if len(output_shape) != 3:
        raise ValueError(f"Expected [batch, tokens, hidden] output, got {audio_output.name}")
    set_dimension(output_shape[0], 1)
    set_dimension(output_shape[1], expected_features)

    target.parent.mkdir(parents=True, exist_ok=True)
    onnx.save_model(model, str(target), save_as_external_data=False)
    return mel_input.name, audio_output.name


def validate_encoder(
    path: Path,
    input_name: str,
    output_name: str,
    block_frames: int,
    expected_features: int,
) -> tuple[int, ...]:
    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    input_info = session.get_inputs()[0]
    if input_info.name != input_name:
        raise ValueError(f"Unexpected encoder input after export: {input_info.name}")

    output = session.run([output_name], {input_name: np.zeros((1, MEL_BINS, block_frames), dtype=np.float32)})[0]
    if output.shape[0] != 1 or output.shape[1] != expected_features:
        raise ValueError(f"Unexpected streaming output shape: {output.shape}")
    return tuple(int(value) for value in output.shape)


def compare_encoder_outputs(
    source: Path,
    target: Path,
    input_name: str,
    output_name: str,
    block_frames: int,
) -> tuple[float, float, float]:
    """Compare the specialized graph with the dynamic source graph."""
    mel = np.random.default_rng(20260905).standard_normal(
        (1, MEL_BINS, block_frames), dtype=np.float32
    )

    source_session = ort.InferenceSession(str(source), providers=["CPUExecutionProvider"])
    source_output = source_session.run([output_name], {input_name: mel})[0]
    del source_session

    target_session = ort.InferenceSession(str(target), providers=["CPUExecutionProvider"])
    target_output = target_session.run([output_name], {input_name: mel})[0]

    if source_output.shape != target_output.shape:
        raise ValueError(
            f"Encoder output shapes differ: source={source_output.shape}, target={target_output.shape}"
        )

    source_flat = source_output.astype(np.float64, copy=False).ravel()
    target_flat = target_output.astype(np.float64, copy=False).ravel()
    absolute_error = np.abs(source_flat - target_flat)
    denominator = np.linalg.norm(source_flat) * np.linalg.norm(target_flat)
    cosine_similarity = float(np.dot(source_flat, target_flat) / denominator) if denominator else 1.0
    return float(absolute_error.max()), float(absolute_error.mean()), cosine_similarity


def write_manifest(
    output_dir: Path,
    input_dir: Path,
    block_seconds: float,
    window_seconds: float,
    self_contained: bool,
) -> None:
    base_model_dir = "." if self_contained else os.path.relpath(input_dir, output_dir).replace(os.sep, "/")
    manifest = {
        "model_type": "qwen3_asr_onnx_streaming",
        "base_model_dir": base_model_dir,
        "encoder_file": "encoder_stream.onnx",
        "block_seconds": block_seconds,
        "chunk_seconds": block_seconds,
        "window_seconds": window_seconds,
        "streaming_mode": "block_encoder",
        # Blocks below the silence threshold are encoded into the window but not
        # decoded; the next speech block or Flush decodes the full window.
        "skip_silent_blocks": True,
        # 1 = decode on every block; N > 1 coalesces decodes once the window is full.
        "decode_every_blocks": 1,
        # True trades ~+3.5 pp CV17 WER for text at the first block boundary.
        "emit_first_block": False,
    }
    (output_dir / "streaming_config.json").write_text(
        json.dumps(manifest, indent=2) + "\n",
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Export a block-streaming Qwen3-ASR encoder package")
    parser.add_argument("--input", required=True, type=Path, help="Regular Qwen3 ONNX package directory")
    parser.add_argument("--output", required=True, type=Path, help="Streaming manifest/output directory")
    parser.add_argument("--block-frames", type=int, default=DEFAULT_BLOCK_FRAMES)
    parser.add_argument("--block-seconds", type=float, default=DEFAULT_BLOCK_SECONDS)
    parser.add_argument("--window-seconds", type=float, default=DEFAULT_WINDOW_SECONDS)
    parser.add_argument("--force", action="store_true", help="Replace an existing encoder_stream.onnx")
    parser.add_argument(
        "--self-contained",
        action="store_true",
        help="Copy the regular model files into the output directory",
    )
    parser.add_argument(
        "--verify-equivalence",
        action="store_true",
        help="Compare one fixed block against the original dynamic encoder",
    )
    args = parser.parse_args()

    input_dir = args.input.resolve()
    output_dir = args.output.resolve()
    source = input_dir / "encoder.int4.onnx"
    target = output_dir / "encoder_stream.onnx"
    if not source.exists():
        raise FileNotFoundError(f"Missing regular encoder: {source}")
    if target.exists() and not args.force:
        raise FileExistsError(f"Output exists; pass --force to replace it: {target}")
    if args.block_frames <= 0 or args.block_frames % CONV_WINDOW != 0:
        raise ValueError("block_frames must be a positive multiple of 100 mel frames.")
    expected_block_seconds = args.block_frames / 100.0
    if not np.isclose(args.block_seconds, expected_block_seconds):
        raise ValueError(
            f"block_seconds must match {expected_block_seconds:g} seconds for {args.block_frames} mel frames."
        )
    if args.block_seconds <= 0 or args.window_seconds < args.block_seconds * 2:
        raise ValueError("window_seconds must contain at least two encoder blocks.")
    if not np.isclose(args.window_seconds / args.block_seconds, round(args.window_seconds / args.block_seconds)):
        raise ValueError("window_seconds must be an integer multiple of block_seconds.")
    expected_features = get_encoder_output_length(args.block_frames)

    if args.self_contained:
        output_dir.mkdir(parents=True, exist_ok=True)
        for item in input_dir.iterdir():
            if item.is_file():
                shutil.copy2(item, output_dir / item.name)

    print(f"Specializing {source}")
    input_name, output_name = specialize_encoder(
        source, target, args.block_frames, expected_features
    )
    shape = validate_encoder(
        target, input_name, output_name, args.block_frames, expected_features
    )
    write_manifest(
        output_dir,
        input_dir,
        args.block_seconds,
        args.window_seconds,
        args.self_contained,
    )

    print(f"Wrote: {target}")
    print(f"Validated output shape: {shape}")
    if args.verify_equivalence:
        max_abs, mean_abs, cosine = compare_encoder_outputs(
            source, target, input_name, output_name, args.block_frames
        )
        print(f"Equivalence max abs error: {max_abs:.9g}")
        print(f"Equivalence mean abs error: {mean_abs:.9g}")
        print(f"Equivalence cosine similarity: {cosine:.12f}")
    print(f"Manifest: {output_dir / 'streaming_config.json'}")
    print(f"Encoder package size: {target.stat().st_size / 1e9:.3f} GB")


if __name__ == "__main__":
    main()