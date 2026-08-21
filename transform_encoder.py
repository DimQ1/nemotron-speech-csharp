"""Convert all bool paths in Nemotron encoder to float for WebGPU graph capture.
Strips all bool value_info entries after transformation.

Usage: python transform_encoder.py [int4|int8]
  int4 — modules/asr/webgpu-int4 → webgpu-graph-int4 (default)
  int8 — modules/asr/webgpu-int8 → webgpu-graph-int8
"""
import onnx
import sys
from onnx import helper, TensorProto
from pathlib import Path

variant = sys.argv[1] if len(sys.argv) > 1 else 'int4'
assert variant in ('int4', 'int8'), f"Unknown variant: {variant}"

SRC = Path(f'modules/asr/webgpu-{variant}/encoder.onnx')
DST_DIR = Path(f'modules/asr/webgpu-graph-{variant}')
DST_DIR.mkdir(parents=True, exist_ok=True)
print(f"Source: {SRC}")
print(f"Target: {DST_DIR}")

model = onnx.load(str(SRC))

def find_node(name):
    for n in model.graph.node:
        if n.name == name:
            return n
    raise ValueError(f"Node '{name}' not found")

def node_idx(node):
    return list(model.graph.node).index(node)

def insert_after(after_name, new_node):
    idx = node_idx(find_node(after_name))
    model.graph.node.insert(idx + 1, new_node)

def reconnect(old_out, new_out, skip_node_name):
    for n in model.graph.node:
        if n.name == skip_node_name:
            continue
        for i, inp in enumerate(n.input):
            if inp == old_out:
                n.input[i] = new_out

def delete_by_name(name):
    model.graph.node.remove(find_node(name))

# Add constant 1.0
if not any(i.name == 'wgpu_const_one_f32' for i in model.graph.initializer):
    model.graph.initializer.append(
        helper.make_tensor('wgpu_const_one_f32', TensorProto.FLOAT, [1], [1.0]))

# ================================================================
# PHASE 1: Less[6,21,39,59] — Cast(bool→float) + delete stale Casts
# ================================================================
for less_name, stale_cast_name in [
    ('node_lt',   'node__to_copy_4'),
    ('node_lt_1', 'node__to_copy_6'),
    ('node_lt_2', 'node__to_copy_8'),
    ('node_lt_3', 'node__to_copy_10'),
]:
    less_out = find_node(less_name).output[0]
    cast_name = 'wgpu_cast_' + less_out[:30] + '_f32'
    cast_node = helper.make_node('Cast', [less_out], [less_out + '_f32'],
                                 to=TensorProto.FLOAT, name=cast_name)
    insert_after(less_name, cast_node)
    reconnect(less_out, less_out + '_f32', skip_node_name=cast_name)
    
    c = find_node(stale_cast_name)
    print(f"Phase1: [{less_name}] +Cast, -Cast '{stale_cast_name}'")
    reconnect(c.output[0], c.input[0], skip_node_name='')
    delete_by_name(stale_cast_name)

# ================================================================
# PHASE 2: Less[82] + GreaterOrEqual[84] — Cast(bool→float)
# ================================================================

# Less[82]
less82_out = find_node('node_lt_4').output[0]
cast82_name = 'wgpu_cast_lt_4_f32'
insert_after('node_lt_4', helper.make_node(
    'Cast', [less82_out], [less82_out + '_f32'],
    to=TensorProto.FLOAT, name=cast82_name))
reconnect(less82_out, less82_out + '_f32', skip_node_name=cast82_name)
print("Phase2: Less[82] -> Cast(float)")

# GE[84]
ge84_out = find_node('node_ge_1').output[0]
cast84_name = 'wgpu_cast_ge_1_f32'
insert_after('node_ge_1', helper.make_node(
    'Cast', [ge84_out], [ge84_out + '_f32'],
    to=TensorProto.FLOAT, name=cast84_name))
reconnect(ge84_out, ge84_out + '_f32', skip_node_name=cast84_name)
print("Phase2: GE[84] -> Cast(float)")

# ================================================================
# PHASE 3: And[85,90,91] -> Mul
# ================================================================

for and_name, insert_after_name, new_name in [
    ('node_logical_and_2', 'wgpu_cast_ge_1_f32',   'wgpu_mul_logical_and_2'),
    ('node_logical_and_3', 'node_transpose_5',      'wgpu_mul_logical_and_3'),
    ('node_logical_and_4', 'wgpu_mul_logical_and_3', 'wgpu_mul_logical_and_4'),
]:
    n = find_node(and_name)
    out = n.output[0]
    model.graph.node.remove(n)
    insert_after(insert_after_name, helper.make_node(
        'Mul', [n.input[0], n.input[1]], [out],
        name=new_name))
    print(f"Phase3: And '{and_name}' -> Mul ({new_name})")

# ================================================================
# PHASE 4: Not[92,93] -> Sub(1.0, x)
# ================================================================

for not_name, insert_after_name, new_name in [
    ('node_bitwise_not',   'wgpu_mul_logical_and_4', 'wgpu_sub_bitwise_not'),
    ('node_bitwise_not_1', 'wgpu_mul_logical_and_2', 'wgpu_sub_bitwise_not_1'),
]:
    n = find_node(not_name)
    out = n.output[0]
    model.graph.node.remove(n)
    insert_after(insert_after_name, helper.make_node(
        'Sub', ['wgpu_const_one_f32', n.input[0]], [out],
        name=new_name))
    print(f"Phase4: Not '{not_name}' -> Sub(1.0, x) ({new_name})")

# ================================================================
# PHASE 5: Convert bool initializers to float32
# ================================================================
import numpy as np
from onnx.numpy_helper import to_array, from_array

for init in model.graph.initializer:
    if init.data_type == TensorProto.BOOL:
        arr = to_array(init).astype(np.float32)
        new_init = from_array(arr, init.name)
        model.graph.initializer.remove(init)
        model.graph.initializer.append(new_init)
        print(f"Phase5: Initializer '{init.name}' bool->float32 shape={list(arr.shape)}")

# ================================================================
# PHASE 5b: Add Cast(float→bool) for unsqueeze_24/25 feeding Where nodes
# Where(condition, x, y) requires bool condition, but our chain is now float.
# ================================================================
for unsq_name in ['node_unsqueeze_24', 'node_unsqueeze_25']:
    unsq = find_node(unsq_name)
    float_out = unsq.output[0]  # e.g. 'unsqueeze_24'
    bool_out = float_out + '_bool'
    cast_f2b = helper.make_node('Cast', [float_out], [bool_out],
                                to=TensorProto.BOOL,
                                name='wgpu_cast_f2b_' + float_out[:30])
    insert_after(unsq_name, cast_f2b)
    # Reconnect ONLY Where consumers to the bool output
    for n in model.graph.node:
        if n.op_type == 'Where' and n.input[0] == float_out:
            n.input[0] = bool_out
    print(f"Phase5b: Cast(float→bool) for '{float_out}' -> {bool_out}")

    # Also remove stale bool value_info for the float output
    to_del = [vi for vi in model.graph.value_info if vi.name == float_out]
    for vi in to_del:
        model.graph.value_info.remove(vi)

# ================================================================
# PHASE 6: Strip ALL bool (type=9) value_info entries
# These are now stale because the producing nodes output float.
# ================================================================
to_remove = []
for vi in model.graph.value_info:
    if vi.type.tensor_type and vi.type.tensor_type.elem_type == 9:  # bool
        to_remove.append(vi)
for vi in to_remove:
    model.graph.value_info.remove(vi)
print(f"\nPhase6: Removed {len(to_remove)} bool value_info entries")

# Also strip bool type from graph outputs if any (unlikely but safe)
for o in model.graph.output:
    if o.type.tensor_type and o.type.tensor_type.elem_type == 9:
        print(f"WARNING: Graph output '{o.name}' has bool type — clearing")
        o.type.tensor_type.elem_type = 0  # undefined

# ================================================================
# VERIFY
# ================================================================
print()
not_count = sum(1 for n in model.graph.node if n.op_type == 'Not')
and_count = sum(1 for n in model.graph.node if n.op_type == 'And')
bool_vi = sum(1 for vi in model.graph.value_info
              if vi.type.tensor_type and vi.type.tensor_type.elem_type == 9)
print(f"Remaining: Not={not_count} And={and_count} BoolVI={bool_vi}")
assert not_count == 0 and and_count == 0 and bool_vi == 0, "FAIL!"

try:
    onnx.checker.check_model(model)
    print("✓ ONNX checker passed")
except Exception as e:
    print(f"✗ ONNX FAIL: {e}")
    raise

out_path = DST_DIR / 'encoder.onnx'
# Clean old data
for old in DST_DIR.glob('encoder.onnx*'):
    old.unlink()
onnx.save_model(model, str(out_path), save_as_external_data=True,
                all_tensors_to_one_file=True, location='encoder.onnx.data')
sz = (DST_DIR / 'encoder.onnx.data').stat().st_size / 1e6
print(f"✓ Saved: {out_path} ({sz:.1f} MB)")
