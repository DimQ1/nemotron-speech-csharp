"""Flatten Olive's nested output into the deployment layout expected by the app.

Olive >= 0.7 writes converted models to:
    <output_dir>/<component>.onnx/output_model/model/model.onnx  (+ <component>.onnx.data)

The application (onnxruntime-genai / ModelSession) expects a flat directory:
    <output_dir>/encoder.onnx, decoder.onnx, joint.onnx (+ matching .data files)

This script moves the generated files into place and removes the nested dirs.
"""

import shutil
import sys
from pathlib import Path

COMPONENTS = {
    "encoder.onnx": ("encoder.onnx", "encoder.onnx.data"),
    "decoder.onnx": ("decoder.onnx", "decoder.onnx.data"),
    "joint.onnx": ("joint.onnx", "joint.onnx.data"),
}


def flatten(build_dir: Path) -> None:
    for comp_dir_name, (model_name, data_name) in COMPONENTS.items():
        nested = build_dir / comp_dir_name / "output_model" / "model"
        model_src = nested / "model.onnx"
        data_src = nested / data_name
        model_dst = build_dir / comp_dir_name  # e.g. .../encoder.onnx  (final file)
        data_dst = build_dir / data_name

        if not model_src.exists():
            print(f"[skip] {comp_dir_name}: {model_src} not found")
            continue

        # Remove the placeholder dir that currently occupies the target name.
        target_dir = build_dir / comp_dir_name
        # Move model.onnx -> <comp>.onnx and <comp>.onnx.data alongside.
        tmp_model = build_dir / (comp_dir_name + ".tmp")
        shutil.copy2(model_src, tmp_model)
        if data_src.exists():
            shutil.copy2(data_src, data_dst)

        # Now remove the nested component directory and rename tmp into place.
        shutil.rmtree(target_dir)
        tmp_model.rename(model_dst)
        size_mb = model_dst.stat().st_size / 1e6
        data_mb = data_dst.stat().st_size / 1e6 if data_dst.exists() else 0
        print(f"[ok] {comp_dir_name}: model {size_mb:.1f} MB + data {data_mb:.1f} MB")


if __name__ == "__main__":
    build = Path(sys.argv[1]).resolve()
    flatten(build)
    print(f"\nFlattened -> {build}")
    for f in sorted(build.iterdir()):
        if f.is_file():
            print(f"  {f.name} ({f.stat().st_size/1e6:.1f} MB)")
