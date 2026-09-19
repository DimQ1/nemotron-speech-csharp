#!/usr/bin/env python3
"""Compare PyTorch, raw ONNX, and INT4 ONNX VibeVoice streaming output."""

from __future__ import annotations

import argparse
import gc
import json
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any

import numpy as np
import onnxruntime as ort
import soundfile as sf
import torch
from scipy.signal import resample_poly

from vibevoice.modular.modeling_vibevoice_asr import (
    VibeVoiceASRForConditionalGeneration,
)
from vibevoice.processor.vibevoice_asr_processor import VibeVoiceASRProcessor


SAMPLE_RATE = 24_000
FRAME_SAMPLES = 3_200
CHUNK_FRAMES = 22
LOOKAHEAD_FRAMES = 4
CHUNK_SAMPLES = CHUNK_FRAMES * FRAME_SAMPLES
WINDOW_SAMPLES = (CHUNK_FRAMES + LOOKAHEAD_FRAMES) * FRAME_SAMPLES
FEATURE_FRAMES = CHUNK_FRAMES + LOOKAHEAD_FRAMES
HIDDEN_SIZE = 1_536
VOCAB_SIZE = 151_936
LAYER_COUNT = 28


def load_audio(path: Path) -> np.ndarray:
    audio, source_rate = sf.read(str(path), dtype="float32", always_2d=False)
    if audio.ndim > 1:
        audio = audio.mean(axis=1, dtype=np.float32)
    if source_rate != SAMPLE_RATE:
        audio = resample_poly(audio, SAMPLE_RATE, source_rate)
    return np.asarray(audio, dtype=np.float32)


def audio_chunks(audio: np.ndarray) -> list[np.ndarray]:
    chunks: list[np.ndarray] = []
    for start in range(0, audio.size, CHUNK_SAMPLES):
        segment = audio[start : min(start + WINDOW_SAMPLES, audio.size)]
        if segment.size == 0:
            continue
        if segment.size < WINDOW_SAMPLES:
            segment = np.pad(segment, (0, WINDOW_SAMPLES - segment.size))
        chunks.append(segment.reshape(1, 1, WINDOW_SAMPLES).astype(np.float32))
    return chunks


def streaming_prompt(context_info: str | None) -> str:
    keys = "speaker, content"
    if context_info and context_info.strip():
        return (
            "You are a helpful assistant that transcribes audio input into text output. "
            f"Please transcribe the following audios streamingly with these keys: {keys} "
            f"and extra info: {context_info.strip()}\n"
        )
    return (
        "You are a helpful assistant that transcribes audio input into text output. "
        f"Please transcribe the following audios streamingly with these keys: {keys}\n"
    )


def clean_chunk_text(tokenizer: Any, token_ids: list[int]) -> str:
    text = tokenizer.decode(token_ids, skip_special_tokens=True)
    for special_token in (
        "<|text_chunk_end|>",
        "<|object_ref_start|>",
        "<|object_ref_end|>",
        "<|box_start|>",
        "<|speech_start|>",
        "<|speech_end|>",
        "<|speech_pad|>",
    ):
        text = text.replace(special_token, "")
    return text


def transcript_text_for_wer(text: str, processor: VibeVoiceASRProcessor) -> str:
    stripped = text.strip()
    if stripped.startswith(("[", "{", "```json")):
        records = processor.post_process_transcription(text)
        content = [str(record["text"]) for record in records if "text" in record]
        if content:
            return " ".join(content)

    streaming_text = re.sub(r"\bSpeaker\s*\d+\s*:", " ", text, flags=re.IGNORECASE)
    return streaming_text.strip()


def normalize_words(text: str) -> list[str]:
    return re.findall(r"[\w']+", text.lower(), flags=re.UNICODE)


def word_error_rate(hypothesis: str, reference: str) -> float:
    hypothesis_words = normalize_words(hypothesis)
    reference_words = normalize_words(reference)
    if not reference_words:
        return 0.0 if not hypothesis_words else 1.0
    previous = list(range(len(reference_words) + 1))
    for hypothesis_word in hypothesis_words:
        current = [previous[0] + 1]
        for index, reference_word in enumerate(reference_words, start=1):
            substitution = previous[index - 1] + (hypothesis_word != reference_word)
            insertion = current[index - 1] + 1
            deletion = previous[index] + 1
            current.append(min(substitution, insertion, deletion))
        previous = current
    return previous[-1] / len(reference_words)


def load_tokenizer(model_dir: Path) -> VibeVoiceASRProcessor:
    return VibeVoiceASRProcessor.from_pretrained(str(model_dir))


def load_embedding_table(onnx_dir: Path) -> np.memmap:
    embedding_path = onnx_dir / "embed_tokens.float32.bin"
    return np.memmap(
        embedding_path,
        dtype=np.float32,
        mode="r",
        shape=(VOCAB_SIZE, HIDDEN_SIZE),
    )


def embedding_lookup(table: np.ndarray, token_ids: list[int] | np.ndarray) -> np.ndarray:
    ids = np.asarray(token_ids, dtype=np.int64)
    return np.asarray(table[ids], dtype=np.float32)


def torch_feature_chunks(
    model: VibeVoiceASRForConditionalGeneration,
    chunks: list[np.ndarray],
) -> list[np.ndarray]:
    features: list[np.ndarray] = []
    with torch.no_grad():
        for chunk in chunks:
            audio = torch.from_numpy(chunk)
            acoustic_mean = model.model.acoustic_tokenizer.encode(audio).mean
            semantic_mean = model.model.semantic_tokenizer.encode(audio).mean
            chunk_features = (
                model.model.acoustic_connector(acoustic_mean)
                + model.model.semantic_connector(semantic_mean)
            )
            features.append(chunk_features.cpu().numpy().astype(np.float32))
    return features


def ort_options() -> ort.SessionOptions:
    options = ort.SessionOptions()
    options.log_severity_level = 3
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    return options


def open_ort_session(path: Path) -> ort.InferenceSession:
    return ort.InferenceSession(
        str(path),
        sess_options=ort_options(),
        providers=["CPUExecutionProvider"],
    )


def ort_feature_chunks(onnx_dir: Path, chunks: list[np.ndarray]) -> list[np.ndarray]:
    session = open_ort_session(onnx_dir / "speech_features.onnx")
    features: list[np.ndarray] = []
    try:
        for chunk in chunks:
            features.append(session.run(["features"], {"audio": chunk})[0].astype(np.float32))
    finally:
        del session
        gc.collect()
    return features


def cache_input_dict(
    inputs_embeds: np.ndarray,
    position_ids: np.ndarray,
    past: list[np.ndarray] | None,
) -> dict[str, np.ndarray]:
    query_positions = position_ids[0]
    past_length = 0 if past is None else past[0].shape[2]
    key_positions = np.arange(past_length + query_positions.size, dtype=np.int64)
    allowed = key_positions[None, :] <= query_positions[:, None]
    attention_mask = np.where(allowed, 0.0, -np.inf).astype(np.float32)
    values = {
        "inputs_embeds": inputs_embeds.astype(np.float32),
        "position_ids": position_ids.astype(np.int64),
        "attention_mask": attention_mask[None, None, :, :],
    }
    if past is not None:
        for layer in range(LAYER_COUNT):
            values[f"past_key_{layer}"] = past[layer * 2]
            values[f"past_value_{layer}"] = past[layer * 2 + 1]
    return values


def run_onnx_graph(
    session: ort.InferenceSession,
    inputs_embeds: np.ndarray,
    position_ids: np.ndarray,
    past: list[np.ndarray] | None,
) -> tuple[np.ndarray, list[np.ndarray]]:
    outputs = session.run(None, cache_input_dict(inputs_embeds, position_ids, past))
    return outputs[0], [np.asarray(value) for value in outputs[1:]]


def generate_onnx(
    tokenizer: Any,
    table: np.ndarray,
    features: list[np.ndarray],
    graph_dir: Path,
    graph_suffix: str,
    max_new_tokens: int,
) -> tuple[str, list[list[int]], np.ndarray]:
    text_tokenizer = tokenizer.tokenizer
    prompt_ids = text_tokenizer.encode(streaming_prompt(None), add_special_tokens=False)
    prompt_embeds = embedding_lookup(table, prompt_ids)[None, :, :]
    prompt_positions = np.arange(len(prompt_ids), dtype=np.int64)[None, :]

    prefill_session = open_ort_session(graph_dir / f"decoder_prefill{graph_suffix}.onnx")
    try:
        _, past = run_onnx_graph(prefill_session, prompt_embeds, prompt_positions, None)
    finally:
        del prefill_session
        gc.collect()

    speech_start = embedding_lookup(table, [text_tokenizer.speech_start_id])[None, :, :]
    speech_end = embedding_lookup(table, [text_tokenizer.speech_end_id])[None, :, :]
    text_chunk_end_id = text_tokenizer.text_chunk_end_id
    eos_id = text_tokenizer.eos_token_id
    chunks_text: list[str] = []
    chunks_tokens: list[list[int]] = []
    first_audio_logits: np.ndarray | None = None

    for chunk_features in features:
        audio_embeds = np.concatenate(
            (speech_start, chunk_features, speech_end),
            axis=1,
        )
        cache_length = past[0].shape[2]
        audio_positions = np.arange(
            cache_length,
            cache_length + audio_embeds.shape[1],
            dtype=np.int64,
        )[None, :]
        audio_session = open_ort_session(graph_dir / f"decoder_audio{graph_suffix}.onnx")
        try:
            next_logits, past = run_onnx_graph(
                audio_session,
                audio_embeds,
                audio_positions,
                past,
            )
            if first_audio_logits is None:
                first_audio_logits = next_logits[0, -1, :].copy()
        finally:
            del audio_session
            gc.collect()

        token_ids: list[int] = []
        step_session = open_ort_session(graph_dir / f"decoder_step{graph_suffix}.onnx")
        try:
            for _ in range(max_new_tokens):
                next_token_id = int(np.argmax(next_logits[0, -1, :]))
                if next_token_id in (text_chunk_end_id, eos_id):
                    break
                token_ids.append(next_token_id)
                token_embeds = embedding_lookup(table, [next_token_id])[None, :, :]
                cache_length = past[0].shape[2]
                next_logits, past = run_onnx_graph(
                    step_session,
                    token_embeds,
                    np.asarray([[cache_length]], dtype=np.int64),
                    past,
                )

            tce_embeds = embedding_lookup(table, [text_chunk_end_id])[None, :, :]
            cache_length = past[0].shape[2]
            _, past = run_onnx_graph(
                step_session,
                tce_embeds,
                np.asarray([[cache_length]], dtype=np.int64),
                past,
            )
        finally:
            del step_session
            gc.collect()

        chunks_tokens.append(token_ids)
        chunks_text.append(clean_chunk_text(text_tokenizer, token_ids))

    if first_audio_logits is None:
        raise RuntimeError("The audio input did not produce any streaming chunks")
    return "".join(chunks_text), chunks_tokens, first_audio_logits


def generate_torch(
    model: VibeVoiceASRForConditionalGeneration,
    tokenizer: Any,
    features: list[np.ndarray],
    max_new_tokens: int,
) -> tuple[str, list[list[int]], np.ndarray]:
    text_tokenizer = tokenizer.tokenizer
    prompt_ids = text_tokenizer.encode(streaming_prompt(None), add_special_tokens=False)
    with torch.no_grad():
        prompt_tensor = torch.tensor([prompt_ids], dtype=torch.long)
        prompt_embeds = model.get_input_embeddings()(prompt_tensor)
        outputs = model(inputs_embeds=prompt_embeds, use_cache=True, return_dict=True)
        past = outputs.past_key_values
        speech_start_id = text_tokenizer.speech_start_id
        speech_end_id = text_tokenizer.speech_end_id
        text_chunk_end_id = text_tokenizer.text_chunk_end_id
        eos_id = text_tokenizer.eos_token_id
        speech_start = model.get_input_embeddings()(
            torch.tensor([[speech_start_id]], dtype=torch.long)
        )
        speech_end = model.get_input_embeddings()(
            torch.tensor([[speech_end_id]], dtype=torch.long)
        )

        chunks_text: list[str] = []
        chunks_tokens: list[list[int]] = []
        first_audio_logits: torch.Tensor | None = None
        for chunk_features in features:
            feature_tensor = torch.from_numpy(chunk_features)
            audio_embeds = torch.cat((speech_start, feature_tensor, speech_end), dim=1)
            outputs = model(
                inputs_embeds=audio_embeds,
                past_key_values=past,
                use_cache=True,
                return_dict=True,
            )
            past = outputs.past_key_values
            next_logits = outputs.logits[:, -1:, :]
            if first_audio_logits is None:
                first_audio_logits = next_logits[0, -1, :].detach().cpu().numpy().copy()

            token_ids: list[int] = []
            for _ in range(max_new_tokens):
                next_token_id = int(torch.argmax(next_logits[:, -1, :], dim=-1).item())
                if next_token_id in (text_chunk_end_id, eos_id):
                    break
                token_ids.append(next_token_id)
                token_embeds = model.get_input_embeddings()(
                    torch.tensor([[next_token_id]], dtype=torch.long)
                )
                outputs = model(
                    inputs_embeds=token_embeds,
                    past_key_values=past,
                    use_cache=True,
                    return_dict=True,
                )
                past = outputs.past_key_values
                next_logits = outputs.logits

            tce_embeds = model.get_input_embeddings()(
                torch.tensor([[text_chunk_end_id]], dtype=torch.long)
            )
            outputs = model(
                inputs_embeds=tce_embeds,
                past_key_values=past,
                use_cache=True,
                return_dict=True,
            )
            past = outputs.past_key_values
            chunks_tokens.append(token_ids)
            chunks_text.append(clean_chunk_text(text_tokenizer, token_ids))

    if first_audio_logits is None:
        raise RuntimeError("The audio input did not produce any streaming chunks")
    return "".join(chunks_text), chunks_tokens, first_audio_logits


def load_torch_model(model_dir: Path) -> VibeVoiceASRForConditionalGeneration:
    torch.backends.cudnn.enabled = False
    return VibeVoiceASRForConditionalGeneration.from_pretrained(
        str(model_dir),
        dtype=torch.float32,
        attn_implementation="sdpa",
    ).to("cpu").eval()


def run_backend(args: argparse.Namespace) -> dict[str, Any]:
    model_dir = args.model_dir.resolve()
    onnx_dir = args.onnx_dir.resolve()
    processor = load_tokenizer(model_dir)
    results: list[dict[str, Any]] = []
    table: np.memmap | None = None
    model: VibeVoiceASRForConditionalGeneration | None = None

    if args.backend == "pytorch":
        model = load_torch_model(model_dir)
    else:
        table = load_embedding_table(onnx_dir)

    for audio_path in args.audio:
        audio_path = audio_path.resolve()
        audio = load_audio(audio_path)
        chunks = audio_chunks(audio)
        started = time.perf_counter()
        if args.backend == "pytorch":
            features = torch_feature_chunks(model, chunks)
            transcript, token_ids, first_logits = generate_torch(
                model, processor, features, args.max_new_tokens
            )
        else:
            features = ort_feature_chunks(onnx_dir, chunks)
            suffix = "" if args.backend == "onnx-fp32" else ".int4"
            transcript, token_ids, first_logits = generate_onnx(
                processor,
                table,
                features,
                onnx_dir,
                suffix,
                args.max_new_tokens,
            )
        elapsed = time.perf_counter() - started
        reference_path = audio_path.with_suffix(".txt")
        reference = reference_path.read_text(encoding="utf-8").strip() if reference_path.exists() else ""
        wer_text = transcript_text_for_wer(transcript, processor)
        logits_path = None
        if args.logits_dir:
            args.logits_dir.mkdir(parents=True, exist_ok=True)
            logits_path = args.logits_dir / f"{args.backend}-{len(results):03d}-{audio_path.stem}.npy"
            np.save(logits_path, first_logits.astype(np.float32))
        results.append(
            {
                "audio": str(audio_path),
                "duration_sec": float(audio.size / SAMPLE_RATE),
                "chunks": len(chunks),
                "transcript": transcript,
                "wer_text": wer_text,
                "reference": reference,
                "wer": word_error_rate(wer_text, reference) if reference else None,
                "token_ids": token_ids,
                "token_count": sum(len(chunk) for chunk in token_ids),
                "elapsed_sec": elapsed,
                "rtf": elapsed / (audio.size / SAMPLE_RATE),
                "first_audio_logits": str(logits_path) if logits_path else None,
            }
        )

    del table, model, processor
    gc.collect()
    return {"backend": args.backend, "results": results}


def compare_logits(first: np.ndarray, second: np.ndarray) -> dict[str, float | bool]:
    first = first.astype(np.float64).reshape(-1)
    second = second.astype(np.float64).reshape(-1)
    difference = np.abs(first - second)
    denominator = np.linalg.norm(first) * np.linalg.norm(second)
    cosine = float(np.dot(first, second) / denominator) if denominator else 0.0
    return {
        "max_abs": float(difference.max()),
        "mean_abs": float(difference.mean()),
        "cosine": cosine,
        "argmax_equal": bool(np.argmax(first) == np.argmax(second)),
    }


def compare_tokens(first: list[list[int]], second: list[list[int]]) -> dict[str, float | int]:
    first_flat = [token for chunk in first for token in chunk]
    second_flat = [token for chunk in second for token in chunk]
    compared = min(len(first_flat), len(second_flat))
    matches = sum(first_flat[index] == second_flat[index] for index in range(compared))
    return {
        "first_count": len(first_flat),
        "second_count": len(second_flat),
        "compared": compared,
        "agreement": float(matches / compared) if compared else 1.0,
    }


def run_all(args: argparse.Namespace) -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="vibevoice-quality-") as temporary_dir:
        temporary_path = Path(temporary_dir)
        persistent_logits_dir = (
            args.logits_dir or Path("work") / "vibevoice-quality-logits-all"
        ).resolve()
        backend_results: dict[str, dict[str, Any]] = {}
        for backend in ("pytorch", "onnx-fp32", "onnx-int4"):
            result_path = temporary_path / f"{backend}.json"
            command = [
                sys.executable,
                str(Path(__file__).resolve()),
                "--backend",
                backend,
                "--model-dir",
                str(args.model_dir.resolve()),
                "--onnx-dir",
                str(args.onnx_dir.resolve()),
                "--audio",
                *(str(path.resolve()) for path in args.audio),
                "--max-new-tokens",
                str(args.max_new_tokens),
                "--json-out",
                str(result_path),
                "--logits-dir",
                str(persistent_logits_dir),
            ]
            completed = subprocess.run(command, capture_output=True, text=True)
            if completed.returncode != 0:
                raise RuntimeError(
                    f"{backend} quality run failed:\n{completed.stderr[-4000:]}"
                )
            backend_results[backend] = json.loads(result_path.read_text(encoding="utf-8"))

        by_audio: dict[str, dict[str, Any]] = {}
        for index, audio_path in enumerate(args.audio):
            key = str(audio_path.resolve())
            pytorch_result = backend_results["pytorch"]["results"][index]
            fp32_result = backend_results["onnx-fp32"]["results"][index]
            int4_result = backend_results["onnx-int4"]["results"][index]
            pytorch_logits = np.load(pytorch_result["first_audio_logits"])
            fp32_logits = np.load(fp32_result["first_audio_logits"])
            int4_logits = np.load(int4_result["first_audio_logits"])
            by_audio[key] = {
                "pytorch": pytorch_result,
                "onnx_fp32": fp32_result,
                "onnx_int4": int4_result,
                "tokens_pytorch_vs_fp32": compare_tokens(
                    pytorch_result["token_ids"], fp32_result["token_ids"]
                ),
                "tokens_fp32_vs_int4": compare_tokens(
                    fp32_result["token_ids"], int4_result["token_ids"]
                ),
                "logits_pytorch_vs_fp32": compare_logits(pytorch_logits, fp32_logits),
                "logits_fp32_vs_int4": compare_logits(fp32_logits, int4_logits),
            }
        return {"mode": "all", "results": by_audio}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--backend",
        choices=("pytorch", "onnx-fp32", "onnx-int4", "all"),
        default="all",
    )
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--onnx-dir", type=Path, required=True)
    parser.add_argument("--audio", type=Path, nargs="+", required=True)
    parser.add_argument("--max-new-tokens", type=int, default=128)
    parser.add_argument("--json-out", type=Path)
    parser.add_argument("--logits-dir", type=Path)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    result = run_all(args) if args.backend == "all" else run_backend(args)
    serialized = json.dumps(result, indent=2, ensure_ascii=True) + "\n"
    if args.json_out:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(serialized, encoding="utf-8")
    print(serialized, end="")


if __name__ == "__main__":
    main()