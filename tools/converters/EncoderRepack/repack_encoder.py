"""Repack a FastConformer ASR encoder (Nemotron streaming, Parakeet TDT) into a smaller INT4 model folder.

Olive's k-quant pass and onnxruntime's MatMulNBitsQuantizer quantise MatMul only, so the 48 pointwise (kernel 1)
Conv layers of the conformer convolution modules stay FP32 in the shipped INT4 encoders: 302 MB of the ~690 MB of
encoder weights. This tool, working on the exported graph (no re-export, no PyTorch):

  1. folds constants (Nemotron): the Slice of the 41 MB relative-position table becomes the 125 / 139 rows it reads,
     and the 24 `linear_pos` projections of that constant become constants too (ORT folds the Slice at load, but not
     the MatMulNBits after it, so every chunk recomputes them);
  2. rewrites every pointwise Conv as Transpose -> MatMul -> Transpose and cancels the Transpose pairs this creates
     with the transposes the exporter put around the conv module;
  3. checks numerically, on real audio, that the rewritten graph (pointwise weights still FP32) matches the source
     encoder, before anything is quantised;
  4. quantises the new MatMul weights (and, for an FP32 source, every MatMul weight) to INT4 MatMulNBits with the
     chosen recipe; `match` reproduces the scheme of the source graph's existing MatMulNBits (block size, accuracy
     level, zero points);
  5. writes a new model folder: the new encoder plus every other file of the source folder, copied unchanged, and
     `repack-info.json` (recipe, census before / after, equivalence numbers, sizes).

Usage:
    python repack_encoder.py --model-dir <model folder> --out-dir <new folder> [--recipe match]
        [--bits 4|8] [--block-size N] [--accuracy-level N] [--fold-pos full|table|none] [--no-pointwise]
        [--include-2d]
        [--verify-audio <wav or folder> ...] [--skip-verify] [--threads 4]
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort

from encoder_check import audio_files, compare_encoders
from graph_surgery import (
    cancel_transpose_pairs,
    census,
    fold_constant_projections,
    fold_constant_slices,
    rewrite_pointwise_convs,
)
from model_folder import ModelFolder, WeightStore, copy_folder_except, folder_size, save_encoder
from quant_recipes import RECIPES, quantize_graph

# INT4 scheme of the shipped encoders, used by `--recipe match` when the source encoder is FP32.
SHIPPED_SCHEME = {
    "nemotron": ("k_quant", 32, 4),     # Olive OnnxKQuantQuantization, block 32, accuracy_level 4, zero points
    "parakeet": ("default_sym", 32, 0),  # MatMulNBitsQuantizer default, block 32, symmetric, no accuracy_level
    "generic": ("default_sym", 32, 0),
}


def resolve_recipe(args, scheme: dict | None, kind: str) -> tuple[str, int, int]:
    if scheme is None:
        recipe, block, acc = SHIPPED_SCHEME[kind]
    else:
        recipe = "k_quant" if scheme["zero_points"] else "default_sym"
        block, acc = scheme["block_size"], scheme["accuracy_level"]
    if args.recipe != "match":
        recipe = args.recipe
    if args.block_size:
        block = args.block_size
    if args.accuracy_level is not None:
        acc = args.accuracy_level
    return recipe, block, acc


def log(msg: str) -> None:
    print(msg, flush=True)


def set_accuracy_level(graph: onnx.GraphProto, level: int) -> dict[str, int | None]:
    """Sets accuracy_level on every MatMulNBits; returns the previous values (None = attribute absent)."""
    previous = {}
    for n in graph.node:
        if n.op_type != "MatMulNBits":
            continue
        attr = next((a for a in n.attribute if a.name == "accuracy_level"), None)
        previous[n.name] = attr.i if attr is not None else None
        if attr is None:
            n.attribute.append(onnx.helper.make_attribute("accuracy_level", level))
        else:
            attr.i = level
    return previous


def restore_accuracy_level(graph: onnx.GraphProto, previous: dict[str, int | None]) -> None:
    for n in graph.node:
        if n.name not in previous:
            continue
        kept = [a for a in n.attribute if a.name != "accuracy_level"]
        if previous[n.name] is not None:
            kept.append(onnx.helper.make_attribute("accuracy_level", previous[n.name]))
        del n.attribute[:]
        n.attribute.extend(kept)


def describe(stats: dict) -> str:
    return (f"max|diff| {stats['max_abs_diff']:.3g} (ref max {stats['ref_max_abs']:.3g}), "
            f"rel RMS {stats['rel_rms_diff']:.3g}, cosine {stats['cosine']:.9f}, bit-identical {stats['bit_identical']}")


class Checker:
    """Saves intermediate graphs into a stage folder and compares them on the verification audio."""

    def __init__(self, folder: ModelFolder, files: list[Path], threads: int, stage: Path):
        self.folder, self.files, self.threads, self.stage = folder, files, threads, stage

    def save(self, model: onnx.ModelProto, store: WeightStore, tag: str, fp32_activations: bool = False) -> Path:
        """Writes the working graph; with fp32_activations every MatMulNBits gets accuracy_level 0 in the copy."""
        path = self.stage / tag / self.folder.encoder_file
        previous = set_accuracy_level(model.graph, 0) if fp32_activations else None
        try:
            save_encoder(model, store, path, external=True)
        finally:
            if previous is not None:
                restore_accuracy_level(model.graph, previous)
        return path

    def compare(self, reference: Path, candidate: Path) -> dict:
        return compare_encoders(self.folder, reference, candidate, self.files, self.threads)

    def drop(self, path: Path) -> None:
        shutil.rmtree(path.parent, ignore_errors=True)

    def close(self) -> None:
        shutil.rmtree(self.stage, ignore_errors=True)


def has_int8_activations(graph: onnx.GraphProto) -> bool:
    return any(a.name == "accuracy_level" and a.i != 0
               for n in graph.node if n.op_type == "MatMulNBits" for a in n.attribute)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model-dir", required=True, help="source model folder (shipped INT4 or FP32)")
    ap.add_argument("--out-dir", required=True, help="new model folder (must not exist, or pass --overwrite)")
    ap.add_argument("--encoder-file", help="encoder file name when the folder type cannot be detected")
    ap.add_argument("--recipe", default="match", choices=("match",) + RECIPES,
                    help="INT4 recipe of the new MatMulNBits (default: match the source graph's scheme)")
    ap.add_argument("--bits", type=int, default=4, choices=(4, 8),
                    help="bits of the new MatMulNBits weights (default 4; 8 = INT8 MatMulNBits)")
    ap.add_argument("--block-size", type=int, help="quantisation block size (default: the source graph's, else 32)")
    ap.add_argument("--accuracy-level", type=int,
                    help="MatMulNBits accuracy_level (default: the source graph's; 0 = attribute omitted)")
    ap.add_argument("--fold-pos", default="full", choices=("full", "table", "none"),
                    help="full: fold the position-table Slice and the projections of it (default); table: the "
                         "Slice only; none: keep both")
    ap.add_argument("--no-pointwise", action="store_true", help="keep the pointwise Convs (folds only)")
    ap.add_argument("--include-2d", action="store_true",
                    help="also rewrite the 1x1 Conv2d of the subsampling stack (0.26 MB each; kept by default)")
    ap.add_argument("--layout", default="source", choices=("source", "single", "external"),
                    help="encoder file layout: like the source (default), one file, or graph + .data file")
    ap.add_argument("--verify-audio", nargs="+", help=".wav files or folders for the equivalence check")
    ap.add_argument("--verify-files", type=int, default=2, help="files taken from each verify folder (default 2)")
    ap.add_argument("--max-rel-rms", type=float, default=1e-4,
                    help="gate of the pointwise rewrite (before quantising): relative RMS difference of all encoder "
                         "outputs, FP32 activations (default 1e-4)")
    ap.add_argument("--max-rel-rms-folds", type=float, default=1e-5,
                    help="gate of the constant folds: relative RMS difference vs the source (default 1e-5)")
    ap.add_argument("--skip-verify", action="store_true", help="no equivalence check (not recommended)")
    ap.add_argument("--force", action="store_true", help="write the folder even if the equivalence gate fails")
    ap.add_argument("--threads", type=int, default=4, help="ORT intra-op threads for the checks (default 4)")
    ap.add_argument("--overwrite", action="store_true", help="replace an existing --out-dir")
    args = ap.parse_args()

    t_start = time.time()
    folder = ModelFolder.detect(Path(args.model_dir), args.encoder_file)
    out = Path(args.out_dir).resolve()
    if out == folder.path or folder.path in out.parents or out in folder.path.parents:
        raise SystemExit("--out-dir must be a separate folder (not the source folder, not inside it)")
    if out.exists() and any(out.iterdir()):
        if not args.overwrite:
            raise SystemExit(f"{out} exists and is not empty (pass --overwrite)")
        shutil.rmtree(out)
    if not args.skip_verify and not args.verify_audio:
        raise SystemExit("--verify-audio is required (or --skip-verify)")

    source_data = folder.encoder_data_files()
    model = onnx.load(str(folder.encoder_path), load_external_data=False)
    store = WeightStore(model, folder.path)
    before = census(model, store)
    log(f"[source] {folder.path} ({folder.kind}), encoder {folder.encoder_file}"
        f"{' + ' + ', '.join(p.name for p in source_data) if source_data else ''}")
    log(before.summary())
    recipe, block, acc = resolve_recipe(args, before.scheme(), folder.kind)
    log(f"[recipe] {recipe}, {args.bits} bits, block {block}, accuracy_level {acc}")
    info: dict = {
        "source": str(folder.path), "kind": folder.kind, "encoder": folder.encoder_file,
        "recipe": recipe, "bits": args.bits, "block_size": block, "accuracy_level": acc,
        "fold_pos": args.fold_pos, "pointwise": not args.no_pointwise, "include_2d": args.include_2d,
        "versions": {"onnx": onnx.__version__, "onnxruntime": ort.__version__, "numpy": np.__version__,
                     "python": sys.version.split()[0]},
        "census_before": before.as_dict(),
    }

    files = audio_files(args.verify_audio, args.verify_files) if not args.skip_verify else []
    checker = None if args.skip_verify else Checker(folder, files, args.threads, out / ".stage")
    enc_name = folder.encoder_file

    # 1. constant folds
    g = model.graph
    if args.fold_pos in ("table", "full"):
        info["folded_slices"] = fold_constant_slices(g, store)
        for f in info["folded_slices"]:
            log(f"[fold] {f['node']}: {f['table']} {f['table_shape']} -> {f['kept_shape']} "
                f"({f['table_bytes'] / 1e6:.1f} -> {f['kept_bytes'] / 1e6:.2f} MB)")
    if args.fold_pos == "full":
        info["folded_projections"] = fold_constant_projections(g, store, model.opset_import)
        if info["folded_projections"]:
            w = sum(f["weight_bytes"] for f in info["folded_projections"])
            c = sum(f["folded_bytes"] for f in info["folded_projections"])
            log(f"[fold] {len(info['folded_projections'])} constant projections -> constants "
                f"(weights {w / 1e6:.1f} MB -> folded outputs {c / 1e6:.1f} MB)")

    def fail(what: str, stats: dict, limit: float) -> int:
        log(f"[check] FAILED: {what} rel RMS {stats['rel_rms_diff']:.3g} > {limit:g}; nothing written")
        checker.close()
        if out.exists() and not any(out.iterdir()):
            out.rmdir()
        return 2

    # Gate A: the folds are evaluated with the encoder's own kernels, so the outputs must not move at all.
    folded = bool(info.get("folded_slices") or info.get("folded_projections"))
    if checker and folded:
        path = checker.save(model, store, "folded")
        info["check_folds"] = checker.compare(folder.encoder_path, path)
        checker.drop(path)
        log(f"[check] folded graph vs source: {describe(info['check_folds'])}")
        if info["check_folds"]["rel_rms_diff"] > args.max_rel_rms_folds and not args.force:
            return fail("folds", info["check_folds"], args.max_rel_rms_folds)

    # 2. pointwise Conv -> MatMul
    if not args.no_pointwise:
        # Gate B compares the graph just before and just after the rewrite. With accuracy_level 4 the MatMulNBits
        # quantise their activations to int8 per block: a last-bit difference (Conv and MatMul sum in another order)
        # can flip an int8 rounding and grow through 24 layers and the caches. Both sides of the gate therefore run
        # with accuracy_level 0 (FP32 activations, same INT4 weights), where an exact rewrite differs by float
        # rounding only; the as-is difference is recorded for information.
        int8_act = has_int8_activations(g)
        reference = checker.save(model, store, "before", fp32_activations=int8_act) if checker else None
        info["pointwise_matmuls"] = rewrite_pointwise_convs(g, store, args.include_2d)
        info["transposes_removed"] = cancel_transpose_pairs(g)
        log(f"[surgery] {len(info['pointwise_matmuls'])} pointwise Conv -> MatMul; "
            f"{info['transposes_removed']} Transpose nodes cancelled; {len(g.node)} nodes")

        # 3. equivalence gate on the FP32 path (rewritten graph, pointwise weights not yet quantised)
        if checker:
            candidate = checker.save(model, store, "after", fp32_activations=int8_act)
            gate = checker.compare(reference, candidate)
            checker.drop(reference)
            checker.drop(candidate)
            info["check_rewrite"] = {"fp32_activations": int8_act, **gate}
            log(f"[check] rewritten graph vs graph before the rewrite, pointwise weights FP32"
                f"{', both with FP32 activations (accuracy_level 0)' if int8_act else ''}: {describe(gate)}")
            if int8_act:
                path = checker.save(model, store, "as_is")
                info["check_rewrite_as_is"] = checker.compare(folder.encoder_path, path)
                checker.drop(path)
                log(f"[check] (information) the same, graphs as they are, vs source: "
                    f"{describe(info['check_rewrite_as_is'])}")
            if gate["rel_rms_diff"] > args.max_rel_rms and not args.force:
                return fail("rewrite", gate, args.max_rel_rms)

    # 4. quantise
    report = quantize_graph(g, store, recipe, block, acc, bits=args.bits)
    info["quantized"] = report
    if report:
        fp = sum(r["fp32_bytes"] for r in report)
        q = sum(r["quantized_bytes"] for r in report)
        err = np.sqrt(np.mean([r["rel_rmse"] ** 2 for r in report]))
        log(f"[quant] {len(report)} MatMul -> MatMulNBits: {fp / 1e6:.1f} MB FP32 -> {q / 1e6:.1f} MB "
            f"(weight rel RMSE {err:.4f})")

    # 5. write the folder
    if checker:
        checker.close()
    external =bool(source_data) if args.layout == "source" else args.layout == "external"
    data_name = (source_data[0].name if source_data else enc_name + ".data") if external else None
    sizes = save_encoder(model, store, out / enc_name, external, data_name)
    skip = {enc_name, "repack-info.json"} | {p.name for p in source_data}
    info["copied_files"] = copy_folder_except(folder.path, out, skip)
    after_model = onnx.load(str(out / enc_name), load_external_data=False)
    after = census(after_model, WeightStore(after_model, out))
    del after_model
    info["census_after"] = after.as_dict()
    enc_bytes = sizes["graph_bytes"] + sizes.get("data_bytes", 0)
    src_enc_bytes = folder.encoder_path.stat().st_size + sum(p.stat().st_size for p in source_data)
    info["sizes"] = {"encoder_bytes_source": src_enc_bytes, "encoder_bytes": enc_bytes,
                     "folder_bytes_source": folder_size(folder.path), **sizes}
    log(f"[write] {out / enc_name}: {src_enc_bytes / 1e6:.1f} -> {enc_bytes / 1e6:.1f} MB")
    log(after.summary())

    # 6. what the quantisation did to the outputs (information, no gate)
    if not args.skip_verify:
        fin = compare_encoders(folder, folder.encoder_path, out / enc_name, files, args.threads)
        info["final_vs_source"] = fin
        log(f"[check] (information) repacked encoder vs source: {describe(fin)}")

    info["seconds"] = round(time.time() - t_start, 1)
    info["sizes"]["folder_bytes"] = 0
    (out / "repack-info.json").write_text(json.dumps(info, indent=2), encoding="utf-8")
    info["sizes"]["folder_bytes"] = folder_size(out)
    (out / "repack-info.json").write_text(json.dumps(info, indent=2), encoding="utf-8")
    log(f"[done] {out} ({info['sizes']['folder_bytes'] / 1e6:.1f} MB folder, "
        f"source {info['sizes']['folder_bytes_source'] / 1e6:.1f} MB) in {info['seconds']} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
