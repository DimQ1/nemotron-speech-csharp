"""Weight-level A/B of the INT4 recipes against the FP32 encoder, and a reproduction check of a shipped INT4 encoder.

For every MatMul weight (and, with --pointwise, every pointwise Conv weight) of the FP32 encoder: quantise it with
each recipe x block size, dequantise, and report the relative RMS error ||W - W_hat|| / ||W|| (a cheap proxy that
ranks recipes before any WER run). With --shipped, the shipped INT4 encoder's MatMulNBits weights are dequantised
and scored the same way, and the recipe that reproduces them byte for byte (if any) is named.

Usage:
    python compare_recipes.py --fp32 <FP32 model folder> [--shipped <INT4 model folder>]
        [--recipes k_quant default_sym ...] [--blocks 32 64] [--pointwise] [--limit N]
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import onnx

from graph_surgery import pointwise_conv_rank
from model_folder import ModelFolder, WeightStore
from quant_recipes import RECIPES, dequantize, quantize_weight


def rel_rmse(w: np.ndarray, w_hat: np.ndarray) -> float:
    w = w.astype(np.float64)
    return float(np.sqrt(np.mean((w - w_hat) ** 2) / np.mean(w**2)))


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--fp32", required=True, help="FP32 model folder")
    ap.add_argument("--shipped", help="INT4 model folder made from the same FP32 export")
    ap.add_argument("--recipes", nargs="+", default=list(RECIPES), choices=RECIPES)
    ap.add_argument("--blocks", nargs="+", type=int, default=[32, 64])
    ap.add_argument("--pointwise", action="store_true", help="also score the pointwise Conv weights")
    ap.add_argument("--limit", type=int, help="score only the first N weights of each group (quick look)")
    ap.add_argument("--json", help="write the per-weight numbers here")
    args = ap.parse_args()

    fp = ModelFolder.detect(Path(args.fp32))
    fm = onnx.load(str(fp.encoder_path), load_external_data=False)
    fs = WeightStore(fm, fp.path)
    groups: dict[str, list[tuple[str, str]]] = {"matmul": [], "pointwise": []}
    for n in fm.graph.node:
        if n.op_type == "MatMul" and n.input[1] in fs and n.input[0] not in fs and len(fs.shape(n.input[1])) == 2:
            groups["matmul"].append((n.name, n.input[1]))
        elif args.pointwise and pointwise_conv_rank(n, fs) == 3:
            groups["pointwise"].append((n.name, n.input[1]))

    shipped = {}
    if args.shipped:
        sf = ModelFolder.detect(Path(args.shipped))
        sm = onnx.load(str(sf.encoder_path), load_external_data=False)
        ss = WeightStore(sm, sf.path)
        for n in sm.graph.node:
            if n.op_type == "MatMulNBits":
                a = {x.name: onnx.helper.get_attribute_value(x) for x in n.attribute}
                shipped[n.name.removesuffix("_Q4")] = (n, a, ss)

    combos = [(r, b) for r in args.recipes for b in args.blocks]
    rows = []
    totals: dict[str, list[float]] = {}
    for group, items in groups.items():
        for name, w_name in items[: args.limit]:
            w = fs.array(w_name).astype(np.float32)
            if group == "pointwise":
                w = w.reshape(w.shape[0], w.shape[1]).T  # [Cin, Cout], the MatMul view
            k, n_cols = w.shape
            row = {"group": group, "node": name, "K": k, "N": n_cols}
            for recipe, block in combos:
                packed, scales, zp = quantize_weight(w, recipe, block)
                key = f"{recipe}/{block}"
                row[key] = rel_rmse(w, dequantize(packed, scales, zp, k, n_cols, block))
                totals.setdefault(f"{group}:{key}", []).append(row[key])
                if name in shipped and recipe == "k_quant" and block == shipped[name][1].get("block_size"):
                    node, attrs, ss = shipped[name]
                    sp = ss.array(node.input[1]).reshape(packed.shape)
                    sc = ss.array(node.input[2]).reshape(scales.shape)
                    sz = ss.array(node.input[3]).reshape(zp.shape) if len(node.input) > 3 and zp is not None else None
                    row["kq_bytes_equal_shipped"] = bool(np.array_equal(sp, packed) and np.array_equal(sc, scales)
                                                         and (sz is None or np.array_equal(sz, zp)))
                    row["kq_packed_equal_fraction"] = float(np.mean(sp == packed))
            if name in shipped:
                node, attrs, ss = shipped[name]
                b = attrs["block_size"]
                zp_s = ss.array(node.input[3]) if len(node.input) > 3 and node.input[3] else None
                w_hat = dequantize(ss.array(node.input[1]), ss.array(node.input[2]), zp_s, k, n_cols, b)
                row["shipped"] = rel_rmse(w, w_hat)
                totals.setdefault(f"{group}:shipped", []).append(row["shipped"])
            rows.append(row)
        print(f"{group}: {min(len(items), args.limit or len(items))} weights scored", flush=True)

    print(f"\nrelative RMS weight error (RMS over weights), FP32 = {fp.path.name}")
    for key, vals in sorted(totals.items()):
        print(f"  {key:30s} {np.sqrt(np.mean(np.square(vals))):.5f}  (n={len(vals)})")
    eq = [r["kq_bytes_equal_shipped"] for r in rows if "kq_bytes_equal_shipped" in r]
    if eq:
        frac = [r["kq_packed_equal_fraction"] for r in rows if "kq_packed_equal_fraction" in r]
        print(f"  k_quant reproduces the shipped MatMulNBits byte for byte: {sum(eq)} / {len(eq)} weights "
              f"(packed nibbles equal on average {np.mean(frac):.4%})")
    if args.json:
        Path(args.json).write_text(json.dumps(rows, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
