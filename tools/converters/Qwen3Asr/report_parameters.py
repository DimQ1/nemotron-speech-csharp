#!/usr/bin/env python3
"""Report Qwen3-ASR logical parameters and physical package storage."""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

import onnx


GRAPH_NAMES = (
    "encoder.int4.onnx",
    "decoder_init.int4.onnx",
    "decoder_step.int4.onnx",
)


def graph_metadata(path: Path) -> dict[str, int | str]:
    model = onnx.load(str(path), load_external_data=False)
    operator_counts: dict[str, int] = {}
    initializer_elements = 0
    quantized_parameter_equivalent = 0
    quantized_initializers: set[str] = set()
    for node in model.graph.node:
        operator_counts[node.op_type] = operator_counts.get(node.op_type, 0) + 1
        if node.op_type == "MatMulNBits":
            attributes = {attribute.name: onnx.helper.get_attribute_value(attribute) for attribute in node.attribute}
            quantized_parameter_equivalent += int(attributes["K"]) * int(attributes["N"])
            quantized_initializers.update(node.input[1:])
    for tensor in model.graph.initializer:
        elements = 1
        for dimension in tensor.dims:
            elements *= dimension
        initializer_elements += elements
        if tensor.name not in quantized_initializers:
            quantized_parameter_equivalent += elements
    return {
        "nodes": len(model.graph.node),
        "initializers": len(model.graph.initializer),
        "initializer_elements": initializer_elements,
        "matmul_nbits": operator_counts.get("MatMulNBits", 0),
        "matmul": operator_counts.get("MatMul", 0),
        "parameter_equivalent": quantized_parameter_equivalent,
    }


def decoder_parameter_count(config: dict) -> dict[str, int]:
    decoder = config["decoder"]
    hidden_size = int(decoder["hidden_size"])
    layers = int(decoder["num_layers"])
    attention_heads = int(decoder["num_attention_heads"])
    key_value_heads = int(decoder["num_key_value_heads"])
    head_dim = int(decoder["head_dim"])
    intermediate_size = int(decoder["intermediate_size"])
    vocab_size = int(decoder["vocab_size"])

    query_size = attention_heads * head_dim
    key_value_size = key_value_heads * head_dim
    attention = (
        hidden_size * query_size
        + 2 * hidden_size * key_value_size
        + query_size * hidden_size
    )
    feed_forward = 3 * hidden_size * intermediate_size
    layer_norms = 2 * hidden_size
    per_layer = attention + feed_forward + layer_norms
    embeddings = vocab_size * hidden_size
    final_norm = hidden_size
    return {
        "embeddings": embeddings,
        "attention": layers * attention,
        "feed_forward": layers * feed_forward,
        "layer_norms": layers * layer_norms + final_norm,
        "total": embeddings + layers * per_layer + final_norm,
    }


def format_count(value: int) -> str:
    return f"{value:,} ({value / 1_000_000_000:.3f}B)"


def format_bytes(value: int) -> str:
    return f"{value:,} ({value / 1_000_000_000:.3f} GB; {value / 2**30:.3f} GiB)"


def package_files(model_dir: Path) -> list[Path]:
    return [
        path
        for path in model_dir.rglob("*")
        if path.is_file() and ".cache" not in path.relative_to(model_dir).parts
    ]


def external_data_size(graph_path: Path) -> int:
    data_path = Path(f"{graph_path}.data")
    return data_path.stat().st_size if data_path.exists() else 0


def write_report(model_dir: Path, output_path: Path | None) -> str:
    config_path = model_dir / "config.json"
    config = json.loads(config_path.read_text(encoding="utf-8"))
    graph_info = {
        name: graph_metadata(model_dir / name)
        for name in GRAPH_NAMES
    }
    decoder = decoder_parameter_count(config)
    encoder_initializer_elements = int(graph_info["encoder.int4.onnx"]["initializer_elements"])
    decoder_graph_parameter_equivalent = int(graph_info["decoder_init.int4.onnx"]["parameter_equivalent"])
    tied_embedding_parameters = int(config["decoder"]["vocab_size"]) * int(config["decoder"]["hidden_size"])
    graph_logical_total = encoder_initializer_elements + decoder_graph_parameter_equivalent - tied_embedding_parameters

    files = package_files(model_dir)
    package_size = sum(path.stat().st_size for path in files)
    encoder_graph = (model_dir / "encoder.int4.onnx").stat().st_size + external_data_size(
        model_dir / "encoder.int4.onnx"
    )
    decoder_graphs = sum(
        (model_dir / name).stat().st_size
        for name in GRAPH_NAMES[1:]
    )
    decoder_data = sum(
        (model_dir / name).stat().st_size
        for name in ("decoder_weights.int4.data", "decoder_init.int4.onnx.data", "decoder_step.int4.onnx.data")
        if (model_dir / name).exists()
    )
    embeddings_path = model_dir / "embed_tokens.bin"
    embeddings_size = embeddings_path.stat().st_size if embeddings_path.exists() else 0
    component_size = encoder_graph + decoder_graphs + decoder_data + embeddings_size
    support_files = package_size - component_size
    generated = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

    lines = [
        "# Qwen3-ASR Parameter and Storage Report",
        "",
        f"Generated: {generated}",
        f"Model directory: `{model_dir}`",
        "",
        "## Logical parameters",
        "",
        "The logical count estimates the original dense model. The decoder uses tied word embeddings, so the embedding matrix is counted once.",
        "",
        "| Component | Parameters |",
        "| --- | ---: |",
        f"| Encoder graph initializer elements | {format_count(encoder_initializer_elements)} |",
        f"| Decoder embeddings | {format_count(decoder['embeddings'])} |",
        f"| Decoder attention projections | {format_count(decoder['attention'])} |",
        f"| Decoder feed-forward projections | {format_count(decoder['feed_forward'])} |",
        f"| Decoder norms | {format_count(decoder['layer_norms'])} |",
        f"| Decoder total | {format_count(decoder['total'])} |",
        f"| Architecture estimate | {format_count(encoder_initializer_elements + decoder['total'])} |",
        f"| Graph-equivalent decoder (including biases) | {format_count(decoder_graph_parameter_equivalent)} |",
        f"| Tied embedding counted twice in decoder graph | -{format_count(tied_embedding_parameters)} |",
        f"| **Logical total from graph-equivalent count** | **{format_count(graph_logical_total)}** |",
        "",
        "The architecture estimate follows config.json. The graph-equivalent count replaces each MatMulNBits weight with its dense K x N shape and retains biases and other dense initializers; it is the primary total for this package. The tied embedding matrix is subtracted once because the decoder graph contains both the lookup and lm_head views.",
        "",
        "## Physical package storage",
        "",
        "| Storage group | Bytes |",
        "| --- | ---: |",
        f"| Encoder graph and external data | {format_bytes(encoder_graph)} |",
        f"| Decoder graph files (without external data) | {format_bytes(decoder_graphs)} |",
        f"| Decoder INT4 external data | {format_bytes(decoder_data)} |",
        f"| Tied embedding file | {format_bytes(embeddings_size)} |",
        f"| Tokenizer, config, and support files | {format_bytes(support_files)} |",
        f"| **Package total** | **{format_bytes(package_size)}** |",
        "",
        "## Graph validation",
        "",
        "| Graph | Nodes | Initializers | MatMulNBits | MatMul |",
        "| --- | ---: | ---: | ---: | ---: |",
    ]
    for name in GRAPH_NAMES:
        info = graph_info[name]
        lines.append(
            f"| `{name}` | {info['nodes']:,} | {info['initializers']:,} | {info['matmul_nbits']:,} | {info['matmul']:,} |"
        )
    lines.extend(
        [
            "",
            "## Interpretation",
            "",
            "- This package is hybrid: the encoder is stored as FP32 under the compatibility name `encoder.int4.onnx`; it has no `MatMulNBits` nodes.",
            "- The decoder graphs use INT4 RTN `MatMulNBits` weights and share `decoder_weights.int4.data` when produced by the converter.",
            "- Logical parameters and physical storage are different measurements: packed INT4 weights reduce bytes without changing the dense model parameter count.",
        ]
    )
    report = "\n".join(lines) + "\n"
    if output_path is not None:
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(report, encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description="Report Qwen3-ASR parameters and package storage")
    parser.add_argument("--model-dir", required=True, type=Path, help="Qwen3-ASR model package directory")
    parser.add_argument("--output", type=Path, help="Optional Markdown report path")
    args = parser.parse_args()

    model_dir = args.model_dir.resolve()
    missing = [name for name in ("config.json", *GRAPH_NAMES) if not (model_dir / name).exists()]
    if missing:
        raise FileNotFoundError(f"Missing model files in {model_dir}: {', '.join(missing)}")
    report = write_report(model_dir, args.output.resolve() if args.output else None)
    print(report, end="")
    if args.output:
        print(f"Saved: {args.output}")


if __name__ == "__main__":
    main()