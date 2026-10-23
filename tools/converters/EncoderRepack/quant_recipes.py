"""Weight-only recipes for `MatMulNBits` (com.microsoft), from the quantisers that ship with onnxruntime.

Every recipe quantises a constant [K, N] MatMul weight in blocks of `block_size` consecutive K values per output
column and returns the three (or four) MatMulNBits inputs: packed weights [N, k_blocks, block_size * bits / 8],
float32 scales [N, k_blocks] and, for the asymmetric recipes, zero points (4 bits: packed two per byte,
[N, ceil(k_blocks / 2)]; 8 bits: [N, k_blocks]). A symmetric recipe has no zero-point input: the kernel then uses the
middle of the range (8 for 4 bits, 128 for 8 bits).

| recipe        | zero points | source of the code                                    | what it is                          |
|---------------|-------------|-------------------------------------------------------|-------------------------------------|
| k_quant       | yes         | onnxruntime.quantization.neural_compressor (INC)      | llama.cpp-style search of scale/min per block (Olive `OnnxKQuantQuantization`, the scheme of the shipped Nemotron INT4 encoders) |
| default_sym   | no          | onnxruntime C++ `quantize_matmul_{4,8}bits` (MLAS)    | scale = the block's extreme / -8 (`MatMulNBitsQuantizer` default, the scheme of the shipped Parakeet INT4 encoder) |
| default_asym  | yes         | onnxruntime C++ `quantize_matmul_{4,8}bits` (MLAS)    | min / max per block                 |
| rtn_sym       | no          | INC `quant_tensor(scheme="sym")`                      | scale = max abs / 7.5 (6.7 % coarser than default_sym) |
| rtn_asym      | yes         | INC `quant_tensor(scheme="asym")`                     | min / max per block                 |

HQQ (`HQQWeightOnlyQuantizer`) needs torch and stores float zero points; it is not offered here.
"""

from __future__ import annotations

import numpy as np
from onnx import helper
from onnxruntime.capi._pybind_state import quantize_matmul_4bits, quantize_matmul_8bits
from onnxruntime.quantization.neural_compressor.weight_only import quant_tensor, quant_tensor_k_quant_cpu

from model_folder import WeightStore

RECIPES = ("k_quant", "default_sym", "default_asym", "rtn_sym", "rtn_asym")


def _pad_rows(w: np.ndarray, block_size: int) -> np.ndarray:
    k = w.shape[0]
    padded = -(-k // block_size) * block_size
    return np.pad(w, ((0, padded - k), (0, 0))) if padded != k else w


def _pack_nibbles(q: np.ndarray) -> np.ndarray:
    """[rows, 2m] values 0..15 -> [rows, m] bytes, even element in the low nibble (the MatMulNBits layout)."""
    if q.shape[-1] % 2:
        q = np.concatenate([q, np.full(q.shape[:-1] + (1,), 8, dtype=np.uint8)], axis=-1)
    return (q[..., ::2] | (q[..., 1::2] << 4)).astype(np.uint8)


def quantize_weight(w: np.ndarray, recipe: str, block_size: int, bits: int = 4):
    """Quantises a [K, N] float weight; returns (packed, scales, zero_points or None)."""
    if recipe not in RECIPES:
        raise ValueError(f"unknown recipe {recipe}; use one of {RECIPES}")
    if bits not in (4, 8):
        raise ValueError("bits must be 4 or 8")
    w = np.ascontiguousarray(w.astype(np.float32))
    k, n = w.shape
    k_blocks = -(-k // block_size)
    wp = np.ascontiguousarray(_pad_rows(w, block_size))
    if recipe.startswith("default"):
        symmetric = recipe == "default_sym"
        per_byte = 8 // bits
        packed = np.zeros((n, k_blocks, block_size // per_byte), dtype=np.uint8)
        zero_points = np.zeros((n, (k_blocks + per_byte - 1) // per_byte), dtype=np.uint8)
        scales = np.zeros((n, k_blocks), dtype=np.float32)
        quantize = quantize_matmul_4bits if bits == 4 else quantize_matmul_8bits
        quantize(packed, wp, scales, zero_points, block_size, n, k, symmetric)
        return packed, scales, None if symmetric else zero_points

    if recipe == "k_quant":
        q, scale, zp = quant_tensor_k_quant_cpu(wp.T, bits, block_size)
    else:
        q, scale, zp = quant_tensor(wp.T, bits, block_size, "sym" if recipe == "rtn_sym" else "asym", "uint")
    q = q.astype(np.uint8)  # [n * k_blocks, block_size]
    packed = (_pack_nibbles(q) if bits == 4 else q).reshape(n, k_blocks, block_size * bits // 8)
    scales = scale.astype(np.float32).reshape(n, k_blocks)
    if recipe == "rtn_sym":
        return packed, scales, None
    zp = np.asarray(zp, dtype=np.uint8).reshape(n, k_blocks)
    return packed, scales, _pack_nibbles(zp) if bits == 4 else zp


def dequantize(packed: np.ndarray, scales: np.ndarray, zero_points: np.ndarray | None, k: int, n: int,
               block_size: int, bits: int = 4) -> np.ndarray:
    """MatMulNBits weights back to a float [K, N] matrix (for the reconstruction-error report)."""
    k_blocks = -(-k // block_size)
    p = np.asarray(packed, dtype=np.uint8).reshape(n, k_blocks, block_size * bits // 8)
    if bits == 4:
        q = np.empty((n, k_blocks, block_size), dtype=np.float32)
        q[..., ::2] = p & 0x0F
        q[..., 1::2] = p >> 4
    else:
        q = p.astype(np.float32)
    s = np.asarray(scales, dtype=np.float32).reshape(n, k_blocks, 1)
    if zero_points is None:
        z = np.full((n, k_blocks, 1), float(1 << (bits - 1)), dtype=np.float32)
    elif bits == 4:
        zp = np.asarray(zero_points, dtype=np.uint8).reshape(n, -1)
        z_all = np.empty((n, zp.shape[1] * 2), dtype=np.float32)
        z_all[:, ::2] = zp & 0x0F
        z_all[:, 1::2] = zp >> 4
        z = z_all[:, :k_blocks].reshape(n, k_blocks, 1)
    else:
        z = np.asarray(zero_points, dtype=np.float32).reshape(n, k_blocks, 1)
    w = ((q - z) * s).reshape(n, k_blocks * block_size)[:, :k]
    return w.T


def quantize_graph(graph, store: WeightStore, recipe: str, block_size: int, accuracy_level: int,
                   min_k: int | None = None, only: set[str] | None = None, bits: int = 4) -> list[dict]:
    """Replaces every FP32 MatMul with a constant 2-D weight (optionally only the nodes in `only`) by MatMulNBits."""
    min_k = block_size if min_k is None else min_k
    report = []
    nodes = []
    for node in graph.node:
        eligible = (
            node.op_type == "MatMul"
            and node.input[1] in store
            and node.input[0] not in store
            and len(store.shape(node.input[1])) == 2
            and store.dtype(node.input[1]) == np.float32
            and store.shape(node.input[1])[0] >= min_k
            and (only is None or node.name in only)
        )
        if not eligible:
            nodes.append(node)
            continue
        w = store.array(node.input[1])
        k, n = w.shape
        packed, scales, zero_points = quantize_weight(w, recipe, block_size, bits)
        w_hat = dequantize(packed, scales, zero_points, k, n, block_size, bits)
        base = node.input[1]
        q_name, s_name, z_name = base + f"_Q{bits}", base + "_scales", base + "_zero_points"
        store.add(q_name, packed)
        store.add(s_name, scales)
        inputs = [node.input[0], q_name, s_name]
        if zero_points is not None:
            store.add(z_name, zero_points)
            inputs.append(z_name)
        attrs = {"K": k, "N": n, "bits": bits, "block_size": block_size}
        if accuracy_level:
            attrs["accuracy_level"] = accuracy_level
        nodes.append(helper.make_node("MatMulNBits", inputs, list(node.output), name=(node.name or base) + f"_Q{bits}",
                                      domain="com.microsoft", **attrs))
        err = w - w_hat
        report.append({
            "node": node.name, "K": k, "N": n, "bits": bits,
            "fp32_bytes": int(w.nbytes),
            "quantized_bytes": int(packed.nbytes + scales.nbytes + (0 if zero_points is None else zero_points.nbytes)),
            "rel_rmse": float(np.sqrt(np.mean(err.astype(np.float64) ** 2) / np.mean(w.astype(np.float64) ** 2))),
        })
    copies = []
    for node in nodes:
        c = type(node)()
        c.CopyFrom(node)
        copies.append(c)
    del graph.node[:]
    graph.node.extend(copies)
    return report
