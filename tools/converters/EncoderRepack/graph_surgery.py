"""Graph surgery on a FastConformer encoder: pointwise Conv -> MatMul, Transpose-pair cancellation, constant folds.

All functions work on the graph proto in place and keep weights in a `WeightStore` (no external data in memory).
"""

from __future__ import annotations

import collections
from dataclasses import dataclass, field

import numpy as np
import onnx
import onnxruntime as ort
from onnx import TensorProto, helper

from model_folder import WeightStore


def _attrs(node: onnx.NodeProto) -> dict:
    return {a.name: helper.get_attribute_value(a) for a in node.attribute}


def _replace_nodes(graph: onnx.GraphProto, nodes: list[onnx.NodeProto]) -> None:
    copies = []
    for n in nodes:
        c = onnx.NodeProto()
        c.CopyFrom(n)
        copies.append(c)
    del graph.node[:]
    graph.node.extend(copies)


class _Namer:
    def __init__(self, graph: onnx.GraphProto, store: WeightStore):
        self.used = {o for n in graph.node for o in n.output} | {i for n in graph.node for i in n.input}
        self.used |= {n.name for n in graph.node} | store.names()
        self.used |= {i.name for i in graph.input} | {o.name for o in graph.output}

    def __call__(self, base: str) -> str:
        name, k = base, 1
        while name in self.used:
            name = f"{base}_{k}"
            k += 1
        self.used.add(name)
        return name


# ---------------------------------------------------------------------------------------------------- census


@dataclass
class Census:
    nodes: int = 0
    ops: collections.Counter = field(default_factory=collections.Counter)
    nbits: int = 0
    nbits_block: collections.Counter = field(default_factory=collections.Counter)
    nbits_accuracy: collections.Counter = field(default_factory=collections.Counter)
    nbits_with_zero_points: int = 0
    nbits_bytes: int = 0
    fp32_matmul_weights: int = 0
    fp32_matmul_bytes: int = 0
    pointwise_convs: int = 0
    pointwise_conv_bytes: int = 0
    pointwise_convs_2d: int = 0
    other_conv_bytes: int = 0
    constant_slice_tables: list = field(default_factory=list)
    initializer_bytes: int = 0

    def as_dict(self) -> dict:
        d = dict(self.__dict__)
        d["ops"] = dict(self.ops.most_common())
        d["nbits_block"] = dict(self.nbits_block)
        d["nbits_accuracy"] = {str(k): v for k, v in self.nbits_accuracy.items()}
        return d

    def scheme(self) -> dict | None:
        """The INT4 scheme the existing MatMulNBits nodes use (majority), or None for an FP32 encoder."""
        if not self.nbits:
            return None
        return {
            "block_size": self.nbits_block.most_common(1)[0][0],
            "accuracy_level": self.nbits_accuracy.most_common(1)[0][0],
            "zero_points": self.nbits_with_zero_points * 2 > self.nbits,
        }

    def summary(self) -> str:
        mb = 1e6
        lines = [
            f"nodes {self.nodes}; initializers {self.initializer_bytes / mb:.1f} MB",
            f"MatMulNBits {self.nbits} ({self.nbits_bytes / mb:.1f} MB; block {dict(self.nbits_block)}, "
            f"accuracy_level {dict(self.nbits_accuracy)}, with zero points {self.nbits_with_zero_points})",
            f"FP32 MatMul with a constant weight {self.fp32_matmul_weights} ({self.fp32_matmul_bytes / mb:.1f} MB)",
            f"pointwise Conv (k=1) {self.pointwise_convs} 1-D + {self.pointwise_convs_2d} 2-D "
            f"({self.pointwise_conv_bytes / mb:.1f} MB FP32); other Conv weights {self.other_conv_bytes / mb:.1f} MB",
        ]
        for name, dims, nbytes in self.constant_slice_tables:
            lines.append(f"constant Slice table {name} {dims} = {nbytes / mb:.1f} MB")
        return "\n".join(lines)


def pointwise_conv_rank(node: onnx.NodeProto, store: WeightStore) -> int | None:
    """3 for a 1-D, 4 for a 2-D pointwise Conv that is a plain matrix product over the channels, else None."""
    if node.op_type != "Conv" or len(node.input) < 2 or node.input[1] not in store:
        return None
    if len(node.input) > 2 and node.input[2] and node.input[2] not in store:
        return None
    shape = store.shape(node.input[1])
    if len(shape) not in (3, 4) or any(d != 1 for d in shape[2:]):
        return None
    a = _attrs(node)
    if a.get("group", 1) != 1:
        return None
    if any(s != 1 for s in a.get("strides", [])) or any(d != 1 for d in a.get("dilations", [])):
        return None
    if any(p != 0 for p in a.get("pads", [])):
        return None
    auto_pad = a.get("auto_pad", b"NOTSET")
    if isinstance(auto_pad, bytes):
        auto_pad = auto_pad.decode()
    if auto_pad not in ("NOTSET", "VALID", "SAME_UPPER", "SAME_LOWER"):
        return None
    return len(shape)


def census(model: onnx.ModelProto, store: WeightStore) -> Census:
    g = model.graph
    c = Census(nodes=len(g.node), ops=collections.Counter(n.op_type for n in g.node))
    c.initializer_bytes = sum(store.nbytes(n) for n in store.source)
    for n in g.node:
        if n.op_type == "MatMulNBits":
            a = _attrs(n)
            c.nbits += 1
            c.nbits_block[a.get("block_size")] += 1
            c.nbits_accuracy[a.get("accuracy_level", 0)] += 1
            if len(n.input) > 3 and n.input[3]:
                c.nbits_with_zero_points += 1
            c.nbits_bytes += sum(store.nbytes(i) for i in n.input[1:] if i and i in store)
        elif n.op_type == "MatMul" and n.input[1] in store and len(store.shape(n.input[1])) == 2 \
                and n.input[0] not in store:
            c.fp32_matmul_weights += 1
            c.fp32_matmul_bytes += store.nbytes(n.input[1])
        elif n.op_type == "Conv" and n.input[1] in store:
            rank = pointwise_conv_rank(n, store)
            if rank == 3:
                c.pointwise_convs += 1
                c.pointwise_conv_bytes += store.nbytes(n.input[1])
            elif rank == 4:
                c.pointwise_convs_2d += 1
                c.pointwise_conv_bytes += store.nbytes(n.input[1])
            else:
                c.other_conv_bytes += store.nbytes(n.input[1])
        elif n.op_type == "Slice" and all((not i) or i in store for i in n.input):
            c.constant_slice_tables.append((n.input[0], list(store.shape(n.input[0])), store.nbytes(n.input[0])))
    return c


# ---------------------------------------------------------------------------------------------------- pointwise convs


def rewrite_pointwise_convs(graph: onnx.GraphProto, store: WeightStore, include_2d: bool) -> list[str]:
    """Replaces every pointwise Conv by Transpose -> MatMul (-> Add bias) -> Transpose.

    The weight [Cout, Cin, 1(, 1)] becomes a [Cin, Cout] MatMul weight on the channels-last view, which the
    MatMulNBits quantiser can handle; `cancel_transpose_pairs` then removes the transposes that undo the ones the
    exporter put around the conv module. Returns the names of the new MatMul nodes.
    """
    namer = _Namer(graph, store)
    new_nodes: list[onnx.NodeProto] = []
    matmuls: list[str] = []
    for node in graph.node:
        rank = pointwise_conv_rank(node, store)
        if rank is None or (rank == 4 and not include_2d):
            new_nodes.append(node)
            continue
        w = store.array(node.input[1]).astype(np.float32)
        cout, cin = w.shape[:2]
        base = node.name or node.output[0]
        w_name = namer(node.input[1] + "_matmul")
        store.add(w_name, np.ascontiguousarray(w.reshape(cout, cin).T))
        perm_in, perm_out = ([0, 2, 1], [0, 2, 1]) if rank == 3 else ([0, 2, 3, 1], [0, 3, 1, 2])
        x_t = namer(base + "_cl")
        mm_out = namer(base + "_matmul_out")
        mm_name = namer(base + "_MatMul")
        new_nodes.append(helper.make_node("Transpose", [node.input[0]], [x_t], name=namer(base + "_to_cl"), perm=perm_in))
        new_nodes.append(helper.make_node("MatMul", [x_t, w_name], [mm_out], name=mm_name))
        last = mm_out
        if len(node.input) > 2 and node.input[2]:
            add_out = namer(base + "_bias_out")
            new_nodes.append(helper.make_node("Add", [last, node.input[2]], [add_out], name=namer(base + "_bias")))
            last = add_out
        new_nodes.append(helper.make_node("Transpose", [last], [node.output[0]], name=namer(base + "_to_cf"), perm=perm_out))
        matmuls.append(mm_name)
    _replace_nodes(graph, new_nodes)
    return matmuls


def cancel_transpose_pairs(graph: onnx.GraphProto) -> int:
    """Removes Transpose(Transpose(x, p1), p2) when p2 undoes p1; returns the number of Transpose nodes removed."""
    graph_outputs = {o.name for o in graph.output}
    removed = 0
    while True:
        producers = {o: n for n in graph.node for o in n.output}
        uses = collections.Counter(i for n in graph.node for i in n.input if i)
        pair = None
        for t2 in graph.node:
            if t2.op_type != "Transpose" or t2.output[0] in graph_outputs:
                continue
            t1 = producers.get(t2.input[0])
            if t1 is None or t1.op_type != "Transpose":
                continue
            p1 = _attrs(t1).get("perm")
            p2 = _attrs(t2).get("perm")
            if not p1 or not p2 or len(p1) != len(p2):
                continue
            if [p1[j] for j in p2] == list(range(len(p1))):
                pair = (t1, t2)
                break
        if pair is None:
            return removed
        t1, t2 = pair
        source, target = t1.input[0], t2.output[0]
        drop_t1 = uses[t1.output[0]] == 1 and t1.output[0] not in graph_outputs
        kept = []
        for n in graph.node:
            if n is t2 or (drop_t1 and n is t1):
                continue
            for k, name in enumerate(n.input):
                if name == target:
                    n.input[k] = source
            kept.append(n)
        removed += 1 + int(drop_t1)
        _replace_nodes(graph, kept)


# ---------------------------------------------------------------------------------------------------- constant folds


def _onnx_slice(data: np.ndarray, starts, ends, axes, steps) -> np.ndarray:
    axes = list(range(len(starts))) if axes is None else [a + data.ndim if a < 0 else a for a in axes]
    steps = [1] * len(starts) if steps is None else steps
    index = [slice(None)] * data.ndim
    for s, e, a, st in zip(starts, ends, axes, steps):
        index[a] = slice(int(s), int(min(max(e, -(2**62)), 2**62)), int(st))
    return data[tuple(index)]


def fold_constant_slices(graph: onnx.GraphProto, store: WeightStore, min_bytes: int = 1 << 20) -> list[dict]:
    """Replaces a Slice of a large constant table by the rows it reads (Nemotron's rel-pos table, B12g)."""
    folded, kept = [], []
    for n in graph.node:
        if n.op_type == "Slice" and n.input[0] in store and store.nbytes(n.input[0]) >= min_bytes \
                and all((not i) or i in store for i in n.input):
            inputs = list(n.input) + [""] * (5 - len(n.input))
            starts, ends, axes, steps = (store.array(i).tolist() if i else None for i in inputs[1:5])
            value = np.ascontiguousarray(_onnx_slice(store.array(n.input[0]), starts, ends, axes, steps))
            store.add(n.output[0], value)
            folded.append({"node": n.name, "table": n.input[0], "table_shape": list(store.shape(n.input[0])),
                           "table_bytes": store.nbytes(n.input[0]), "kept_shape": list(value.shape),
                           "kept_bytes": int(value.nbytes)})
            continue
        kept.append(n)
    if folded:
        _replace_nodes(graph, kept)
    return folded


def _run_single_node(node: onnx.NodeProto, store: WeightStore, opsets, out_type=TensorProto.FLOAT) -> np.ndarray:
    """Runs one node whose inputs are all constants with the ORT CPU kernel the encoder itself uses at run time."""
    inits = [helper.make_tensor(i, store.tensor_type(i), store.shape(i), store.raw_bytes(i), raw=True)
             for i in node.input if i]
    out = helper.make_tensor_value_info(node.output[0], out_type, None)
    g = helper.make_graph([node], "fold", [], [out], initializer=inits)
    m = helper.make_model(g, opset_imports=list(opsets), ir_version=10)
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    so.intra_op_num_threads = 1
    sess = ort.InferenceSession(m.SerializeToString(), so, providers=["CPUExecutionProvider"])
    return sess.run(None, {})[0]


def fold_constant_projections(graph: onnx.GraphProto, store: WeightStore, opsets) -> list[dict]:
    """Folds MatMul / MatMulNBits nodes whose activation input is a constant (Nemotron's 24 `linear_pos`
    projections of the folded position table).

    ORT folds the Slice at session creation but NOT the MatMulNBits after it, so every chunk recomputes
    24 x [rows x 1024] x [1024 x 1024]. A MatMulNBits is evaluated with the same ORT CPU kernel (bit-identical to what
    the encoder computed at run time), a plain FP32 MatMul (FP32 source) in float64 and rounded to float32.
    """
    folded, kept = [], []
    for n in graph.node:
        constant_a = n.input and n.input[0] in store
        if n.op_type == "MatMulNBits" and constant_a and all((not i) or i in store for i in n.input):
            value = _run_single_node(n, store, opsets)
        elif n.op_type == "MatMul" and constant_a and n.input[1] in store:
            value = np.matmul(store.array(n.input[0]).astype(np.float64),
                              store.array(n.input[1]).astype(np.float64)).astype(np.float32)
        else:
            kept.append(n)
            continue
        store.add(n.output[0], np.ascontiguousarray(value.astype(np.float32)))
        folded.append({"node": n.name, "op": n.op_type, "shape": list(value.shape),
                       "weight_bytes": sum(store.nbytes(i) for i in n.input[1:] if i and i in store),
                       "folded_bytes": int(value.astype(np.float32).nbytes)})
    if folded:
        _replace_nodes(graph, kept)
    return folded
