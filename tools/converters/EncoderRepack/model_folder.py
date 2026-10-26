"""Model folders and encoder weights: detection, lazy weight reads, writing a repacked encoder.

A Nemotron streaming folder is recognised by `genai_config.json` (encoder file name from `model.encoder.filename`,
`encoder.onnx` + `encoder.onnx.data`), a Parakeet TDT folder by `config.json` with `model_type: nemo-conformer-tdt`
(`encoder-model.onnx`, a single file in the shipped INT4 / INT8 folders, external data in the FP32 one).

The encoder is loaded WITHOUT its external data: `WeightStore` reads one initializer at a time from the data file, so
a 2.5 GB FP32 encoder never sits in memory as a whole, and `save_encoder` streams the output data file tensor by tensor.
"""

from __future__ import annotations

import json
import shutil
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

# Tensors at least this large go to the external data file (onnx's own default threshold).
EXTERNAL_THRESHOLD = 1024


@dataclass
class ModelFolder:
    """An ASR model folder and the role of its files."""

    path: Path
    kind: str  # "nemotron" | "parakeet" | "generic"
    encoder_file: str
    config: dict

    @property
    def encoder_path(self) -> Path:
        return self.path / self.encoder_file

    @staticmethod
    def detect(path: Path, encoder_file: str | None = None) -> ModelFolder:
        path = Path(path).resolve()
        if not path.is_dir():
            raise SystemExit(f"Model folder not found: {path}")
        genai = path / "genai_config.json"
        config = path / "config.json"
        if genai.exists():
            cfg = json.loads(genai.read_text(encoding="utf-8"))
            name = encoder_file or cfg["model"]["encoder"]["filename"]
            return ModelFolder(path, "nemotron", name, cfg)
        if config.exists():
            cfg = json.loads(config.read_text(encoding="utf-8"))
            if cfg.get("model_type") == "nemo-conformer-tdt":
                return ModelFolder(path, "parakeet", encoder_file or "encoder-model.onnx", cfg)
        if encoder_file is None:
            raise SystemExit(f"{path}: neither genai_config.json (Nemotron) nor a nemo-conformer-tdt config.json "
                             "(Parakeet); pass --encoder-file")
        return ModelFolder(path, "generic", encoder_file, {})

    def encoder_data_files(self) -> list[Path]:
        """External data files the encoder graph references (empty for a single-file encoder)."""
        model = onnx.load(str(self.encoder_path), load_external_data=False)
        names = {
            e.value
            for t in model.graph.initializer
            if t.data_location == TensorProto.EXTERNAL
            for e in t.external_data
            if e.key == "location"
        }
        return [self.path / n for n in sorted(names)]


def _external_info(t: TensorProto) -> dict[str, str]:
    return {e.key: e.value for e in t.external_data}


class WeightStore:
    """Initializer values of an encoder loaded without its external data.

    Source tensors are read on demand (from the data file or from the in-proto raw data); tensors created by the
    repack (`add`) live in memory until the encoder is saved.
    """

    def __init__(self, model: onnx.ModelProto, base_dir: Path):
        self.base_dir = Path(base_dir)
        self.source: dict[str, TensorProto] = {t.name: t for t in model.graph.initializer}
        self.new: dict[str, np.ndarray] = {}

    def __contains__(self, name: str) -> bool:
        return name in self.new or name in self.source

    def names(self) -> set[str]:
        return set(self.source) | set(self.new)

    def add(self, name: str, value: np.ndarray) -> None:
        self.new[name] = np.ascontiguousarray(value)

    def shape(self, name: str) -> tuple[int, ...]:
        if name in self.new:
            return tuple(self.new[name].shape)
        return tuple(self.source[name].dims)

    def dtype(self, name: str) -> np.dtype:
        if name in self.new:
            return self.new[name].dtype
        return np.dtype(helper.tensor_dtype_to_np_dtype(self.source[name].data_type))

    def nbytes(self, name: str) -> int:
        if name in self.new:
            return int(self.new[name].nbytes)
        t = self.source[name]
        if t.data_location == TensorProto.EXTERNAL:
            return int(_external_info(t).get("length", 0))
        return len(self.raw_bytes(name))

    def array(self, name: str) -> np.ndarray:
        if name in self.new:
            return self.new[name]
        t = self.source[name]
        if t.data_location == TensorProto.EXTERNAL:
            info = _external_info(t)
            dtype = np.dtype(helper.tensor_dtype_to_np_dtype(t.data_type))
            count = int(np.prod(t.dims)) if len(t.dims) else 1
            with open(self.base_dir / info["location"], "rb") as f:
                f.seek(int(info.get("offset", 0)))
                data = np.fromfile(f, dtype=dtype, count=count)
            return data.reshape(tuple(t.dims))
        return numpy_helper.to_array(t)

    def raw_bytes(self, name: str) -> bytes:
        if name in self.new:
            return self.new[name].tobytes()
        t = self.source[name]
        if t.data_location == TensorProto.EXTERNAL:
            info = _external_info(t)
            with open(self.base_dir / info["location"], "rb") as f:
                f.seek(int(info.get("offset", 0)))
                return f.read(int(info["length"]))
        if t.HasField("raw_data"):
            return t.raw_data
        return numpy_helper.to_array(t).tobytes()

    def tensor_type(self, name: str) -> int:
        if name in self.new:
            return helper.np_dtype_to_tensor_dtype(self.new[name].dtype)
        return self.source[name].data_type


def used_initializers(graph: onnx.GraphProto, store: WeightStore) -> list[str]:
    """Initializers the graph still references: source order first, then the tensors the repack created."""
    referenced = {i for n in graph.node for i in n.input if i}
    referenced |= {o.name for o in graph.output}
    order = [n for n in store.source if n in referenced and n not in store.new]
    order += [n for n in store.new if n in referenced]
    return order


def save_encoder(model: onnx.ModelProto, store: WeightStore, out_path: Path, external: bool,
                 data_name: str | None = None) -> dict:
    """Writes the working graph with the initializers it still uses; returns the file sizes.

    The source model's initializer list is not touched: a fresh model is built around the working graph's nodes,
    every tensor is copied byte for byte (source tensors from their data file, new tensors from memory).
    """
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    data_name = data_name or (out_path.name + ".data")
    data_path = out_path.parent / data_name
    g = model.graph
    for n in g.node:
        for a in n.attribute:
            if a.type in (onnx.AttributeProto.GRAPH, onnx.AttributeProto.GRAPHS):
                raise SystemExit(f"Node {n.name} has a subgraph; the repack handles flat encoder graphs only")

    tensors = []
    data_file = open(data_path, "wb") if external else None
    try:
        for name in used_initializers(g, store):
            t = TensorProto()
            t.name = name
            t.data_type = store.tensor_type(name)
            t.dims.extend(store.shape(name))
            raw = store.raw_bytes(name)
            if data_file is not None and len(raw) >= EXTERNAL_THRESHOLD:
                offset = data_file.tell()
                data_file.write(raw)
                t.data_location = TensorProto.EXTERNAL
                for key, value in (("location", data_name), ("offset", str(offset)), ("length", str(len(raw)))):
                    entry = t.external_data.add()
                    entry.key = key
                    entry.value = value
            else:
                t.raw_data = raw
            tensors.append(t)
    finally:
        if data_file is not None:
            data_file.close()

    names = {t.name for t in tensors}
    produced = {o for n in g.node for o in n.output}
    graph = helper.make_graph(
        nodes=list(g.node),
        name=g.name,
        inputs=list(g.input),
        outputs=list(g.output),
        initializer=tensors,
        # shape annotations of tensors that the surgery removed or folded into constants are dropped
        value_info=[v for v in g.value_info if v.name in produced and v.name not in names],
        doc_string=g.doc_string,
    )
    out = helper.make_model(graph, opset_imports=list(model.opset_import), ir_version=model.ir_version,
                            producer_name=model.producer_name, producer_version=model.producer_version)
    out.domain = model.domain
    out.model_version = model.model_version
    out.doc_string = model.doc_string
    out.metadata_props.extend(model.metadata_props)
    onnx.save(out, str(out_path))
    sizes = {"graph_bytes": out_path.stat().st_size}
    if external:
        sizes["data_bytes"] = data_path.stat().st_size
        sizes["data_file"] = data_name
    return sizes


def copy_folder_except(src: Path, dst: Path, skip: set[str]) -> list[str]:
    """Copies every file of the model folder except `skip` (the encoder files), unchanged."""
    dst.mkdir(parents=True, exist_ok=True)
    copied = []
    for item in sorted(src.iterdir()):
        if item.name in skip:
            continue
        if item.is_dir():
            shutil.copytree(item, dst / item.name, dirs_exist_ok=True)
        else:
            shutil.copy2(item, dst / item.name)
        copied.append(item.name)
    return copied


def folder_size(path: Path) -> int:
    return sum(p.stat().st_size for p in Path(path).rglob("*") if p.is_file())
